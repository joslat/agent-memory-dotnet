using AgentMemory.Abstractions.Options;

namespace AgentMemory.Analytics;

/// <summary>An entity ranked by graph importance — its PageRank score over the <c>RELATED_TO</c> graph.</summary>
public sealed record EntityRank(string EntityId, double Score);

/// <summary>An entity's detected community (topic cluster) id from Louvain community detection.</summary>
public sealed record EntityCommunity(string EntityId, long CommunityId);

/// <summary>Options for the optional GDS analytics services.</summary>
public sealed class GdsAnalyticsOptions
{
    /// <summary>Default number of top-ranked entities returned by PageRank when a caller doesn't specify. Default 20.</summary>
    public int DefaultTopN { get; set; } = 20;
}

/// <summary>
/// Probes whether the Neo4j Graph Data Science (GDS) plugin is installed, so the analytics services can
/// degrade to a graceful no-op when it is absent (GDS is not bundled with Neo4j Community Edition).
/// </summary>
public interface IGdsAvailability
{
    /// <summary>True if GDS procedures are callable on the configured Neo4j instance. Result is memoized.</summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Surfaces the most graph-important entities via GDS PageRank over the (owner-scoped) <c>RELATED_TO</c>
/// graph. Returns an empty list when GDS is not installed.
/// </summary>
public interface IMemoryPageRankService
{
    /// <summary>
    /// Ranks entities by PageRank, highest first. <paramref name="scope"/> confines the projected graph to
    /// the owner's (plus shared) entities (R1). Returns empty if GDS is unavailable.
    /// </summary>
    Task<IReadOnlyList<EntityRank>> RankEntitiesAsync(int? topN = null, MemoryScope? scope = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Detects entity communities (topic clusters) via GDS Louvain over the (owner-scoped) <c>RELATED_TO</c>
/// graph. Returns an empty list when GDS is not installed.
/// </summary>
public interface IMemoryCommunityService
{
    /// <summary>
    /// Assigns each entity a community id (entities in the same cluster share an id).
    /// <paramref name="scope"/> confines the projected graph to the owner's (plus shared) entities (R1).
    /// Returns empty if GDS is unavailable.
    /// </summary>
    Task<IReadOnlyList<EntityCommunity>> DetectCommunitiesAsync(MemoryScope? scope = null, CancellationToken cancellationToken = default);
}
