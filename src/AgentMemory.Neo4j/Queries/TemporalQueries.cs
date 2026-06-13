namespace AgentMemory.Neo4j.Queries;

/// <summary>
/// Cypher queries for temporal (point-in-time) memory retrieval. The vector AsOf searches are
/// <b>bitemporal</b> (D6): the <b>transaction-time</b> clock (<c>$systemAsOf</c>) binds
/// <c>created_at</c>/<c>invalidated_at</c> ("what the system believed at that time"), and the
/// <b>valid-time</b> clock (<c>$validAsOf</c>) binds a fact's <c>valid_from</c>/<c>valid_until</c>
/// ("what was true in the world at that time"). A single-clock caller passes both equal. The by-id AsOf
/// constants remain single-clock (<c>$asOf</c>) — a direct handle lookup has no need for two clocks.
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
    /// Vector similarity search on entities the system believed at <c>$systemAsOf</c> (entities have no
    /// valid-time window, so only the transaction clock applies), with an optional owner/shared filter
    /// (R1). Over-fetches <paramref name="topK"/> then LIMITs to <c>$limit</c> after filtering.
    /// </summary>
    public static string SearchEntitiesAsOf(bool hasOwnerFilter, bool includeShared, int topK) => $@"
            CALL db.index.vector.queryNodes('entity_embedding_idx', {topK}, $embedding)
            YIELD node, score
            WHERE score >= $minScore
              AND node.created_at <= datetime($systemAsOf)
              AND (node.invalidated_at IS NULL OR node.invalidated_at > datetime($systemAsOf)){OwnerAnd(hasOwnerFilter, includeShared)}
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
    /// Bitemporal vector similarity search on facts (D6): the transaction clock (<c>$systemAsOf</c>)
    /// filters <c>created_at</c>/<c>invalidated_at</c> ("what we believed"), and the valid-time clock
    /// (<c>$validAsOf</c>) filters the fact's validity window <c>valid_from</c>/<c>valid_until</c> ("what
    /// was true"). Pass both equal for ordinary single-clock point-in-time recall.
    /// </summary>
    public static string SearchFactsAsOf(bool hasOwnerFilter, bool includeShared, int topK) => $@"
            CALL db.index.vector.queryNodes('fact_embedding_idx', {topK}, $embedding)
            YIELD node, score
            WHERE score >= $minScore
              AND node.created_at <= datetime($systemAsOf)
              AND (node.invalidated_at IS NULL OR node.invalidated_at > datetime($systemAsOf))
              AND (node.valid_from IS NULL OR node.valid_from <= datetime($validAsOf))
              AND (node.valid_until IS NULL OR node.valid_until > datetime($validAsOf)){OwnerAnd(hasOwnerFilter, includeShared)}
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
    /// Vector similarity search on preferences the system believed at <c>$systemAsOf</c> (preferences
    /// have no valid-time window, so only the transaction clock applies).
    /// </summary>
    public static string SearchPreferencesAsOf(bool hasOwnerFilter, bool includeShared, int topK) => $@"
            CALL db.index.vector.queryNodes('preference_embedding_idx', {topK}, $embedding)
            YIELD node, score
            WHERE score >= $minScore
              AND node.created_at <= datetime($systemAsOf)
              AND (node.invalidated_at IS NULL OR node.invalidated_at > datetime($systemAsOf)){OwnerAnd(hasOwnerFilter, includeShared)}
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
