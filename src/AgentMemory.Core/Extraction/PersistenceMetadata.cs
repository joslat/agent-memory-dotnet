using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Repositories;

namespace AgentMemory.Core.Extraction;

/// <summary>Shared trust and provenance rules for typed persistence.</summary>
internal static class PersistenceMetadata
{
    /// <summary>
    /// Trust is monotonic (#92 Phase 3): re-touching an already-persisted entity must never silently lower
    /// its trust level below whatever it already had.
    /// </summary>
    internal static MemoryTrustLevel MaxTrustLevel(MemoryTrustLevel a, MemoryTrustLevel b) => a > b ? a : b;

    internal static IEnumerable<string> ExplicitProvenanceMessageIds(
        object repository, IReadOnlyList<string> sourceMessageIds) =>
        repository is IUpsertPersistsProvenance ? Array.Empty<string>() : sourceMessageIds;
}
