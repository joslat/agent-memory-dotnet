using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Abstractions.Repositories;

/// <summary>
/// Persistence for synthesized entity summaries (S1).
/// </summary>
/// <remarks>
/// One summary per entity per owner. There is no history: a summary is derived, so an old one is not
/// a record of what was believed — it is a record of what a synthesizer produced from facts the store
/// still has, and those facts carry their own bitemporal history already.
/// </remarks>
public interface IEntitySummaryRepository
{
    /// <summary>Stores a summary, replacing the entity's existing one.</summary>
    Task<EntitySummary> UpsertAsync(EntitySummary summary, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the entity's summary, if any. Owner-confined (R1).
    /// </summary>
    /// <remarks>
    /// Returns whatever is stored, <b>without</b> judging freshness: staleness is decided against the
    /// live facts by the caller that has them, and a repository silently withholding a stale row
    /// would make "no summary" and "outdated summary" indistinguishable to a regeneration pass.
    /// </remarks>
    Task<EntitySummary?> GetByEntityAsync(
        string entityId, MemoryScope scope, CancellationToken cancellationToken = default);

    /// <summary>Removes an entity's summary.</summary>
    Task<bool> DeleteByEntityAsync(
        string entityId, MemoryScope scope, CancellationToken cancellationToken = default);
}
