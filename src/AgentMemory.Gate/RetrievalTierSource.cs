using AgentMemory.Abstractions.Services;
using Microsoft.Extensions.Options;

namespace AgentMemory.Gate;

/// <summary>
/// Retrieval's tier with the gate added: the judges that would be asked now, or the similarity floor and why (the mode,
/// no judge configured, or every judge left out after repeated failures, <see cref="MemoryGateOptions.JudgeFailuresBeforeCooldown"/>).
/// </summary>
internal sealed class RetrievalTierSource(IOptions<MemoryGateOptions> options, SystemOneClient client) : IMemoryTierSource
{
    public MemoryTier Describe()
    {
        var o = options.Value;
        if (o.Mode == MemoryGateMode.Floor)
            return new MemoryTier(MemoryTiers.Retrieval, "similarity floor");
        if (o.Mode == MemoryGateMode.Everything)
            return new MemoryTier(MemoryTiers.Retrieval, "everything", "every memory found, no cut and no judge");
        var judges = SystemOneMemoryGate.UsableJudges(o).ToList();
        if (judges.Count == 0)
            return new MemoryTier(MemoryTiers.Retrieval, "similarity floor", "Judge mode has no judge configured");
        var outs = judges.Select(j => (Judge: j, Until: client.OutUntil(j))).Where(x => x.Until is not null).ToList();
        var asked = judges.Where(j => outs.All(x => x.Judge != j)).Select(j => j.Name).ToList();
        if (asked.Count == 0)
            return new MemoryTier(MemoryTiers.Retrieval, "similarity floor",
                $"every judge left out after repeated failures, until {outs.Min(x => x.Until)!.Value:HH:mm:ss} UTC");
        return new MemoryTier(MemoryTiers.Retrieval, $"judge ({string.Join("+", asked)})",
            outs.Count == 0 ? null : $"{string.Join("+", outs.Select(x => x.Judge.Name))} left out after repeated failures");
    }
}
