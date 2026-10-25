using AgentMemory.Abstractions.Services;

namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// Memories from outside core recall, such as module views, that the retrieval gate judges in the same call as the core
/// memories it found (Extensibility 0.12, R1). The gate's probabilities for them come back on
/// <see cref="MemoryContext.CompanionDecisions"/>.
/// </summary>
/// <remarks>
/// <para>
/// The candidates may still be gathered while core recall searches: a gate awaits them after its own search, for at
/// most its timeout, before its judge's own timeout starts. The two searches run side by side and are judged once, and
/// a late module search costs its own items, never the core judgement. Their keys must be unique and must not collide
/// with core memories; their <see cref="MemoryGateCandidate.MemoryType"/> names their source (a module id). Their text
/// must already have passed the host's admission policy, since the gate sends it to a judge.
/// </para>
/// <para>
/// Only a judging gate reads them. Without one, or when the gate takes the similarity floor or fails, they are not
/// judged and <see cref="MemoryContext.CompanionDecisions"/> stays null: the caller owns them and applies its own bounded
/// fallback. A companion task that fails or misses the gate's timeout leaves the core judgement unaffected.
/// </para>
/// </remarks>
public sealed class RecallCompanions
{
    /// <summary>Wraps the companions, gathered or still being gathered.</summary>
    public RecallCompanions(Task<IReadOnlyList<MemoryGateCandidate>> candidates) =>
        Candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));

    /// <summary>The companion candidates, possibly still being gathered.</summary>
    public Task<IReadOnlyList<MemoryGateCandidate>> Candidates { get; }

    /// <summary><see cref="MemoryContext.Metadata"/> key: how many companions a gate call carried, decided or not.</summary>
    public const string OfferedMetadataKey = "gate.companionsOffered";

    /// <summary><see cref="MemoryContext.Metadata"/> key: which judges answered the gate's call.</summary>
    public const string JudgedByMetadataKey = "gate.judgedBy";
}
