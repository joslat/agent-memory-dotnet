namespace AgentMemory.Neo4j.Queries;

/// <summary>
/// Cypher queries for memory decay, access tracking, and pruning.
/// </summary>
internal static class DecayQueries
{
    /// <summary>
    /// Updates <c>last_accessed_at</c> to now, increments <c>access_count</c>, and records a
    /// <c>:MemoryReadAudit</c> row for a node with a given label and id. Use
    /// <see cref="UpdateAccessTimestamp(string)"/> to inject the label.
    /// </summary>
    public static string UpdateAccessTimestamp(string label) => $@"
            MATCH (n:{label} {{id: $id}})
            SET n.last_accessed_at = datetime($now),
                n.access_count     = COALESCE(n.access_count, 0) + 1
            CREATE (:MemoryReadAudit {{
                id: randomUUID(),
                kind: $kind,
                memory_id: $id,
                owner_id: n.owner_id,
                read_at: datetime($now),
                access_count: n.access_count
            }})
            RETURN n.access_count AS accessCount";

    /// <summary>
    /// Batch form of <see cref="UpdateAccessTimestamp(string)"/>: same per-node effect, driven by an
    /// <c>UNWIND</c> so one query touches every recalled node of a given kind.
    /// </summary>
    /// <remarks>
    /// Statement-for-statement identical to the single-node query — same <c>SET</c>, same audit node,
    /// with <c>access_count</c> read after the <c>SET</c> so the audit records the post-increment value
    /// exactly as before. The only difference is arity. Callers must de-duplicate <c>$ids</c>: a
    /// repeated id would be a repeated row and would increment twice.
    /// </remarks>
    public static string UpdateAccessTimestampBatch(string label) => $@"
            UNWIND $ids AS nodeId
            MATCH (n:{label} {{id: nodeId}})
            SET n.last_accessed_at = datetime($now),
                n.access_count     = COALESCE(n.access_count, 0) + 1
            CREATE (:MemoryReadAudit {{
                id: randomUUID(),
                kind: $kind,
                memory_id: nodeId,
                owner_id: n.owner_id,
                read_at: datetime($now),
                access_count: n.access_count
            }})";

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
    /// Prunes Entity nodes whose retention score (computed inline) falls below the threshold. The decay
    /// formula is: confidence * exp(-lambda * daysSinceAccess) + boostFactor * accessCount. When scoped
    /// (R1) the prune only touches the owner's <b>own</b> nodes — never another owner's, never shared/global.
    /// When <paramref name="nonDestructive"/> (D4) it soft-invalidates (stamps <c>invalidated_at</c>,
    /// skipping already-invalidated nodes so a re-run does not re-count them); otherwise it hard-deletes.
    /// </summary>
    public static string PruneEntities(bool hasOwnerFilter, bool nonDestructive = false) => BuildPrune("e", "Entity", hasOwnerFilter, nonDestructive);

    /// <summary>Prunes Fact nodes whose retention score falls below the threshold (owner-scoped when set; soft-invalidate when <paramref name="nonDestructive"/>).</summary>
    public static string PruneFacts(bool hasOwnerFilter, bool nonDestructive = false) => BuildPrune("f", "Fact", hasOwnerFilter, nonDestructive);

    /// <summary>Prunes Preference nodes whose retention score falls below the threshold (owner-scoped when set; soft-invalidate when <paramref name="nonDestructive"/>).</summary>
    public static string PrunePreferences(bool hasOwnerFilter, bool nonDestructive = false) => BuildPrune("p", "Preference", hasOwnerFilter, nonDestructive);

    private static string BuildPrune(string a, string label, bool hasOwnerFilter, bool nonDestructive)
    {
        var owner = hasOwnerFilter ? " AND " + a + ".owner_id = $ownerId" : string.Empty;
        // Non-destructive mode only acts on not-yet-invalidated nodes so a re-run doesn't re-count (and
        // re-stamp) the same nodes. Destructive mode has no such need (deleted nodes are gone) and should
        // also purge already-invalidated nodes when an operator opts into a hard purge.
        var notInvalidated = nonDestructive ? " AND " + a + ".invalidated_at IS NULL" : string.Empty;
        // Soft-invalidate (keep, recoverable, auditable) vs hard DETACH DELETE (storage reclamation / GDPR).
        var action = nonDestructive
            ? "SET " + a + ".invalidated_at = datetime($now)"
            : "DETACH DELETE " + a;
        // daysSince is total elapsed days as a float, computed from the epoch-millis delta. We deliberately
        // avoid duration.between(...).days, which returns only the *days component* of a normalized
        // years/months/days duration (e.g. a 400-day span normalizes to ~1y1m and .days is small),
        // not the total elapsed days the decay formula needs.
        return
            "MATCH (" + a + ":" + label + ")\n" +
            "            WHERE " + a + ".created_at IS NOT NULL" + owner + notInvalidated + "\n" +
            "            WITH " + a + ",\n" +
            "                 " + a + ".confidence AS conf,\n" +
            "                 COALESCE(" + a + ".access_count, 0) AS ac,\n" +
            "                 (datetime($now).epochMillis - COALESCE(" + a + ".last_accessed_at, " + a + ".created_at).epochMillis) / 86400000.0 AS rawDays\n" +
            // Clamp daysSince to >= 0 so the prune score matches the C# read-path score exactly for nodes
            // with a future last_accessed_at (a negative exponent would otherwise inflate the score).
            "            WITH " + a + ", conf, ac, CASE WHEN rawDays < 0 THEN 0.0 ELSE rawDays END AS daysSince\n" +
            "            WHERE (COALESCE(conf, 0.5) * exp(-$lambda * daysSince) + $boostFactor * ac) < $minScore\n" +
            "            " + action + "\n" +
            "            RETURN count(*) AS pruned";
    }
}
