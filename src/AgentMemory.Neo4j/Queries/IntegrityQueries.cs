namespace AgentMemory.Neo4j.Queries;

/// <summary>
/// G6 (PLAN 40.50): one query per integrity rule. Each returns <c>violations</c> (a count) and <c>examples</c> (up to five
/// ids); <c>$ownerId</c> null checks the whole store.
/// </summary>
public static class IntegrityQueries
{
    /// <summary>An edge between two memories of different owners (shared memories, with no owner, may link to anyone's).</summary>
    public const string EdgeEndpoints = @"
            MATCH (a)-[r]->(b)
            WHERE (a:Fact OR a:Entity OR a:Preference) AND (b:Fact OR b:Entity OR b:Preference)
              AND a.owner_id IS NOT NULL AND b.owner_id IS NOT NULL AND a.owner_id <> b.owner_id
              AND ($ownerId IS NULL OR a.owner_id = $ownerId OR b.owner_id = $ownerId)
            RETURN count(r) AS violations, collect(a.id + ' -[' + type(r) + ']-> ' + b.id)[..5] AS examples";

    /// <summary>A relationship edge owned by someone other than an owned endpoint.</summary>
    public const string EdgeOwner = @"
            MATCH (a:Entity)-[r:RELATED_TO]->(b:Entity)
            WHERE r.owner_id IS NOT NULL
              AND ((a.owner_id IS NOT NULL AND a.owner_id <> r.owner_id) OR (b.owner_id IS NOT NULL AND b.owner_id <> r.owner_id))
              AND ($ownerId IS NULL OR r.owner_id = $ownerId OR a.owner_id = $ownerId OR b.owner_id = $ownerId)
            RETURN count(r) AS violations, collect(coalesce(r.id, a.id + '->' + b.id))[..5] AS examples";

    /// <summary>A fact closed as a change or a correction with no successor to say what replaced it.</summary>
    public const string ClosedWithoutSuccessor = @"
            MATCH (f:Fact)
            WHERE f.invalidated_reason IN ['change', 'correction']
              AND NOT EXISTS { (f)-[:SUPERSEDED_BY]->(:Fact) }
              AND ($ownerId IS NULL OR f.owner_id = $ownerId)
            RETURN count(f) AS violations, collect(f.id)[..5] AS examples";

    /// <summary>A live fact (never invalidated) that nonetheless has a successor.</summary>
    public const string LiveWithSuccessor = @"
            MATCH (f:Fact)-[:SUPERSEDED_BY]->(:Fact)
            WHERE f.invalidated_at IS NULL
              AND ($ownerId IS NULL OR f.owner_id = $ownerId)
            RETURN count(DISTINCT f) AS violations, collect(DISTINCT f.id)[..5] AS examples";

    /// <summary>A validity window that ends before it begins.</summary>
    public const string InvertedWindow = @"
            MATCH (f:Fact)
            WHERE f.valid_from IS NOT NULL AND f.valid_until IS NOT NULL AND f.valid_until < f.valid_from
              AND ($ownerId IS NULL OR f.owner_id = $ownerId)
            RETURN count(f) AS violations, collect(f.id)[..5] AS examples";

    /// <summary>A fact with no source: no source message ids and no EXTRACTED_FROM edge.</summary>
    public const string FactWithoutSource = @"
            MATCH (f:Fact)
            WHERE size(coalesce(f.source_message_ids, [])) = 0
              AND NOT EXISTS { (f)-[:EXTRACTED_FROM]->(:Message) }
              AND ($ownerId IS NULL OR f.owner_id = $ownerId)
            RETURN count(f) AS violations, collect(f.id)[..5] AS examples";

    /// <summary>
    /// A live fact, preference or entity with no embedding. A write whose embedding failed stores the memory without one
    /// (the failure is only logged), and recall by meaning never finds it again;
    /// <c>IMemoryMaintenance.GenerateEmbeddingsBatchAsync</c> repairs it.
    /// </summary>
    public const string MemoryWithoutEmbedding = @"
            MATCH (m)
            WHERE (m:Fact OR m:Preference OR m:Entity) AND m.embedding IS NULL AND m.invalidated_at IS NULL
              AND ($ownerId IS NULL OR m.owner_id = $ownerId)
            RETURN count(m) AS violations, collect(m.id)[..5] AS examples";
}
