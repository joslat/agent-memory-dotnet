namespace Neo4j.AgentMemory.Neo4j.Queries;

/// <summary>
/// Centralized Cypher queries for Relationship operations.
/// Each constant corresponds to exactly one repository method in
/// <see cref="Neo4j.AgentMemory.Neo4j.Repositories.Neo4jRelationshipRepository"/>.
/// </summary>
public static class RelationshipQueries
{
    // ── UpsertAsync ────────────────────────────────────────────────────

    /// <summary>Merge a RELATED_TO relationship between two entities with full property set.</summary>
    public const string Upsert = @"
            MERGE (s:Entity {id: $sourceEntityId})
            MERGE (t:Entity {id: $targetEntityId})
            MERGE (s)-[r:RELATED_TO {id: $id}]->(t)
            ON CREATE SET
                r.relation_type      = $relationType,
                r.source_entity_id   = $sourceEntityId,
                r.target_entity_id   = $targetEntityId,
                r.confidence         = $confidence,
                r.description        = $description,
                r.valid_from         = CASE WHEN $validFrom IS NOT NULL THEN datetime($validFrom) ELSE null END,
                r.valid_until        = CASE WHEN $validUntil IS NOT NULL THEN datetime($validUntil) ELSE null END,
                r.attributes         = $attributes,
                r.source_message_ids = $sourceMessageIds,
                r.created_at         = datetime($createdAt),
                r.updated_at         = datetime($updatedAt),
                r.metadata           = $metadata
            ON MATCH SET
                r.relation_type      = $relationType,
                r.confidence         = $confidence,
                r.description        = $description,
                r.valid_from         = CASE WHEN $validFrom IS NOT NULL THEN datetime($validFrom) ELSE null END,
                r.valid_until        = CASE WHEN $validUntil IS NOT NULL THEN datetime($validUntil) ELSE null END,
                r.attributes         = $attributes,
                r.source_message_ids = $sourceMessageIds,
                r.updated_at         = datetime($updatedAt),
                r.metadata           = $metadata
            RETURN r";

    // ── GetByIdAsync ───────────────────────────────────────────────────

    /// <summary>Get a single RELATED_TO relationship by id.</summary>
    public const string GetById = "MATCH ()-[r:RELATED_TO {id: $id}]->() RETURN r";

    // ── GetByEntityAsync ───────────────────────────────────────────────

    /// <summary>Get all RELATED_TO relationships involving a specific entity (source or target).</summary>
    public const string GetByEntity = @"
            MATCH (s:Entity)-[r:RELATED_TO]->(t:Entity)
            WHERE s.id = $entityId OR t.id = $entityId
            RETURN r";

    // ── GetBySourceEntityAsync ─────────────────────────────────────────

    /// <summary>Get all outgoing RELATED_TO relationships from a specific entity.</summary>
    public const string GetBySourceEntity =
        "MATCH (s:Entity {id: $sourceEntityId})-[r:RELATED_TO]->() RETURN r";

    // ── GetByTargetEntityAsync ─────────────────────────────────────────

    /// <summary>Get all incoming RELATED_TO relationships to a specific entity.</summary>
    public const string GetByTargetEntity =
        "MATCH ()-[r:RELATED_TO]->(t:Entity {id: $targetEntityId}) RETURN r";
}
