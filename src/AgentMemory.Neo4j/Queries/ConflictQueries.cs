namespace AgentMemory.Neo4j.Queries;

/// <summary>
/// Cypher for conflict / contradiction detection (detect-only). Groups facts by subject + predicate
/// within an owner scope (<c>coalesce(owner_id, '*')</c>, so users don't conflict across the isolation
/// boundary) and surfaces groups asserting more than one distinct object.
/// </summary>
public static class ConflictQueries
{
    /// <summary>
    /// Finds fact contradictions: same subject + predicate + owner with ≥2 distinct objects. Optionally
    /// gated by <c>$minConfidence</c> (null ⇒ no gate). Returns one row per conflict group with the
    /// members (id/object/confidence). Capped by <c>$limit</c>.
    /// </summary>
    public const string DetectFactContradictions = @"
            MATCH (f:Fact)
            WHERE ($minConfidence IS NULL OR f.confidence >= $minConfidence)
            WITH f.subject AS subject, f.predicate AS predicate, coalesce(f.owner_id, '*') AS ownerKey,
                 collect(DISTINCT f.object) AS distinctObjects,
                 collect({ factId: f.id, object: f.object, confidence: f.confidence }) AS members
            WHERE size(distinctObjects) > 1
            RETURN subject, predicate, ownerKey, members
            ORDER BY subject, predicate
            LIMIT $limit";
}
