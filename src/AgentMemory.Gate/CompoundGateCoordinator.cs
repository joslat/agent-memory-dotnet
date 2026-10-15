using System.Diagnostics;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Gate;

/// <summary>Adds bounded, same-pool part admissions while retaining the completed baseline decision.</summary>
internal static class CompoundGateCoordinator
{
    internal const int MaxParts = 3;
    internal const int MaxPartCharacters = 512;
    internal const int MaxQuestionCharacters = 4096;
    internal const int MaxConversationCharacters = 4096;
    internal const int MaxCandidates = 300;
    internal const int MaxCandidateCharacters = 65536;

    public static async Task<(MemoryGateDecision Decision, CompoundQuestionTrace Trace)> ExpandAsync(
        MemoryGateRequest request, MemoryGateDecision baseline, IMemoryGate gate,
        ICompoundQuestionPlanner? planner, CancellationToken deadline, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var watch = Stopwatch.StartNew();
        var planned = 0;
        var completed = 0;
        var calls = 0;
        var scores = new Dictionary<string, double>(baseline.Probabilities, StringComparer.Ordinal);
        var answered = new HashSet<string>(baseline.AnsweredBy.Split('+', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

        (MemoryGateDecision, CompoundQuestionTrace) Finish(string outcome, string? reason = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return (completed == 0 ? baseline : new MemoryGateDecision(scores, string.Join("+", answered.Order(StringComparer.Ordinal))),
                new CompoundQuestionTrace
                {
                    Outcome = outcome, Reason = reason, PlannedParts = planned, CompletedParts = completed,
                    AdditionalGateDecisions = calls, ElapsedMilliseconds = watch.ElapsedMilliseconds,
                });
        }

        if (planner is null) return Finish("baseline", "planner-unavailable");
        if (request.Turn.Length > MaxQuestionCharacters ||
            request.Conversation.Sum(t => (long)t.Text.Length) > MaxConversationCharacters ||
            request.Candidates.Count > MaxCandidates ||
            request.Candidates.Sum(c => (long)c.Text.Length) > MaxCandidateCharacters)
            return Finish("baseline", "input-limit");

        string? failure = null;
        try
        {
            deadline.ThrowIfCancellationRequested();
            var parts = await planner.PlanAsync(new CompoundQuestionRequest
            {
                Question = request.Turn, Conversation = request.Conversation, NowUtc = request.Now,
            }, deadline).WaitAsync(deadline).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            deadline.ThrowIfCancellationRequested();
            if (parts is { Count: 0 }) return Finish("not-needed");
            if (!ValidParts(parts)) return Finish("baseline", "invalid-plan");
            planned = parts.Count;
            var keys = request.Candidates.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var part in parts)
            {
                deadline.ThrowIfCancellationRequested();
                try
                {
                    calls++;
                    var focused = request with
                    {
                        Turn = $"Original question (all constraints still apply): {request.Turn}\n"
                            + $"Retrieval focus for this part of the reply: {part.Trim()}",
                    };
                    var decision = await gate.DecideAsync(focused, deadline).WaitAsync(deadline).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    deadline.ThrowIfCancellationRequested();
                    if (decision.Probabilities.Count != keys.Count || decision.Probabilities.Any(p =>
                            !keys.Contains(p.Key) || !double.IsFinite(p.Value) || p.Value is < 0 or > 1))
                    {
                        failure = "invalid-part";
                        continue;
                    }
                    foreach (var (key, score) in decision.Probabilities)
                    {
                        if (!scores.TryGetValue(key, out var previous) || !(previous >= score)) scores[key] = score;
                    }
                    answered.UnionWith(decision.AnsweredBy.Split('+', StringSplitOptions.RemoveEmptyEntries));
                    completed++;
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested || cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    failure = "part-failed";
                }
            }
            return Finish(completed == planned ? "split" : completed > 0 ? "partial" : "baseline", failure);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Finish(completed > 0 ? "partial" : "baseline", "timeout");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return Finish(completed > 0 ? "partial" : "baseline", "planner-failed");
        }
    }

    internal static bool ValidParts(IReadOnlyList<string>? parts) => parts is { Count: >= 2 and <= MaxParts } &&
        parts.All(p => !string.IsNullOrWhiteSpace(p) && p.Length <= MaxPartCharacters) &&
        parts.Select(p => p.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() == parts.Count;
}
