namespace Neo4j.AgentMemory.Neo4j.Queries;

/// <summary>
/// Reusable Cypher fragments shared across multiple domain query classes.
/// </summary>
public static class SharedFragments
{
    // ── Embedding operations ───────────────────────────────────────────

    /// <summary>Set embedding on an Entity node by id.</summary>
    public const string SetEntityEmbedding =
        "MATCH (e:Entity {id: $id}) SET e.embedding = $embedding";

    /// <summary>Set embedding on a Fact node by id.</summary>
    public const string SetFactEmbedding =
        "MATCH (f:Fact {id: $id}) SET f.embedding = $embedding";

    /// <summary>Set embedding on a Message node by id.</summary>
    public const string SetMessageEmbedding =
        "MATCH (m:Message {id: $id}) SET m.embedding = $embedding";

    // ── Geospatial ─────────────────────────────────────────────────────

    /// <summary>Set geospatial location on an Entity node.</summary>
    public const string SetEntityLocation =
        "MATCH (e:Entity {id: $id}) SET e.location = point({latitude: $lat, longitude: $lon})";

    // ── EXTRACTED_FROM relationships ───────────────────────────────────

    /// <summary>Link an Entity to its source Messages via EXTRACTED_FROM.</summary>
    public const string LinkEntityExtractedFrom = @"
        MATCH (e:Entity {id: $id})
        UNWIND $sourceMessageIds AS msgId
        MATCH (m:Message {id: msgId})
        MERGE (e)-[:EXTRACTED_FROM]->(m)";

    /// <summary>Link a Fact to its source Messages via EXTRACTED_FROM.</summary>
    public const string LinkFactExtractedFrom = @"
        MATCH (f:Fact {id: $id})
        UNWIND $sourceMessageIds AS msgId
        MATCH (m:Message {id: msgId})
        MERGE (f)-[:EXTRACTED_FROM]->(m)";
}
