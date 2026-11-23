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
        // P231-A F3: created_at as a datetime, as every other entity write stores it. The map carried it as text, which the
        // prune's score could not read and every created_at comparison (as of a moment, change windows) skipped.
        ON CREATE SET e += $properties, e.created_at = datetime($properties.created_at)
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

    /// <summary>
    /// IDENTITY-CONFIRMED-ALIAS: a decision confirmed as a person keeps the names its observation was seen under (its name
    /// and the short forms the writer's reference gave it) among that person's kept names (identity_surfaces), as Bind keeps
    /// a bound fact's: only on a target the decision's owner owns, starting, when none are kept yet, from the person's own
    /// bindings, and append-only. The observation of a confirmed decision is not stored as an entity, so without this a
    /// later turn that says the short form alone finds nobody. Kept names are no aliases: identity does not match on them.
    /// </summary>
    internal const string KeepSeenNames = """
        MATCH (t:Entity {id: $targetId})
        WHERE $ownerId IS NOT NULL AND t.owner_id = $ownerId AND t.invalidated_at IS NULL AND t.merged_into IS NULL
        WITH t, CASE WHEN t.identity_surfaces IS NULL
            THEN [(t)<-[:RESOLVES_TO]-(o:IdentityFactBinding) WHERE o.owner_id = t.owner_id AND o.identity_observed_surface IS NOT NULL
                  | o.identity_observed_surface]
            ELSE t.identity_surfaces END AS have
        WITH t, have, [s IN $names WHERE NOT s IN have] AS added
        FOREACH (_ IN CASE WHEN size(added) > 0 OR t.identity_surfaces IS NULL THEN [1] ELSE [] END |
            SET t.identity_surfaces = reduce(names = [], s IN have + added | CASE WHEN s IN names THEN names ELSE names + s END))
        RETURN size(added) AS kept
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
        // REV2-2b: the name this binding saw is kept on its person, so recall for people reads a person's names without
        // walking every binding. Only on a target the binding's owner owns: one owner's spelling never sits on a shared
        // node another owner reads. Append-only, so a name a review moved elsewhere stays and errs towards ambiguity. A
        // person with no names kept yet (bound before 2.3.1 and not bound since) starts from all of its own bindings, this
        // one included, so the first new binding never hides the names bound before it.
        WITH b, t, CASE WHEN t.identity_surfaces IS NULL AND t.owner_id IS NOT NULL AND t.owner_id = b.owner_id
            THEN [(t)<-[:RESOLVES_TO]-(o:IdentityFactBinding) WHERE o.owner_id = t.owner_id AND o.identity_observed_surface IS NOT NULL
                  | o.identity_observed_surface]
            ELSE t.identity_surfaces END AS have
        FOREACH (_ IN CASE WHEN t.owner_id IS NOT NULL AND t.owner_id = b.owner_id
                AND (t.identity_surfaces IS NULL OR NOT b.identity_observed_surface IN t.identity_surfaces) THEN [1] ELSE [] END |
            SET t.identity_surfaces = reduce(names = [], s IN have + b.identity_observed_surface |
                CASE WHEN s IN names THEN names ELSE names + s END))
        RETURN b.id AS id
        """;
}
