namespace AgentMemory.Neo4j.Queries;

/// <summary>
/// Cypher for batch memory-hygiene (consolidation, PR #113). Count* variants are the dry-run
/// projections of their mutating counterparts, so a dry run reports exactly what an apply run does.
/// </summary>
public static class ConsolidationQueries
{
    // ── Archive expired conversations ───────────────────────────────────

    /// <summary>Counts conversations not updated since <c>$cutoff</c> that are not already archived.</summary>
    public const string CountExpiredConversations = @"
            MATCH (c:Conversation)
            WHERE c.updated_at < datetime($cutoff) AND coalesce(c.archived, false) = false
            RETURN count(c) AS count";

    /// <summary>Archives (sets <c>archived = true</c>) conversations not updated since <c>$cutoff</c>.</summary>
    public const string ArchiveExpiredConversations = @"
            MATCH (c:Conversation)
            WHERE c.updated_at < datetime($cutoff) AND coalesce(c.archived, false) = false
            SET c.archived = true
            RETURN count(c) AS count";

    // ── Remove duplicate preferences (exact owner+category+text) ─────────

    /// <summary>
    /// Counts redundant preferences (per owner+category+text group, all but one). Only live (not
    /// already-invalidated) preferences are considered, so the count matches what a non-destructive
    /// collapse would close and re-runs after a collapse report zero.
    /// </summary>
    public const string CountDuplicatePreferences = @"
            MATCH (p:Preference)
            WHERE p.invalidated_at IS NULL
            WITH coalesce(p.owner_id, '*') AS ownerKey, p.category AS category, toLower(p.preference) AS text, collect(p) AS grp
            WHERE size(grp) >= $minGroupSize
            RETURN coalesce(sum(size(grp) - 1), 0) AS count";

    /// <summary>
    /// Collapses redundant preferences <b>non-destructively</b> (D7), keeping the newest per
    /// owner+category+text group: the older duplicates are soft-invalidated (stamp <c>invalidated_at</c>
    /// so they drop from live recall but are kept and auditable) and linked
    /// <c>(dup)-[:SUPERSEDED_BY]-&gt;(keep)</c>, rather than <c>DETACH DELETE</c>d. Only live preferences
    /// are grouped (<c>invalidated_at IS NULL</c>), so the operation is idempotent — a re-run finds nothing
    /// new. The group is ordered newest-first, so <c>grp[0]</c> is the keeper and <c>grp[1..]</c> the
    /// duplicates.
    /// </summary>
    public const string RemoveDuplicatePreferences = @"
            MATCH (p:Preference)
            WHERE p.invalidated_at IS NULL
            WITH coalesce(p.owner_id, '*') AS ownerKey, p.category AS category, toLower(p.preference) AS text, p
            ORDER BY p.created_at DESC
            WITH ownerKey, category, text, collect(p) AS grp
            WHERE size(grp) >= $minGroupSize
            WITH grp[0] AS keep, grp[1..] AS dups
            UNWIND dups AS dup
            SET dup.invalidated_at = coalesce(dup.invalidated_at, datetime())
            MERGE (dup)-[:SUPERSEDED_BY]->(keep)
            RETURN count(dup) AS count";

    // ── Detect duplicate entities (report only) ──────────────────────────

    /// <summary>
    /// Counts redundant entities (per owner+name+type group, all but one). Detection only. Only live
    /// (not already-invalidated) entities are grouped (<c>invalidated_at IS NULL</c>) — mirroring
    /// <see cref="CountDuplicatePreferences"/> — so the count reflects what a non-destructive collapse
    /// would close and does not over-report tombstoned duplicates after decay (R6-B).
    /// </summary>
    public const string CountDuplicateEntities = @"
            MATCH (e:Entity)
            WHERE e.invalidated_at IS NULL
            WITH coalesce(e.owner_id, '*') AS ownerKey, toLower(e.name) AS name, e.type AS type, collect(e) AS grp
            WHERE size(grp) >= $minGroupSize
            RETURN coalesce(sum(size(grp) - 1), 0) AS count";

    // ── Detect long traces (report only) ─────────────────────────────────

    /// <summary>Counts reasoning traces with more than <c>$threshold</c> steps (summarization candidates).</summary>
    public const string CountLongTraces = @"
            MATCH (t:ReasoningTrace)-[:HAS_STEP]->(s:ReasoningStep)
            WITH t, count(s) AS steps
            WHERE steps > $threshold
            RETURN count(t) AS count";

    // ── Audit ────────────────────────────────────────────────────────────

    /// <summary>Records a (:ConsolidationRun) audit node for an applied run.</summary>
    public const string RecordConsolidationRun = @"
            MERGE (r:ConsolidationRun {id: $id})
            SET r.ran_at                  = datetime($ranAt),
                r.conversations_archived  = $conversationsArchived,
                r.preferences_removed     = $preferencesRemoved,
                r.duplicate_entities      = $duplicateEntities,
                r.long_trace_candidates   = $longTraceCandidates
            RETURN r";
}
