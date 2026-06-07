namespace AgentMemory.Neo4j.Queries;

/// <summary>
/// Cypher queries for memory decay, access tracking, and pruning.
/// </summary>
public static class DecayQueries
{
    /// <summary>
    /// Updates <c>last_accessed_at</c> to now and increments <c>access_count</c> for a node
    /// with a given label and id.  Use <see cref="UpdateAccessTimestamp(string)"/> to inject the label.
    /// </summary>
    public static string UpdateAccessTimestamp(string label) => $@"
            MATCH (n:{label} {{id: $id}})
            SET n.last_accessed_at = datetime($now),
                n.access_count     = COALESCE(n.access_count, 0) + 1
            RETURN n.access_count AS accessCount";

    /// <summary>
    /// Retrieves the fields needed to compute a retention score for a single node.
    /// </summary>
    public static string GetRetentionFields(string label) => $@"
            MATCH (n:{label} {{id: $id}})
            RETURN n.confidence         AS confidence,
                   n.created_at         AS createdAt,
                   n.last_accessed_at   AS lastAccessedAt,
                   n.access_count       AS accessCount";

    /// <summary>
    /// Deletes Entity nodes whose retention score (computed inline) falls below the threshold. The decay
    /// formula is: confidence * exp(-lambda * daysSinceAccess) + boostFactor * accessCount. When scoped
    /// (R1) the prune only removes the owner's <b>own</b> nodes — never another owner's, never shared/global.
    /// </summary>
    public static string PruneEntities(bool hasOwnerFilter) => BuildPrune("e", "Entity", hasOwnerFilter);

    /// <summary>Deletes Fact nodes whose retention score falls below the threshold (owner-scoped when set).</summary>
    public static string PruneFacts(bool hasOwnerFilter) => BuildPrune("f", "Fact", hasOwnerFilter);

    /// <summary>Deletes Preference nodes whose retention score falls below the threshold (owner-scoped when set).</summary>
    public static string PrunePreferences(bool hasOwnerFilter) => BuildPrune("p", "Preference", hasOwnerFilter);

    private static string BuildPrune(string a, string label, bool hasOwnerFilter)
    {
        var owner = hasOwnerFilter ? " AND " + a + ".owner_id = $ownerId" : string.Empty;
        // daysSince is total elapsed days as a float, computed from the epoch-millis delta. We deliberately
        // avoid duration.between(...).days, which returns only the *days component* of a normalized
        // years/months/days duration (e.g. a 400-day span normalizes to ~1y1m and .days is small),
        // not the total elapsed days the decay formula needs.
        return
            "MATCH (" + a + ":" + label + ")\n" +
            "            WHERE " + a + ".created_at IS NOT NULL" + owner + "\n" +
            "            WITH " + a + ",\n" +
            "                 " + a + ".confidence AS conf,\n" +
            "                 COALESCE(" + a + ".access_count, 0) AS ac,\n" +
            "                 (datetime($now).epochMillis - COALESCE(" + a + ".last_accessed_at, " + a + ".created_at).epochMillis) / 86400000.0 AS rawDays\n" +
            // Clamp daysSince to >= 0 so the prune score matches the C# read-path score exactly for nodes
            // with a future last_accessed_at (a negative exponent would otherwise inflate the score).
            "            WITH " + a + ", conf, ac, CASE WHEN rawDays < 0 THEN 0.0 ELSE rawDays END AS daysSince\n" +
            "            WHERE (COALESCE(conf, 0.5) * exp(-$lambda * daysSince) + $boostFactor * ac) < $minScore\n" +
            "            DETACH DELETE " + a + "\n" +
            "            RETURN count(*) AS pruned";
    }
}
