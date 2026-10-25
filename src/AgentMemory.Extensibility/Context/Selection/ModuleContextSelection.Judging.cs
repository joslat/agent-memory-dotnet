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
        var offered = Offer(entries);
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
        if (decision?.Probabilities is null) return (ModuleJudgeOutcome.InvalidDecision, null);
        return Apply(offered, decision.Probabilities, exactKeys: true) ? (ModuleJudgeOutcome.Applied, decision.AnsweredBy)
            : (ModuleJudgeOutcome.InvalidDecision, null);
    }

    /// <summary>The eligible entries a judge is shown, within the judge's byte allowance; the rest are omitted for it.</summary>
    private List<Entry> Offer(List<Entry> entries)
    {
        var offered = new List<Entry>(); var bytes = 0;
        foreach (var entry in entries.Where(e => e.Disposition is null))
        {
            var size = Encoding.UTF8.GetByteCount(entry.Candidate.Text);
            if (size > _options.MaxJudgeUtf8Bytes - bytes) entry.Disposition = ModuleCandidateDisposition.JudgeByteLimit;
            else { offered.Add(entry); bytes += size; }
        }
        return offered;
    }

    /// <summary>The core gate's decisions about this selection's companions, applied as the module's own judge would be.</summary>
    internal (ModuleJudgeOutcome Outcome, string? AnsweredBy) ApplyCoreDecisions(List<Entry> offered, int companions,
        IReadOnlyDictionary<string, double>? decisions, string? answeredBy, bool offeredToJudge)
    {
        if (!_options.UseJudge) return (ModuleJudgeOutcome.Disabled, null);
        if (companions == 0) return (ModuleJudgeOutcome.NoCandidates, null);
        if (offeredToJudge || decisions is not null)
            foreach (var entry in offered) entry.OfferedToJudge = true;
        if (decisions is null) return (ModuleJudgeOutcome.Unavailable, null);
        // The core gate's answer also covers core memories it judged; only this selection's own keys must be complete.
        return Apply(offered, decisions, exactKeys: false) ? (ModuleJudgeOutcome.Applied, answeredBy) : (ModuleJudgeOutcome.InvalidDecision, null);
    }

    /// <summary>Validates one finite [0,1] score for every offered key, then applies the threshold; nothing changes when invalid.</summary>
    private bool Apply(List<Entry> offered, IReadOnlyDictionary<string, double> probabilities, bool exactKeys)
    {
        try
        {
            if (exactKeys && probabilities.Count != offered.Count) return false;
            if (offered.Any(e => !probabilities.TryGetValue(e.Candidate.Key, out var score) || !double.IsFinite(score) || score is < 0 or > 1))
                return false;
        }
        catch { return false; }
        foreach (var entry in offered)
        {
            entry.Score = probabilities[entry.Candidate.Key];
            if (entry.Score < _options.Threshold) entry.Disposition = ModuleCandidateDisposition.JudgeRejected;
        }
        return true;
    }
}
