namespace AgentMemory.Neo4j.Queries;

/// <summary>
/// Centralized Cypher queries for Relationship operations.
/// Each constant corresponds to exactly one repository method in
/// <see cref="AgentMemory.Neo4j.Repositories.Neo4jRelationshipRepository"/>.
/// </summary>
internal static class RelationshipQueries
{
    // ── UpsertAsync ────────────────────────────────────────────────────

    /// <summary>Merge a RELATED_TO relationship between two entities with full property set.</summary>
    public const string Upsert = @"
            MERGE (s:Entity {id: $sourceEntityId})
            MERGE (t:Entity {id: $targetEntityId})
            MERGE (s)-[r:RELATED_TO {id: $id}]->(t)
            ON CREATE SET
                r.relation_type      = $relationType,
                r.owner_id           = $ownerId,
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

    /// <summary>Batch merge RELATED_TO relationships by id via UNWIND.</summary>
    public const string UpsertBatch = @"
            UNWIND $items AS item
            MERGE (s:Entity {id: item.source_entity_id})
            MERGE (t:Entity {id: item.target_entity_id})
            MERGE (s)-[r:RELATED_TO {id: item.id}]->(t)
            ON CREATE SET
                r.relation_type      = item.relation_type,
                r.owner_id           = item.owner_id,
                r.source_entity_id   = item.source_entity_id,
                r.target_entity_id   = item.target_entity_id,
                r.confidence         = item.confidence,
                r.description        = item.description,
                r.valid_from         = CASE WHEN item.valid_from IS NOT NULL THEN datetime(item.valid_from) ELSE null END,
                r.valid_until        = CASE WHEN item.valid_until IS NOT NULL THEN datetime(item.valid_until) ELSE null END,
                r.attributes         = item.attributes,
                r.source_message_ids = item.source_message_ids,
                r.created_at         = datetime(item.created_at),
                r.updated_at         = datetime(item.updated_at),
                r.metadata           = item.metadata
            ON MATCH SET
                r.relation_type      = item.relation_type,
                r.confidence         = item.confidence,
                r.description        = item.description,
                r.valid_from         = CASE WHEN item.valid_from IS NOT NULL THEN datetime(item.valid_from) ELSE null END,
                r.valid_until        = CASE WHEN item.valid_until IS NOT NULL THEN datetime(item.valid_until) ELSE null END,
                r.attributes         = item.attributes,
                r.source_message_ids = item.source_message_ids,
                r.updated_at         = datetime(item.updated_at),
                r.metadata           = item.metadata
            RETURN r";
    // ── GetByIdAsync ───────────────────────────────────────────────────

    /// <summary>Get a single RELATED_TO relationship by id.</summary>
    public const string GetById = "MATCH ()-[r:RELATED_TO {id: $id}]->() RETURN r";

    // ── owner/shared filter on the RELATED_TO edge (R1) ─────────────────

    /// <summary>The owner/shared AND-clause for relationship edge <c>r</c>, or empty when unscoped.</summary>
    private static string OwnerAnd(bool hasOwnerFilter, bool includeShared) =>
        !hasOwnerFilter ? string.Empty
        : includeShared ? " AND (r.owner_id = $ownerId OR r.owner_id IS NULL)"
                        : " AND r.owner_id = $ownerId";

    // ── GetByEntityAsync ───────────────────────────────────────────────

    /// <summary>Get all RELATED_TO relationships involving a specific entity (source or target).</summary>
    public static string GetByEntity(bool hasOwnerFilter, bool includeShared) => $@"
            MATCH (s:Entity)-[r:RELATED_TO]->(t:Entity)
            WHERE (s.id = $entityId OR t.id = $entityId){OwnerAnd(hasOwnerFilter, includeShared)}
            RETURN r";

    // ── GetBySourceEntityAsync ─────────────────────────────────────────

    /// <summary>Get all outgoing RELATED_TO relationships from a specific entity.</summary>
    public static string GetBySourceEntity(bool hasOwnerFilter, bool includeShared) =>
        $"MATCH (s:Entity {{id: $sourceEntityId}})-[r:RELATED_TO]->() WHERE true{OwnerAnd(hasOwnerFilter, includeShared)} RETURN r";

    // ── GetByTargetEntityAsync ─────────────────────────────────────────

    /// <summary>Get all incoming RELATED_TO relationships to a specific entity.</summary>
    public static string GetByTargetEntity(bool hasOwnerFilter, bool includeShared) =>
        $"MATCH ()-[r:RELATED_TO]->(t:Entity {{id: $targetEntityId}}) WHERE true{OwnerAnd(hasOwnerFilter, includeShared)} RETURN r";
}
