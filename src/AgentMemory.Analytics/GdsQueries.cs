namespace AgentMemory.Analytics;

/// <summary>
/// Cypher for the optional Neo4j GDS analytics. Algorithms run over a transient, owner-filtered Cypher
/// projection of the live <c>Entity</c> / <c>RELATED_TO</c> subgraph. All node/relationship queries and the
/// owner id are passed as parameters; only the projection <i>config</i> (a literal map) is interpolated.
/// </summary>
internal static class GdsQueries
{
    /// <summary>Returns the installed GDS version, or fails if the plugin is absent (used for availability probing).</summary>
    public const string ProbeVersion = "RETURN gds.version() AS version";

    public const string PageRankStream =
        "CALL gds.pageRank.stream($graphName) YIELD nodeId, score " +
        "RETURN gds.util.asNode(nodeId).id AS entityId, score ORDER BY score DESC LIMIT $topN";

    public const string LouvainStream =
        "CALL gds.louvain.stream($graphName) YIELD nodeId, communityId " +
        "RETURN gds.util.asNode(nodeId).id AS entityId, communityId";

    public const string DropGraph = "CALL gds.graph.drop($graphName, false) YIELD graphName RETURN graphName";

    /// <summary>
    /// The owner-filtered node + relationship queries for <c>gds.graph.project.cypher</c>. Only <b>live</b>
    /// entities (<c>invalidated_at IS NULL</c>) are projected, and a relationship is included only when
    /// <b>both</b> endpoints are in scope — so the projected graph never crosses the owner boundary (R1).
    /// </summary>
    public static (string NodeQuery, string RelQuery) Projection(bool hasOwnerFilter, bool includeShared)
    {
        if (!hasOwnerFilter)
        {
            return (
                "MATCH (e:Entity) WHERE e.invalidated_at IS NULL RETURN id(e) AS id",
                "MATCH (a:Entity)-[:RELATED_TO]->(b:Entity) RETURN id(a) AS source, id(b) AS target");
        }

        var nf = includeShared ? "(e.owner_id = $ownerId OR e.owner_id IS NULL)" : "e.owner_id = $ownerId";
        var af = includeShared ? "(a.owner_id = $ownerId OR a.owner_id IS NULL)" : "a.owner_id = $ownerId";
        var bf = includeShared ? "(b.owner_id = $ownerId OR b.owner_id IS NULL)" : "b.owner_id = $ownerId";
        // The edge itself carries owner_id; scope it the same way the rest of the library does
        // (RelationshipQueries.OwnerAnd) so a foreign owner's edge between two visible (shared) nodes can't
        // perturb a scoped owner's PageRank/community result.
        var rf = includeShared ? "(r.owner_id = $ownerId OR r.owner_id IS NULL)" : "r.owner_id = $ownerId";
        return (
            $"MATCH (e:Entity) WHERE e.invalidated_at IS NULL AND {nf} RETURN id(e) AS id",
            $"MATCH (a:Entity)-[r:RELATED_TO]->(b:Entity) WHERE {af} AND {bf} AND {rf} RETURN id(a) AS source, id(b) AS target");
    }

    /// <summary>
    /// The <c>gds.graph.project.cypher</c> call. <c>validateRelationships: false</c> makes the projection
    /// skip (rather than error on) any relationship whose endpoint was filtered out; the owner id is bound
    /// via the projection's <c>parameters</c> map when scoped.
    /// </summary>
    public static string ProjectCypher(bool hasOwnerFilter)
    {
        var config = hasOwnerFilter
            ? "{validateRelationships: false, parameters: {ownerId: $ownerId}}"
            : "{validateRelationships: false}";
        return $"CALL gds.graph.project.cypher($graphName, $nodeQuery, $relQuery, {config}) YIELD nodeCount RETURN nodeCount";
    }
}
