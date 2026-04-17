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
}
