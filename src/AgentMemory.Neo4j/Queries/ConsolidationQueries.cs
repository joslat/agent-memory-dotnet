namespace AgentMemory.Neo4j.Queries;

/// <summary>
/// Cypher for batch memory-hygiene (consolidation, PR #113). Count* variants are the dry-run
/// projections of their mutating counterparts, so a dry run reports exactly what an apply run does.
/// </summary>
internal static class ConsolidationQueries
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

    // ── Dreaming (AMDREAM001): generic entities and unsaid preferences ──
    // Read-only projections feed the decision, made in code (GenericEntityRule; the preference source check), so a dry
    // run and an apply run decide alike; the Close* queries then stamp exactly the ids decided on. Soft-closed
    // (invalidated_at plus why), never deleted: a closing is auditable and can be undone. $ownerId null reads every owner.

    /// <summary>Live entities (not invalidated, not merged into another), with owner, name and type.</summary>
    public const string LiveEntities = @"
            MATCH (e:Entity)
            WHERE e.invalidated_at IS NULL AND e.merged_into IS NULL
              AND ($ownerId IS NULL OR e.owner_id = $ownerId)
            RETURN e.id AS id, e.owner_id AS ownerId, e.name AS name, e.type AS type";

    /// <summary>What every live fact and preference says, per owner: the text a generic entity must be named in.</summary>
    public const string LiveStatementTexts = @"
            MATCH (f:Fact)
            WHERE f.invalidated_at IS NULL AND ($ownerId IS NULL OR f.owner_id = $ownerId)
            RETURN f.owner_id AS ownerId,
                   coalesce(f.subject, '') + ' ' + coalesce(f.predicate, '') + ' ' + coalesce(f.object, '') AS text
            UNION ALL
            MATCH (p:Preference)
            WHERE p.invalidated_at IS NULL AND ($ownerId IS NULL OR p.owner_id = $ownerId)
            RETURN p.owner_id AS ownerId, coalesce(p.preference, '') AS text";

    /// <summary>Live connections touching any of <c>$closed</c> and none of <c>$spared</c>.</summary>
    public const string LiveConnectionsOf = @"
            MATCH (s:Entity)-[r:RELATED_TO]->(t:Entity)
            WHERE r.invalidated_at IS NULL AND r.id IS NOT NULL
              AND (s.id IN $closed OR t.id IN $closed)
              AND NOT (s.id IN $spared OR t.id IN $spared)
            RETURN r.id AS id, r.owner_id AS ownerId, s.name AS source, coalesce(r.relation_type, type(r)) AS type, t.name AS target";

    /// <summary>
    /// Live preferences, each with the first message it was stored from and up to two earlier messages of the same role
    /// in that message's conversation (oldest first).
    /// </summary>
    public const string LivePreferenceSources = @"
            MATCH (p:Preference)-[:EXTRACTED_FROM]->(m:Message)
            WHERE p.invalidated_at IS NULL AND ($ownerId IS NULL OR p.owner_id = $ownerId)
            WITH p, m ORDER BY m.created_at ASC
            WITH p, head(collect(m)) AS m
            OPTIONAL MATCH (c:Conversation)-[:HAS_MESSAGE]->(m)
            OPTIONAL MATCH (c)-[:HAS_MESSAGE]->(e:Message)
            WHERE e.role = m.role AND e.created_at < m.created_at
            WITH p, m, e ORDER BY e.created_at DESC
            WITH p, m, collect(e.content)[0..2] AS earlier
            RETURN p.id AS id, p.owner_id AS ownerId, p.category AS category, p.preference AS text,
                   m.id AS messageId, m.content AS message, toString(m.created_at) AS saidAt, reverse(earlier) AS earlier";

    /// <summary>Live preferences among <c>$ids</c>: an approved proposal is closed without asking the model again.</summary>
    public const string LivePreferencesById = @"
            UNWIND $ids AS id
            MATCH (p:Preference {id: id})
            WHERE p.invalidated_at IS NULL
            RETURN p.id AS id, p.owner_id AS ownerId, p.category AS category, p.preference AS text";

    /// <summary>Closes live entities by id: <c>invalidated_at</c> and why, kept and auditable.</summary>
    public const string CloseEntities = @"
            UNWIND $ids AS id
            MATCH (e:Entity {id: id})
            WHERE e.invalidated_at IS NULL
            SET e.invalidated_at = datetime($now), e.invalidated_reason = 'consolidation'
            RETURN count(e) AS count";

    /// <summary>Closes live connections by id.</summary>
    public const string CloseConnections = @"
            UNWIND $ids AS id
            MATCH (:Entity)-[r:RELATED_TO {id: id}]->(:Entity)
            WHERE r.invalidated_at IS NULL
            SET r.invalidated_at = datetime($now), r.invalidated_reason = 'consolidation'
            RETURN count(r) AS count";

    /// <summary>Closes live preferences by id.</summary>
    public const string ClosePreferences = @"
            UNWIND $ids AS id
            MATCH (p:Preference {id: id})
            WHERE p.invalidated_at IS NULL
            SET p.invalidated_at = datetime($now), p.invalidated_reason = 'consolidation'
            RETURN count(p) AS count";

    // ── Audit ────────────────────────────────────────────────────────────

    /// <summary>Records a (:ConsolidationRun) audit node for an applied run.</summary>
    public const string RecordConsolidationRun = @"
            MERGE (r:ConsolidationRun {id: $id})
            SET r.ran_at                     = datetime($ranAt),
                r.conversations_archived     = $conversationsArchived,
                r.preferences_removed        = $preferencesRemoved,
                r.duplicate_entities         = $duplicateEntities,
                r.long_trace_candidates      = $longTraceCandidates,
                r.generic_entities_closed    = $genericEntitiesClosed,
                r.generic_connections_closed = $genericConnectionsClosed,
                r.unsaid_preferences_closed  = $unsaidPreferencesClosed
            RETURN r";
}
