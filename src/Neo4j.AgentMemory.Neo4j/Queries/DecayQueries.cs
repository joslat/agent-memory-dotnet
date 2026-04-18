namespace Neo4j.AgentMemory.Neo4j.Queries;

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
    /// Deletes Entity nodes whose retention score (computed inline) falls below the threshold.
    /// The decay formula is: confidence * exp(-lambda * daysSinceAccess) + boostFactor * accessCount.
    /// </summary>
    public const string PruneEntities = @"
            MATCH (e:Entity)
            WHERE e.created_at IS NOT NULL
            WITH e,
                 e.confidence AS conf,
                 COALESCE(e.access_count, 0) AS ac,
                 duration.between(COALESCE(e.last_accessed_at, e.created_at), datetime($now)).days +
                 duration.between(COALESCE(e.last_accessed_at, e.created_at), datetime($now)).hours / 24.0 AS daysSince
            WHERE (COALESCE(conf, 0.5) * exp(-$lambda * daysSince) + $boostFactor * ac) < $minScore
            DETACH DELETE e
            RETURN count(*) AS pruned";

    /// <summary>
    /// Deletes Fact nodes whose retention score falls below the threshold.
    /// </summary>
    public const string PruneFacts = @"
            MATCH (f:Fact)
            WHERE f.created_at IS NOT NULL
            WITH f,
                 f.confidence AS conf,
                 COALESCE(f.access_count, 0) AS ac,
                 duration.between(COALESCE(f.last_accessed_at, f.created_at), datetime($now)).days +
                 duration.between(COALESCE(f.last_accessed_at, f.created_at), datetime($now)).hours / 24.0 AS daysSince
            WHERE (COALESCE(conf, 0.5) * exp(-$lambda * daysSince) + $boostFactor * ac) < $minScore
            DETACH DELETE f
            RETURN count(*) AS pruned";

    /// <summary>
    /// Deletes Preference nodes whose retention score falls below the threshold.
    /// </summary>
    public const string PrunePreferences = @"
            MATCH (p:Preference)
            WHERE p.created_at IS NOT NULL
            WITH p,
                 p.confidence AS conf,
                 COALESCE(p.access_count, 0) AS ac,
                 duration.between(COALESCE(p.last_accessed_at, p.created_at), datetime($now)).days +
                 duration.between(COALESCE(p.last_accessed_at, p.created_at), datetime($now)).hours / 24.0 AS daysSince
            WHERE (COALESCE(conf, 0.5) * exp(-$lambda * daysSince) + $boostFactor * ac) < $minScore
            DETACH DELETE p
            RETURN count(*) AS pruned";
}
