namespace AgentMemory.Neo4j.Queries;

/// <summary>Original relation records and decision-owned roles; no legacy RELATED_TO mutation.</summary>
internal static class IdentityRelationshipQueries
{
    internal const string LegacyIdCollision = """
        RETURN EXISTS { MATCH ()-[:RELATED_TO {id: $id}]->() } AS collision
        """;

    internal const string GetObservation = """
        MATCH (o:IdentityRelationshipObservation {id: $id, owner_scope: $ownerScope})
        RETURN o.identity_record_json AS recordJson, o.valid_until AS endedAt
        """;

    internal const string ReadReplay = """
        MATCH (o:IdentityRelationshipObservation {id: $id})
        SET o.identity_lock_version = coalesce(o.identity_lock_version, 0) + 1
        RETURN o.owner_scope = $ownerScope AS authorized,
            CASE WHEN o.owner_scope = $ownerScope THEN o.identity_record_json ELSE null END AS recordJson,
            CASE WHEN o.owner_scope = $ownerScope THEN o.valid_until ELSE null END AS endedAt
        """;

    internal const string ReadEndpointDecision = """
        MATCH (d:IdentityDecision {id: $decisionId, owner_scope: $ownerScope})
        SET d.identity_lock_version = coalesce(d.identity_lock_version, 0) + 1
        RETURN d.identity_record_json AS recordJson, d.identity_target_snapshot AS targetSnapshot
        """;

    internal const string BoundIds = """
        MATCH (d:IdentityDecision {id: $decisionId, owner_scope: $ownerScope})
              <-[:SOURCE_IDENTITY|TARGET_IDENTITY]-(o:IdentityRelationshipObservation {owner_scope: $ownerScope})
        RETURN DISTINCT o.id AS id ORDER BY id LIMIT $limit
        """;

    internal const string CreateObservation = """
        CREATE (o:IdentityRelationshipObservation {id: $id, owner_scope: $ownerScope, owner_id: $ownerId,
            identity_record_json: $recordJson, identity_source_ids: $sourceIds,
            relation_type: $relationType, confidence: $confidence, description: $description, attributes: $attributes,
            identity_source_decision_id: $sourceDecisionId, identity_target_decision_id: $targetDecisionId,
            identity_lock_version: 0, created_at: datetime($createdAt),
            valid_until: CASE WHEN $endedAt IS NULL THEN null ELSE datetime($endedAt) END})
        """;

    internal const string LinkSource = """
        MATCH (o:IdentityRelationshipObservation {id: $id, owner_scope: $ownerScope}),
              (d:IdentityDecision {id: $decisionId, owner_scope: $ownerScope})
        MERGE (o)-[:SOURCE_IDENTITY]->(d)
        RETURN o.id AS id
        """;

    internal const string LinkTarget = """
        MATCH (o:IdentityRelationshipObservation {id: $id, owner_scope: $ownerScope}),
              (d:IdentityDecision {id: $decisionId, owner_scope: $ownerScope})
        MERGE (o)-[:TARGET_IDENTITY]->(d)
        RETURN o.id AS id
        """;

    internal const string EnsureDecisionTarget = """
        MATCH (d:IdentityDecision {id: $decisionId, owner_scope: $ownerScope}), (t:Entity {id: $targetId})
        WHERE (($ownerId IS NULL AND t.owner_id IS NULL) OR
            ($ownerId IS NOT NULL AND (t.owner_id = $ownerId OR ($includeShared AND t.owner_id IS NULL))))
            AND t.invalidated_at IS NULL AND t.merged_into IS NULL
        OPTIONAL MATCH (d)-[:RESOLVES_TO]->(old:Entity)
        WITH d, t, collect(old.id) AS previous
        WHERE size(previous) = 0 OR (size(previous) = 1 AND previous[0] = t.id)
        MERGE (d)-[:RESOLVES_TO]->(t)
        RETURN d.id AS id
        """;
}
