namespace Neo4j.AgentMemory.Neo4j.Queries;

/// <summary>
/// Centralized Cypher queries for Entity operations.
/// Each constant or method corresponds to exactly one repository method in
/// <see cref="Neo4j.AgentMemory.Neo4j.Repositories.Neo4jEntityRepository"/>.
/// </summary>
public static class EntityQueries
{
    // ── UpsertAsync ────────────────────────────────────────────────────

    /// <summary>Merge an entity by id, setting all properties on create/match.</summary>
    public const string Upsert = @"
            MERGE (e:Entity {id: $id})
            ON CREATE SET
                e.name               = $name,
                e.canonical_name     = $canonicalName,
                e.type               = $type,
                e.subtype            = $subtype,
                e.description        = $description,
                e.confidence         = $confidence,
                e.aliases            = $aliases,
                e.attributes         = $attributes,
                e.source_message_ids = $sourceMessageIds,
                e.created_at         = datetime($createdAtUtc),
                e.metadata           = $metadata
            ON MATCH SET
                e.name               = $name,
                e.canonical_name     = $canonicalName,
                e.type               = $type,
                e.subtype            = $subtype,
                e.description        = $description,
                e.confidence         = $confidence,
                e.aliases            = $aliases,
                e.attributes         = $attributes,
                e.source_message_ids = $sourceMessageIds,
                e.metadata           = $metadata,
                e.updated_at         = datetime()
            RETURN e";

    // ── GetByIdAsync ───────────────────────────────────────────────────

    /// <summary>Get a single entity by id.</summary>
    public const string GetById = "MATCH (e:Entity {id: $id}) RETURN e";

    // ── GetByNameAsync ─────────────────────────────────────────────────

    /// <summary>Get entities matching name or aliases.</summary>
    public const string GetByNameWithAliases =
        "MATCH (e:Entity) WHERE e.name = $name OR $name IN e.aliases RETURN e";

    /// <summary>Get entities matching exact name only.</summary>
    public const string GetByNameOnly =
        "MATCH (e:Entity {name: $name}) RETURN e";

    /// <summary>Returns the appropriate GetByName query based on <paramref name="includeAliases"/>.</summary>
    public static string GetByName(bool includeAliases) =>
        includeAliases ? GetByNameWithAliases : GetByNameOnly;

    // ── SearchByVectorAsync ────────────────────────────────────────────

    /// <summary>Vector similarity search on entity embeddings.</summary>
    public const string SearchByVector = @"
            CALL db.index.vector.queryNodes('entity_embedding_idx', $limit, $embedding)
            YIELD node, score
            WHERE score >= $minScore
            RETURN node, score
            ORDER BY score DESC";

    // ── GetByTypeAsync ─────────────────────────────────────────────────

    /// <summary>Get all entities of a given type.</summary>
    public const string GetByType = "MATCH (e:Entity {type: $type}) RETURN e";

    // ── SearchByNameAsync ──────────────────────────────────────────────

    /// <summary>Case-insensitive name search (no type filter).</summary>
    public const string SearchByName =
        "MATCH (e:Entity) WHERE toLower(e.name) CONTAINS toLower($name) OR toLower(e.canonical_name) CONTAINS toLower($name) RETURN e";

    /// <summary>Case-insensitive name search filtered by type.</summary>
    public const string SearchByNameWithType =
        "MATCH (e:Entity {type: $type}) WHERE toLower(e.name) CONTAINS toLower($name) OR toLower(e.canonical_name) CONTAINS toLower($name) RETURN e";

    /// <summary>Returns the appropriate SearchByName query based on whether <paramref name="type"/> is provided.</summary>
    public static string SearchByNameFiltered(string? type) =>
        type is null ? SearchByName : SearchByNameWithType;

    // ── AddMentionAsync ────────────────────────────────────────────────

    /// <summary>Create MENTIONS relationship from Message to Entity.</summary>
    public const string AddMention = @"
            MATCH (m:Message {id: $messageId})
            MATCH (e:Entity {id: $entityId})
            MERGE (m)-[r:MENTIONS]->(e)
            ON CREATE SET r.confidence = $confidence, r.start_pos = $startPos, r.end_pos = $endPos, r.context = $context, r.created_at = datetime()";

    // ── AddMentionsBatchAsync ──────────────────────────────────────────

    /// <summary>Batch create MENTIONS relationships from a Message to multiple Entities.</summary>
    public const string AddMentionsBatch = @"
            MATCH (m:Message {id: $messageId})
            UNWIND $entityIds AS eid
            MATCH (e:Entity {id: eid})
            MERGE (m)-[r:MENTIONS]->(e)
            ON CREATE SET r.confidence = $confidence, r.created_at = datetime()";

    // ── AddSameAsRelationshipAsync ─────────────────────────────────────

    /// <summary>Merge SAME_AS relationship between two entities with confidence tracking.</summary>
    public const string AddSameAs = @"
            MATCH (e1:Entity {id: $entityId1})
            MATCH (e2:Entity {id: $entityId2})
            MERGE (e1)-[r:SAME_AS]->(e2)
            ON CREATE SET r.confidence = $confidence, r.match_type = $matchType, r.created_at = datetime(), r.status = $status
            ON MATCH SET r.confidence = CASE WHEN $confidence > r.confidence THEN $confidence ELSE r.confidence END, r.updated_at = datetime()";

    // ── GetSameAsEntitiesAsync ──────────────────────────────────────────

    /// <summary>Get all entities linked by SAME_AS to a given entity.</summary>
    public const string GetSameAsEntities = @"
            MATCH (e:Entity {id: $entityId})-[r:SAME_AS]-(other:Entity)
            RETURN other, r.confidence AS confidence, r.match_type AS matchType";

    // ── UpsertBatchAsync ───────────────────────────────────────────────

    /// <summary>Batch merge entities by id via UNWIND.</summary>
    public const string UpsertBatch = @"
            UNWIND $items AS item
            MERGE (e:Entity {id: item.id})
            ON CREATE SET
                e.name               = item.name,
                e.canonical_name     = item.canonical_name,
                e.type               = item.type,
                e.subtype            = item.subtype,
                e.description        = item.description,
                e.confidence         = item.confidence,
                e.aliases            = item.aliases,
                e.attributes         = item.attributes,
                e.source_message_ids = item.source_message_ids,
                e.created_at         = datetime(item.created_at),
                e.metadata           = item.metadata
            ON MATCH SET
                e.name               = item.name,
                e.canonical_name     = item.canonical_name,
                e.type               = item.type,
                e.subtype            = item.subtype,
                e.description        = item.description,
                e.confidence         = item.confidence,
                e.aliases            = item.aliases,
                e.attributes         = item.attributes,
                e.source_message_ids = item.source_message_ids,
                e.metadata           = item.metadata,
                e.updated_at         = datetime()
            RETURN e";

    // ── CreateExtractedFromRelationshipAsync ────────────────────────────

    /// <summary>Merge EXTRACTED_FROM with optional confidence/position metadata.</summary>
    public const string CreateExtractedFrom = @"
                MATCH (e:Entity {id: $entityId}), (m:Message {id: $messageId})
                MERGE (e)-[r:EXTRACTED_FROM]->(m)
                ON CREATE SET r.confidence = $confidence, r.start_pos = $startPos, r.end_pos = $endPos, r.context = $context, r.created_at = datetime()
                ON MATCH SET r.confidence = CASE WHEN $confidence IS NOT NULL AND ($confidence > r.confidence OR r.confidence IS NULL) THEN $confidence ELSE r.confidence END";

    // ── MergeEntitiesAsync ─────────────────────────────────────────────

    /// <summary>Merge a source entity into a target entity, transferring relationships and aliases.</summary>
    public const string MergeEntities = @"
            MATCH (source:Entity {id: $sourceEntityId})
            MATCH (target:Entity {id: $targetEntityId})
            CALL (source, target) {
                MATCH (source)<-[:MENTIONS]-(m:Message)
                WHERE NOT (m)-[:MENTIONS]->(target)
                MERGE (m)-[:MENTIONS]->(target)
                RETURN count(*) AS mentionsTransferred
            }
            CALL (source, target) {
                MATCH (source)-[r:SAME_AS]-(other:Entity)
                WHERE other <> target AND NOT (target)-[:SAME_AS]-(other)
                MERGE (target)-[:SAME_AS {confidence: r.confidence, match_type: r.match_type, created_at: datetime()}]-(other)
                RETURN count(*) AS sameAsTransferred
            }
            SET source.merged_into = target.id, source.merged_at = datetime()
            WITH source, target,
                 coalesce(target.aliases, []) +
                 [x IN ([source.name] + coalesce(source.aliases, []))
                  WHERE NOT x IN coalesce(target.aliases, [])] AS mergedAliases
            SET target.aliases = mergedAliases,
                target.description = CASE
                    WHEN target.description IS NULL THEN source.description
                    WHEN source.description IS NULL OR target.description CONTAINS source.description THEN target.description
                    ELSE target.description + ' ' + source.description
                END,
                target.embedding = null,
                target.updated_at = datetime()
            RETURN source, target";

    // ── RefreshEntitySearchFieldsAsync ──────────────────────────────────

    /// <summary>Refresh search-relevant fields (aliases cleanup, updated_at).</summary>
    public const string RefreshSearchFields = @"
            MATCH (e:Entity {id: $entityId})
            SET e.updated_at = datetime($updatedAt),
                e.aliases    = [x IN coalesce(e.aliases, []) WHERE x IS NOT NULL AND size(toString(x)) > 0]
            RETURN e";

    // ── SearchByLocationAsync ──────────────────────────────────────────

    /// <summary>Spatial proximity search within a radius (km converted to meters by caller).</summary>
    public const string SearchByLocation = @"
            MATCH (e:Entity)
            WHERE e.location IS NOT NULL
              AND point.distance(e.location, point({latitude: $lat, longitude: $lon})) < $radiusMeters
            RETURN e
            ORDER BY point.distance(e.location, point({latitude: $lat, longitude: $lon}))
            LIMIT $limit";

    // ── SearchInBoundingBoxAsync ────────────────────────────────────────

    /// <summary>Spatial bounding-box search.</summary>
    public const string SearchInBoundingBox = @"
            MATCH (e:Entity)
            WHERE e.location IS NOT NULL
              AND point.withinBBox(
                    e.location,
                    point({longitude: $minLon, latitude: $minLat}),
                    point({longitude: $maxLon, latitude: $maxLat}))
            RETURN e
            LIMIT $limit";

    // ── GetPageWithoutEmbeddingAsync ────────────────────────────────────

    /// <summary>Get entities that have no embedding yet (for background embedding jobs).</summary>
    public const string GetPageWithoutEmbedding =
        "MATCH (e:Entity) WHERE e.embedding IS NULL RETURN e LIMIT $limit";

    // ── UpdateEmbeddingAsync ───────────────────────────────────────────

    /// <summary>Update embedding for a single entity (same as SharedFragments.SetEntityEmbedding).</summary>
    public const string UpdateEmbedding =
        "MATCH (e:Entity {id: $id}) SET e.embedding = $embedding";

    // ── DeleteAsync ────────────────────────────────────────────────────

    /// <summary>Detach-delete an entity by id and report whether it existed.</summary>
    public const string Delete = @"
            MATCH (e:Entity {id: $entityId})
            DETACH DELETE e
            RETURN count(e) > 0 AS deleted";
}
