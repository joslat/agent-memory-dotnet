namespace Neo4j.AgentMemory.Neo4j.Queries;

/// <summary>
/// Centralized Cypher queries for Extractor operations.
/// </summary>
public static class ExtractorQueries
{
    /// <summary>Upsert an Extractor node by name.</summary>
    public const string Upsert = @"
            MERGE (ex:Extractor {name: $name})
            ON CREATE SET ex.id = $id, ex.version = $version, ex.config = $config, ex.created_at = datetime()
            ON MATCH SET ex.version = COALESCE($version, ex.version), ex.config = COALESCE($config, ex.config)
            RETURN ex";

    /// <summary>Get an Extractor by name.</summary>
    public const string GetByName = "MATCH (ex:Extractor {name: $name}) RETURN ex";

    /// <summary>List all Extractors, ordered by name.</summary>
    public const string List = "MATCH (ex:Extractor) RETURN ex ORDER BY ex.name";

    /// <summary>Create or update an EXTRACTED_BY relationship between an Entity and an Extractor.</summary>
    public const string CreateExtractedByRelationship = @"
            MATCH (e:Entity {id: $entity_id})
            MATCH (ex:Extractor {name: $extractor_name})
            MERGE (e)-[r:EXTRACTED_BY]->(ex)
            ON CREATE SET r.confidence = $confidence, r.extraction_time_ms = $extraction_time_ms, r.created_at = datetime()
            RETURN r";

    /// <summary>Get Entities extracted by a specific Extractor, with confidence scores.</summary>
    public const string GetEntitiesByExtractor = @"
            MATCH (ex:Extractor {name: $extractor_name})<-[r:EXTRACTED_BY]-(e:Entity)
            RETURN e, r.confidence AS confidence
            ORDER BY e.created_at DESC
            LIMIT $limit";

    // ── GetEntityProvenance ────────────────────────────────────────────

    /// <summary>Get full provenance for an entity: source messages and extractors.</summary>
    public const string GetEntityProvenance = @"
            MATCH (e:Entity {id: $entityId})
            OPTIONAL MATCH (e)-[ef:EXTRACTED_FROM]->(m:Message)
            OPTIONAL MATCH (e)-[eb:EXTRACTED_BY]->(ex:Extractor)
            RETURN e.id AS entityId,
                   collect(DISTINCT {messageId: m.id, confidence: ef.confidence, startPos: ef.start_position, endPos: ef.end_position}) AS sources,
                   collect(DISTINCT {extractorName: ex.name, confidence: eb.confidence, extractionTimeMs: eb.extraction_time_ms}) AS extractors";

    // ── GetExtractionStats ─────────────────────────────────────────────

    /// <summary>Get aggregate extraction statistics.</summary>
    public const string GetExtractionStats = @"
            OPTIONAL MATCH (e:Entity)
            WITH COUNT(e) AS totalEntities
            OPTIONAL MATCH (m:Message)<-[:EXTRACTED_FROM]-(:Entity)
            WITH totalEntities, COUNT(DISTINCT m) AS totalMessages
            RETURN totalEntities, totalMessages,
                   CASE WHEN totalMessages > 0 THEN toFloat(totalEntities) / totalMessages ELSE 0.0 END AS avgPerMessage";

    // ── GetExtractorStats ──────────────────────────────────────────────

    /// <summary>Get statistics for a specific extractor.</summary>
    public const string GetExtractorStats = @"
            MATCH (ex:Extractor {name: $extractorName})
            OPTIONAL MATCH (ex)<-[eb:EXTRACTED_BY]-(e:Entity)
            RETURN ex.name AS name, COUNT(e) AS entityCount, AVG(eb.confidence) AS avgConfidence, COUNT(eb) AS totalExtractions";

    // ── DeleteEntityProvenance ─────────────────────────────────────────

    /// <summary>Delete all provenance relationships for an entity.</summary>
    public const string DeleteEntityProvenance = @"
            MATCH (e:Entity {id: $entityId})
            OPTIONAL MATCH (e)-[ef:EXTRACTED_FROM]->()
            OPTIONAL MATCH (e)-[eb:EXTRACTED_BY]->()
            DELETE ef, eb
            RETURN COUNT(ef) + COUNT(eb) AS deleted";
}
