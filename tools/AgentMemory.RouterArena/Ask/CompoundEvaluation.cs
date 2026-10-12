using System.Diagnostics;
using AgentMemory.Abstractions.Services;
using AgentMemory.Gate;

namespace AgentMemory.RouterArena.Ask;

internal sealed record CompoundCase
{
    public required string Id { get; init; }
    public required string Question { get; init; }
    public IReadOnlyList<MemoryGateTurn> Conversation { get; init; } = [];
    public IReadOnlyList<MemoryGateCandidate> Candidates { get; init; } = [];
}

internal sealed record CompoundEvaluationRow
{
    public required string Id { get; init; }
    public IReadOnlyList<string> Kept { get; init; } = [];
    public double ElapsedMs { get; init; }
    public long Calls { get; init; }
    public long FailedRequests { get; init; }
    public long JudgeFailures { get; init; }
    public string? AnsweredBy { get; init; }
    public long PendingRequests { get; init; }
    public long TokenReportedResponses { get; init; }
    public long? ReportedInputTokens { get; init; }
    public long? ReportedOutputTokens { get; init; }
    public bool Fallback { get; init; }
    public string? Error { get; init; }
    public CompoundQuestionTrace? Compound { get; init; }
}

internal sealed record CompoundRequestCounts(long Attempts, long Failed, long Pending,
    long TokenReportedResponses, long? InputTokens, long? OutputTokens)
{
    public static CompoundRequestCounts Empty { get; } = new(0, 0, 0, 0, null, null);
    public long JudgeFailures { get; init; }
}

internal static class CompoundEvaluation
{
    public static async Task<(CompoundEvaluationRow Baseline, CompoundEvaluationRow Compound)> RunCaseAsync(
        CompoundCase item, DateTimeOffset now, MemoryGateOptions options, IMemoryGate gate,
        ICompoundQuestionPlanner planner, Func<CompoundRequestCounts> counts,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var watch = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.Timeout);
        var request = new MemoryGateRequest(item.Question, item.Conversation, now, item.Candidates);
        CompoundEvaluationRow Row(MemoryGateDecision? decision, string? error = null, CompoundQuestionTrace? trace = null)
        {
            var measured = counts();
            error ??= measured.JudgeFailures > 0 ? "judge-failure" : null;
            return new CompoundEvaluationRow
            {
                Id = item.Id,
                Kept = decision is null ? [] : [.. item.Candidates.Where(c =>
                    decision.Probabilities.TryGetValue(c.Key, out var score) && score >= options.Threshold).Select(c => c.Key)],
                ElapsedMs = watch.Elapsed.TotalMilliseconds, Calls = measured.Attempts,
                FailedRequests = measured.Failed, PendingRequests = measured.Pending,
                JudgeFailures = measured.JudgeFailures, AnsweredBy = decision?.AnsweredBy,
                TokenReportedResponses = measured.TokenReportedResponses,
                ReportedInputTokens = measured.InputTokens, ReportedOutputTokens = measured.OutputTokens,
                Error = error, Fallback = error is not null || trace?.Reason is not null, Compound = trace,
            };
        }

        MemoryGateDecision baseline;
        try
        {
            if (item.Candidates.Count == 0)
            {
                var empty = Row(null);
                return (empty, empty);
            }
            baseline = await gate.DecideAsync(request, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var keys = item.Candidates.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
            if (baseline.Probabilities.Count != keys.Count || baseline.Probabilities.Any(p =>
                    !keys.Contains(p.Key) || !double.IsFinite(p.Value) || p.Value is < 0 or > 1))
            {
                var invalid = Row(null, "baseline-invalid-decision");
                return (invalid, invalid);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // A fixed-pool test has no second store search from which to simulate the floor.
            // Keep the failed case in both arms and never turn it into successful empty retrieval.
            var failed = Row(null, "baseline-" + ex.GetType().Name);
            return (failed, failed);
        }

        var baselineRow = Row(baseline);
        if (baselineRow.Error is not null) return (baselineRow, baselineRow);
        var (decision, trace) = await CompoundGateCoordinator.ExpandAsync(request, baseline, gate, planner,
            deadline.Token, cancellationToken).ConfigureAwait(false);
        var error = trace.Reason is null or "input-limit" ? null : trace.Reason;
        return (baselineRow, Row(decision, error, trace));
    }
}
