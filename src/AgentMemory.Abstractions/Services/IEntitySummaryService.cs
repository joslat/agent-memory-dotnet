using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Writes entity summaries and withholds any that can no longer prove it is current (S1).
/// </summary>
/// <remarks>
/// A summary is derived memory, which is where a store quietly starts lying: the sources change, the
/// summary does not, and nothing about it looks any different afterwards. Every read here is checked
/// against the live facts before it is handed back.
/// </remarks>
public interface IEntitySummaryService
{
    /// <summary>
    /// Synthesizes and stores a summary for the entity, replacing any existing one.
    /// </summary>
    /// <returns>The stored summary, or <see langword="null"/> when there was nothing to summarise.</returns>
    Task<EntitySummary?> RefreshAsync(
        Entity entity, MemoryScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the entity's summary only if it still describes the current facts.
    /// </summary>
    /// <returns>
    /// The summary when its fingerprint still matches the store; <see langword="null"/> when there is
    /// none <b>or</b> when the one on record has gone stale.
    /// </returns>
    /// <remarks>
    /// Null for stale rather than a summary carrying an <c>IsStale</c> flag. A flag puts the decision
    /// in every caller's hands, and one caller forgetting to check it is indistinguishable from
    /// correct memory.
    /// </remarks>
    Task<EntitySummary?> GetIfCurrentAsync(
        Entity entity, MemoryScope scope, CancellationToken cancellationToken = default);
}
