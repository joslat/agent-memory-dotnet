namespace AgentMemory.Neo4j.Queries;

/// <summary>Decision-owned, reversible support. These queries never create generic ABOUT or SAME_AS.</summary>
internal static class IdentityDecisionQueries
{
    internal const string EnsureScope = """
        MERGE (s:IdentityEvidenceScopeLock {owner_scope: $ownerScope})
        ON CREATE SET s.identity_lock_version = 0, s.owner_id = CASE WHEN $ownerScope STARTS WITH 'owned:' THEN substring($ownerScope, 6) ELSE null END
        """;

    internal const string AcquireScope = """
        MATCH (s:IdentityEvidenceScopeLock {owner_scope: $ownerScope})
        SET s.identity_lock_version = 1
        RETURN count(s) AS acquired
        """;

    internal const string LockScope = """
        MERGE (s:IdentityEvidenceScopeLock {owner_scope: $ownerScope})
        ON CREATE SET s.owner_id = CASE WHEN $ownerScope STARTS WITH 'owned:' THEN substring($ownerScope, 6) ELSE null END
        SET s.identity_lock_version = 1
        """;

    internal const string ValidateRoots = """
        UNWIND $sourceIds AS sourceId
        OPTIONAL MATCH (m:Message {id: sourceId})
        OPTIONAL MATCH (c:Conversation {id: m.conversation_id})
        RETURN count(CASE WHEN c IS NOT NULL AND (
            ($ownerId IS NULL AND c.user_id IS NOT NULL) OR
            ($ownerId IS NOT NULL AND ((c.user_id IS NULL AND NOT $includeShared) OR
                (c.user_id IS NOT NULL AND c.user_id <> $ownerId)))) THEN 1 END) = 0 AS valid
        """;

    // Lock only visible rows, retaining existing values. Re-read both text and owner after the
    // locks: ordinary message/conversation writers need not participate in identity scope locks.
    internal const string ValidateContextualSources = """
        UNWIND $sources AS source
        WITH source ORDER BY source.id
        OPTIONAL MATCH (m:Message {id: source.id})
        OPTIONAL MATCH (c:Conversation {id: m.conversation_id})
        WITH source, m, c, coalesce(
            ($ownerId IS NULL AND c IS NOT NULL AND c.user_id IS NULL) OR
            ($ownerId IS NOT NULL AND (c.user_id = $ownerId OR ($includeShared AND c IS NOT NULL AND c.user_id IS NULL))), false) AS visible
        FOREACH (node IN CASE WHEN visible THEN [c] ELSE [] END | SET node.user_id = node.user_id)
        FOREACH (node IN CASE WHEN visible THEN [m] ELSE [] END | SET node.content = node.content)
        RETURN source.id AS sourceId, coalesce(c.id = m.conversation_id AND m.invalidated_at IS NULL AND m.content = source.text AND (
            ($ownerId IS NULL AND c IS NOT NULL AND c.user_id IS NULL) OR
            ($ownerId IS NOT NULL AND (c.user_id = $ownerId OR ($includeShared AND c IS NOT NULL AND c.user_id IS NULL)))), false) AS valid
        """;

    internal const string ReadReplay = """
        MATCH (d:IdentityDecision)
        WHERE d.id = $decisionId OR (d.owner_scope = $ownerScope AND d.identity_observation_key = $observationKey)
        WITH d ORDER BY d.id LIMIT 2
        SET d.identity_lock_version = coalesce(d.identity_lock_version, 0) + 1
        RETURN d.owner_scope = $ownerScope AS authorized,
            CASE WHEN d.owner_scope = $ownerScope THEN d.identity_record_json ELSE null END AS identity_record_json,
            CASE WHEN d.owner_scope = $ownerScope THEN d.identity_target_snapshot ELSE null END AS identity_target_snapshot
        """;

    // ILC-19 review queue: decisions in the exact owner scope that still hold proposal edges; bounded, stable paging.
    internal const string ListPending = """
        MATCH (d:IdentityDecision {owner_scope: $ownerScope})
        WHERE ($after IS NULL OR d.id > $after) AND EXISTS { MATCH (d)-[:POSSIBLE_SAME_AS]->(:Entity) }
        RETURN d.identity_record_json AS identity_record_json ORDER BY d.id LIMIT $limit
        """;

    internal const string Get = """
        MATCH (d:IdentityDecision {id: $decisionId, owner_scope: $ownerScope}) RETURN d.identity_record_json AS identity_record_json
        """;

    internal const string LockCandidates = """
        UNWIND $ids AS id
        MATCH (e:Entity {id: id})
        WHERE ($ownerId IS NULL AND e.owner_id IS NULL) OR
            ($ownerId IS NOT NULL AND (e.owner_id = $ownerId OR ($includeShared AND e.owner_id IS NULL)))
        WITH e ORDER BY e.id
        SET e.identity_evidence_version = coalesce(e.identity_evidence_version, 0)
        RETURN e.id AS id
        """;

    internal const string CreateObservation = """
        MERGE (e:Entity {id: $entityId})
        ON CREATE SET e += $properties
        WITH e, coalesce(((e.owner_id IS NULL AND $ownerId IS NULL) OR e.owner_id = $ownerId)
            AND e.identity_observation_key = $observationKey
            AND e.name = $name AND e.type = $type, false) AS valid
        CALL {
            WITH e, valid
            UNWIND $sourceIds AS sourceId
            MATCH (m:Message {id: sourceId})
            MATCH (c:Conversation {id: m.conversation_id})
            WHERE valid AND (($ownerId IS NULL AND c.user_id IS NULL) OR ($ownerId IS NOT NULL AND (c.user_id = $ownerId OR ($includeShared AND c.user_id IS NULL))))
            MERGE (e)-[:EXTRACTED_FROM]->(m)
            RETURN count(m) AS linked
        }
        RETURN valid
        """;

    internal const string Create = """
        CREATE (d:IdentityDecision {id: $decisionId, owner_scope: $ownerScope, owner_id: $ownerId,
            identity_observation_key: $observationKey, identity_record_json: $recordJson, identity_target_snapshot: $targetSnapshot,
            identity_revision: 1, identity_lock_version: 0, created_at: datetime($createdAt)})
        CREATE (r:IdentityDecisionRevision {id: $revisionId, owner_scope: $ownerScope, owner_id: $ownerId,
            identity_record_json: $recordJson, identity_revision: 1, created_at: datetime($createdAt)})
        CREATE (d)-[:HAS_IDENTITY_REVISION]->(r)
        """;

    internal const string Proposals = """
        MATCH (d:IdentityDecision {id: $decisionId, owner_scope: $ownerScope})
        UNWIND $ids AS id
        MATCH (e:Entity {id: id})
        WHERE e.invalidated_at IS NULL AND e.merged_into IS NULL AND
            (($ownerId IS NULL AND e.owner_id IS NULL) OR
             ($ownerId IS NOT NULL AND (e.owner_id=$ownerId OR ($includeShared AND e.owner_id IS NULL))))
        MERGE (d)-[:POSSIBLE_SAME_AS]->(e)
        RETURN count(e) AS linked
        """;

    internal const string BindingIds = """
        MATCH (b:IdentityFactBinding {identity_decision_id: $decisionId, owner_scope: $ownerScope})
        RETURN b.id AS id ORDER BY id LIMIT $limit
        """;

    internal const string Bind = """
        MATCH (d:IdentityDecision {id: $decisionId, owner_scope: $ownerScope}), (f:Fact {id: $factId}), (t:Entity {id: $targetId})
        WHERE (($ownerId IS NULL AND f.owner_id IS NULL) OR ($ownerId IS NOT NULL AND f.owner_id = $ownerId))
            AND (($role = 'Subject' AND f.subject_key = $surfaceKey) OR ($role = 'Object' AND f.object_key = $surfaceKey))
            // The fact was said in one of the decision's source messages, or (PR260-N1) this binding already ties this
            // fact to this decision: its provenance was checked when it was made, and a fact said again since keeps only
            // its latest source, so reviewing that decision must not fail on it.
            AND (any(source IN $sourceIds WHERE source IN coalesce(f.source_message_ids, [])) OR EXISTS {
                MATCH (f)-[:HAS_IDENTITY_BINDING]->(existing:IdentityFactBinding {id: $bindingId})
                WHERE existing.identity_decision_id = $decisionId AND existing.owner_scope = $ownerScope })
            AND (($ownerId IS NULL AND t.owner_id IS NULL) OR
                ($ownerId IS NOT NULL AND (t.owner_id = $ownerId OR ($includeShared AND t.owner_id IS NULL))))
        MERGE (b:IdentityFactBinding {id: $bindingId})
        ON CREATE SET b.identity_decision_id = $decisionId, b.owner_scope = $ownerScope, b.owner_id = $ownerId, b.identity_fact_role = $role,
            b.identity_observed_surface = $surface, b.identity_source_ids = $sourceIds, b.created_at = datetime($createdAt)
        WITH d, f, t, b
        WHERE b.identity_decision_id = $decisionId AND b.owner_scope = $ownerScope AND b.identity_fact_role = $role AND b.identity_observed_surface = $surface
        MERGE (f)-[:HAS_IDENTITY_BINDING]->(b)
        MERGE (b)-[:DECIDED_BY]->(d)
        MERGE (b)-[:RESOLVES_TO]->(t)
        RETURN b.id AS id
        """;
}
