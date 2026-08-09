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
        var retryDelta = callsAfter.RetryCalls - callsBefore.RetryCalls;
        var unifiedBatchCalls = purposeDelta.GetValueOrDefault("unified_batch");
        var otherCalls = purposeDelta
            .Where(pair => !string.Equals(pair.Key, "unified_batch", StringComparison.Ordinal))
            .Sum(pair => pair.Value);
        // The invariant is "every batch produced exactly one SUCCESSFUL unified-batch call", not "no
        // provider error ever occurred". The latter is not something a 614-call run over a network
        // can promise, and requiring it made two n=50 preparations abort mid-run on one transient.
        // A recovered transport retry is exactly one extra call plus one failure, so subtracting
        // failures recovers the successful count without loosening what is actually being checked:
        // an unrecovered failure still throws before reaching here, and a spurious extra call still
        // trips the comparison. Failures are reported rather than required to be zero.
        var successfulCalls = callDelta - failureDelta;
        var successfulUnifiedBatchCalls = unifiedBatchCalls - failureDelta;
        // Excess calls must be EXPLAINED, not absent. The previous equality demanded that nothing
        // ever went wrong, which made the harness incompatible with the recovery paths it ships:
        // a parse retry re-prompts, and a batch split re-sends the halves, and both legitimately
        // add calls. Three consecutive 15-40 minute preparations died on that, the last one on a
        // genuine parse-or-format split doing exactly what it is designed to do.
        //
        // Correctness is not what this checks and never was - the session-set comparison below is,
        // and it is unchanged: every planned source session must be persisted, in chronological
        // order, all succeeded. This is a COST guard, so the invariant it should express is "no
        // unaccounted work": at least the planned calls happened, nothing of an unexpected purpose
        // ran, and any excess is attributable to a recorded split or retry.
        var recordedSplits = _options.BatchSplitCount?.Invoke() ?? 0;
        var excessCalls = successfulUnifiedBatchCalls - plan.BatchCount;
        if (!IsBatchAccountingAcceptable(
                successfulCalls, successfulUnifiedBatchCalls, otherCalls,
                recordedSplits, retryDelta, plan.BatchCount))
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
                $"observed {callDelta} calls ({successfulCalls} successful), {failureDelta} " +
                $"recovered failures, {unifiedBatchCalls} unified-batch calls, and {otherCalls} " +
                $"other calls; expected at least {plan.BatchCount} SUCCESSFUL unified-batch calls, " +
                $"no other calls, and any excess explained by a recorded split or retry " +
                $"(splits={recordedSplits}, retries={retryDelta}, excess={excessCalls})." +
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


    /// <summary>
    /// Whether a question's provider-call accounting is acceptable: no unaccounted work.
    /// </summary>
    /// <remarks>
    /// This is a <b>cost</b> guard, not a correctness one — the session-set comparison that follows
    /// it is what proves every planned source session was persisted, in order, successfully, and that
    /// check is unchanged.
    /// <para>
    /// It previously demanded <i>exactly</i> the planned number of calls and zero failures, which is
    /// to say it demanded that nothing ever went wrong. That made it incompatible with the recovery
    /// paths the extractor ships: a parse retry re-prompts and a batch split re-sends the halves, and
    /// both legitimately add calls. Three consecutive 15–40 minute preparations died on it, the last
    /// on a parse-or-format split doing precisely what it exists to do.
    /// </para>
    /// <para>
    /// The invariant it should express is <b>"every extra call is attributable"</b>: at least the
    /// planned work happened, nothing of an unexpected purpose ran, and any excess coincides with a
    /// recorded split or retry. Excess with no recorded recovery is still rejected — that is the case
    /// worth catching, and it is the one the guard was really for.
    /// </para>
    /// </remarks>
    internal static bool IsBatchAccountingAcceptable(
        long successfulCalls,
        long successfulUnifiedBatchCalls,
        long otherCalls,
        long recordedSplits,
        long recordedRetries,
        int plannedBatchCount)
    {
        if (otherCalls != 0)
            return false;
        if (successfulCalls < plannedBatchCount || successfulUnifiedBatchCalls < plannedBatchCount)
            return false;

        var excess = successfulUnifiedBatchCalls - plannedBatchCount;
        return excess == 0 || recordedSplits > 0 || recordedRetries > 0;
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
