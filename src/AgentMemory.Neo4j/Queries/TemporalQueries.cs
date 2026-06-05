namespace AgentMemory.Neo4j.Queries;

/// <summary>
/// Cypher queries for temporal (point-in-time) memory retrieval.
/// All queries filter nodes that existed at a given <c>$asOf</c> timestamp,
/// meaning they were created on or before <c>$asOf</c> and had not yet been
/// invalidated/superseded at that time.
/// </summary>
public static class TemporalQueries
{
    /// <summary>The owner/shared AsOf-search AND-clause for node alias <c>node</c>, or empty when unscoped (R1).</summary>
    private static string OwnerAnd(bool hasOwnerFilter, bool includeShared) =>
        !hasOwnerFilter ? string.Empty
        : includeShared ? "\n              AND (node.owner_id = $ownerId OR node.owner_id IS NULL)"
                        : "\n              AND node.owner_id = $ownerId";

    // ── Entities ────────────────────────────────────────────────────────

    /// <summary>
    /// Vector similarity search on entities that existed at <c>$asOf</c>, with an optional owner/shared
    /// filter (R1). Over-fetches <paramref name="topK"/> then LIMITs to <c>$limit</c> after filtering.
    /// </summary>
    public static string SearchEntitiesAsOf(bool hasOwnerFilter, bool includeShared, int topK) => $@"
            CALL db.index.vector.queryNodes('entity_embedding_idx', {topK}, $embedding)
            YIELD node, score
            WHERE score >= $minScore
              AND node.created_at <= datetime($asOf)
              AND (node.invalidated_at IS NULL OR node.invalidated_at > datetime($asOf)){OwnerAnd(hasOwnerFilter, includeShared)}
            RETURN node, score
            ORDER BY score DESC
            LIMIT $limit";

    /// <summary>Get a single entity by id as of a point in time.</summary>
    public const string GetEntityByIdAsOf = @"
            MATCH (e:Entity {id: $id})
            WHERE e.created_at <= datetime($asOf)
              AND (e.invalidated_at IS NULL OR e.invalidated_at > datetime($asOf))
            RETURN e";

    // ── Facts ───────────────────────────────────────────────────────────

    /// <summary>
    /// Vector similarity search on facts that existed at <c>$asOf</c>.
    /// Also respects the fact's temporal validity window (<c>valid_from</c> / <c>valid_until</c>).
    /// </summary>
    public static string SearchFactsAsOf(bool hasOwnerFilter, bool includeShared, int topK) => $@"
            CALL db.index.vector.queryNodes('fact_embedding_idx', {topK}, $embedding)
            YIELD node, score
            WHERE score >= $minScore
              AND node.created_at <= datetime($asOf)
              AND (node.invalidated_at IS NULL OR node.invalidated_at > datetime($asOf))
              AND (node.valid_from IS NULL OR node.valid_from <= datetime($asOf))
              AND (node.valid_until IS NULL OR node.valid_until > datetime($asOf)){OwnerAnd(hasOwnerFilter, includeShared)}
            RETURN node, score
            ORDER BY score DESC
            LIMIT $limit";

    /// <summary>Get a single fact by id as of a point in time.</summary>
    public const string GetFactByIdAsOf = @"
            MATCH (f:Fact {id: $id})
            WHERE f.created_at <= datetime($asOf)
              AND (f.invalidated_at IS NULL OR f.invalidated_at > datetime($asOf))
              AND (f.valid_from IS NULL OR f.valid_from <= datetime($asOf))
              AND (f.valid_until IS NULL OR f.valid_until > datetime($asOf))
            RETURN f";

    // ── Preferences ────────────────────────────────────────────────────

    /// <summary>
    /// Vector similarity search on preferences that existed at <c>$asOf</c>.
    /// </summary>
    public static string SearchPreferencesAsOf(bool hasOwnerFilter, bool includeShared, int topK) => $@"
            CALL db.index.vector.queryNodes('preference_embedding_idx', {topK}, $embedding)
            YIELD node, score
            WHERE score >= $minScore
              AND node.created_at <= datetime($asOf)
              AND (node.invalidated_at IS NULL OR node.invalidated_at > datetime($asOf)){OwnerAnd(hasOwnerFilter, includeShared)}
            RETURN node, score
            ORDER BY score DESC
            LIMIT $limit";

    /// <summary>Get a single preference by id as of a point in time.</summary>
    public const string GetPreferenceByIdAsOf = @"
            MATCH (p:Preference {id: $id})
            WHERE p.created_at <= datetime($asOf)
              AND (p.invalidated_at IS NULL OR p.invalidated_at > datetime($asOf))
            RETURN p";

    // ── Messages ────────────────────────────────────────────────────────

    /// <summary>Recent messages for a session that existed at <c>$asOf</c>.</summary>
    public const string GetRecentMessagesAsOf = @"
            MATCH (m:Message {session_id: $sessionId})
            WHERE m.timestamp <= datetime($asOf)
            RETURN m
            ORDER BY m.timestamp DESC
            LIMIT $limit";
}
