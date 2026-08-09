using System.Globalization;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.LongMemEval;

public sealed partial class AgentMemoryLongMemEvalAdapter
{
    private async Task<LongMemEvalBatchedPreparationResult> ExecuteBatchedPreparationAsync(
        IReadOnlyList<Message> messages,
        LongMemEvalEvidenceQuestion evidenceQuestion,
        string sessionId,
        string ownerId,
        int questionNumber,
        LongMemEvalStageTimingCollector timings,
        CancellationToken cancellationToken)
    {
        var requests = BuildExtractionRequests(
            messages,
            evidenceQuestion,
            sessionId,
            ownerId);
        var planner = _options.BatchPlanner!;
        var plan = planner.Plan(
            requests,
            _options.MaxSessionsPerBatch,
            _options.MaxInputTokens);
        if (!PlansMatch(plan, _options.ExpectedExtractionPlan!))
        {
            throw new InvalidOperationException(
                $"LongMemEval question {questionNumber} batch plan changed after preflight.");
        }

        _options.ExtractionProgress?.Invoke(0, requests.Count);
        if (_chatClient is not LongMemEvalChatCallMeter callMeter)
        {
            throw new InvalidOperationException(
                "Batched LongMemEval preparation requires scoped provider-call accounting.");
        }

        var callScope = $"prepared-question-{questionNumber:D4}";
        var callsBefore = callMeter.SnapshotScope(callScope);
        IReadOnlyList<ExtractionResult> results;
        using (callMeter.BeginScope(callScope))
        {
            results = await timings.MeasureAsync(
                LongMemEvalStage.ExtractionPersistence,
                () => LongMemEvalRuntime.ExecuteStageAsync(
                    "batched extraction",
                    () => _options.BatchExtractionPipeline!.ExtractBatchAsync(
                        requests,
                        _options.MaxSessionsPerBatch,
                        _options.MaxInputTokens,
                        cancellationToken))).ConfigureAwait(false);
        }

        var callsAfter = callMeter.SnapshotScope(callScope);
        var callDelta = callsAfter.Calls - callsBefore.Calls;
        var failureDelta = callsAfter.Failures - callsBefore.Failures;
        var purposeDelta = callsAfter.Purposes.ToDictionary(
            pair => pair.Key,
            pair => pair.Value - callsBefore.Purposes.GetValueOrDefault(pair.Key),
            StringComparer.Ordinal);
        var unifiedBatchCalls = purposeDelta.GetValueOrDefault("unified_batch");
        var otherCalls = purposeDelta
            .Where(pair => !string.Equals(pair.Key, "unified_batch", StringComparison.Ordinal))
            .Sum(pair => pair.Value);
        if (callDelta != plan.BatchCount ||
            failureDelta != 0 ||
            unifiedBatchCalls != plan.BatchCount ||
            otherCalls != 0)
        {
            // The provider status is what separates "we are being rate limited" from "the request
            // was malformed" or "the service failed", and they need opposite responses: lower
            // concurrency, fix the request, or retry. Without it a 37-minute preparation aborts with
            // an exception type and no way to choose. Status codes carry no content.
            var failureSummary = string.Join(
                ',',
                callsAfter.Failures > callsBefore.Failures
                    ? callMeter.Snapshot().FailureDetails
                        .Select(failure =>
                            $"{failure.Purpose}:{failure.ExceptionType}" +
                            $":status={failure.ProviderStatus?.ToString(CultureInfo.InvariantCulture) ?? "none"}")
                        .Distinct(StringComparer.Ordinal)
                    : []);
            throw new LongMemEvalExtractionAccountingException(
                $"LongMemEval batched extraction accounting mismatch at question {questionNumber}: " +
                $"observed {callDelta} calls, {failureDelta} failures, " +
                $"{unifiedBatchCalls} unified-batch calls, and {otherCalls} other calls; " +
                $"expected exactly {plan.BatchCount} unified-batch calls and zero failures." +
                (failureSummary.Length == 0 ? "" : $" Provider failures: {failureSummary}."));
        }

        var plannedSessions = plan.Batches
            .SelectMany(batch => batch.SourceSessionIds)
            .ToArray();
        var returnedSessions = results
            .Select(result =>
                result.Metadata.TryGetValue("sessionId", out var value)
                    ? value as string
                    : null)
            .ToArray();
        if (results.Count != requests.Count ||
            results.Any(result => result.Status != IngestionStatus.Succeeded) ||
            !returnedSessions.SequenceEqual(plannedSessions, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"LongMemEval question {questionNumber} did not persist every planned source session in chronological order.");
        }

        _options.ExtractionProgress?.Invoke(results.Count, requests.Count);
        return new LongMemEvalBatchedPreparationResult(
            results.Count,
            plan.BatchCount);
    }

    internal static IReadOnlyList<ExtractionRequest> BuildExtractionRequests(
        IReadOnlyList<Message> messages,
        LongMemEvalEvidenceQuestion evidenceQuestion,
        string sessionId,
        string ownerId)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(evidenceQuestion);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        if (messages.Count != evidenceQuestion.Messages.Count)
        {
            throw new InvalidOperationException(
                "LongMemEval extraction messages do not match source provenance.");
        }

        return messages
            .Select((message, index) =>
                (Message: message, Origin: evidenceQuestion.Messages[index]))
            .Where(item =>
                !item.Origin.IsSyntheticBoundary &&
                !item.Origin.IsSyntheticFormatterPadding)
            .GroupBy(item => item.Origin.SourceSessionOrdinal)
            .OrderBy(group => group.Key)
            .Select(group => new ExtractionRequest
            {
                Messages = group.Select(item => item.Message).ToArray(),
                SessionId = $"{sessionId}-source-{group.Key:D4}",
                UserId = ownerId,
                TypesToExtract = ExtractionTypes.All
            })
            .OrderBy(request => request.Messages
                .Select(message => message.TimestampUtc)
                .DefaultIfEmpty(DateTimeOffset.MinValue)
                .Min())
            .ThenBy(request => request.SessionId, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool PlansMatch(
        MultiSessionExtractionPlan left,
        MultiSessionExtractionPlan right) =>
        left.BatchCount == right.BatchCount &&
        left.SourceSessionCount == right.SourceSessionCount &&
        left.TotalEstimatedInputTokens == right.TotalEstimatedInputTokens &&
        left.Batches.Zip(right.Batches).All(pair =>
            pair.First.EstimatedInputTokens == pair.Second.EstimatedInputTokens &&
            pair.First.SourceSessionIds.SequenceEqual(
                pair.Second.SourceSessionIds,
                StringComparer.Ordinal));

    private sealed record LongMemEvalBatchedPreparationResult(
        int ExtractionUnits,
        int PlannedCalls);
}
