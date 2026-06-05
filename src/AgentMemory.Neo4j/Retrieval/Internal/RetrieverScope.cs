namespace AgentMemory.Neo4j.Retrieval.Internal;

/// <summary>
/// Shared owner-scoping helpers for GraphRAG retrievers (R1). When an owner id is supplied, results
/// are restricted to that owner's nodes plus shared/global nodes (<c>owner_id IS NULL</c>). The clause
/// is emitted as its own Cypher line so it is a no-op blank line when unscoped — keeping the
/// unscoped query byte-for-byte today's behavior. Nodes without an <c>owner_id</c> property (e.g. a
/// GraphRAG index over Message nodes) are treated as shared, so scoping is additive and never errors.
/// </summary>
internal static class RetrieverScope
{
    /// <summary>Vector-index over-fetch when scoped, mirroring the long-term repositories.</summary>
    private const int OverFetchFactor = 5;
    private const int OverFetchFloor = 50;

    public static bool IsScoped(string? ownerId) => !string.IsNullOrEmpty(ownerId);

    /// <summary>The owner/shared WHERE clause for the given node alias, or empty when unscoped.</summary>
    public static string OwnerWhere(string alias, bool scoped) =>
        scoped ? $"WHERE ({alias}.owner_id = $owner_id OR {alias}.owner_id IS NULL)" : string.Empty;

    /// <summary>Over-fetch candidate count so the owner filter is not starved by foreign rows.</summary>
    public static int FetchK(int topK, bool scoped) =>
        scoped ? Math.Max(topK * OverFetchFactor, topK + OverFetchFloor) : topK;
}
