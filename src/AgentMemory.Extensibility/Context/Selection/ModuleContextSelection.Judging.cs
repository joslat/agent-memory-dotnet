using System.Text;
using AgentMemory.Abstractions.Services;
using Microsoft.Extensions.AI;

namespace AgentMemory.Extensibility.Context;

public sealed partial class ModuleContextSelection
{
    private async Task<(ModuleJudgeOutcome Outcome, string? AnsweredBy)> JudgeAsync(ContextEnvelope envelope, List<Entry> entries,
        CancellationToken cancellationToken)
    {
        if (!_options.UseJudge) return (ModuleJudgeOutcome.Disabled, null);
        var offered = new List<Entry>(); var bytes = 0;
        foreach (var entry in entries.Where(e => e.Disposition is null))
        {
            var size = Encoding.UTF8.GetByteCount(entry.Candidate.Text);
            if (size > _options.MaxJudgeUtf8Bytes - bytes) entry.Disposition = ModuleCandidateDisposition.JudgeByteLimit;
            else { offered.Add(entry); bytes += size; }
        }
        if (offered.Count == 0) return (ModuleJudgeOutcome.NoCandidates, null);
        MemoryGateDecision? decision;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var gate = _gateFactory?.Invoke();
            if (gate is null) return (ModuleJudgeOutcome.Unavailable, null);
            var recent = envelope.Request.RecentTurn;
            var lastUser = -1;
            for (var i = 0; i < recent.Count; i++)
                if (recent[i].Role == ChatRole.User && !string.IsNullOrWhiteSpace(recent[i].Text)) lastUser = i;
            var conversation = recent.Take(lastUser < 0 ? recent.Count : lastUser)
                .Where(m => m.Role == ChatRole.User || m.Role == ChatRole.Assistant).TakeLast(_options.MaxConversationMessages)
                .Select(m => new MemoryGateTurn(m.Role == ChatRole.User ? "user" : "assistant", m.Text ?? string.Empty)).ToArray();
            var request = new MemoryGateRequest(lastUser < 0 ? envelope.Request.Query ?? string.Empty : recent[lastUser].Text!,
                Array.AsReadOnly(conversation), envelope.CompiledAt, offered.Select(e => new MemoryGateCandidate(
                    e.Candidate.Key, e.Candidate.Binding.ModuleId, e.Candidate.Text)).ToList().AsReadOnly());
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var entry in offered) entry.OfferedToJudge = true;
            decision = await gate.DecideAsync(request, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return (ModuleJudgeOutcome.Unavailable, null); }
        Dictionary<string, double> scores;
        try
        {
            if (decision?.Probabilities is null) return (ModuleJudgeOutcome.InvalidDecision, null);
            scores = decision.Probabilities.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            if (scores.Count != offered.Count || offered.Any(e => !scores.TryGetValue(e.Candidate.Key, out var score)
                    || !double.IsFinite(score) || score is < 0 or > 1)) return (ModuleJudgeOutcome.InvalidDecision, null);
        }
        catch { return (ModuleJudgeOutcome.InvalidDecision, null); }
        foreach (var entry in offered)
        {
            entry.Score = scores[entry.Candidate.Key];
            if (entry.Score < _options.Threshold) entry.Disposition = ModuleCandidateDisposition.JudgeRejected;
        }
        return (ModuleJudgeOutcome.Applied, decision.AnsweredBy);
    }
}
