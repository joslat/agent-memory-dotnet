namespace AgentMemory.Neo4j.Queries;

/// <summary>
/// Cypher for conflict / contradiction detection (detect-only). Groups facts by subject + predicate
/// within an owner scope (<c>coalesce(owner_id, '*')</c>, so users don't conflict across the isolation
/// boundary) and surfaces groups asserting more than one distinct object. Only <b>live</b> facts are
/// considered (<c>invalidated_at IS NULL</c>): an already-superseded/invalidated fact is resolved
/// history, not a current contradiction — and this also makes the D7 resolution path that reuses this
/// query idempotent (a superseded loser drops out, so a re-run finds nothing) and guarantees the
/// resolution winner is a live fact.
/// </summary>
public static class ConflictQueries
{
    /// <summary>
    /// Finds fact contradictions among <b>live</b> facts: same subject + predicate + owner with ≥2
    /// distinct objects, ignoring soft-invalidated facts. Optionally gated by <c>$minConfidence</c>
    /// (null ⇒ no gate). Returns one row per conflict group with the members (id/object/confidence).
    /// Capped by <c>$limit</c>.
    /// </summary>
    public const string DetectFactContradictions = @"
            MATCH (f:Fact)
            WHERE f.invalidated_at IS NULL
              AND ($minConfidence IS NULL OR f.confidence >= $minConfidence)
            WITH f.subject AS subject, f.predicate AS predicate, coalesce(f.owner_id, '*') AS ownerKey,
                 collect(DISTINCT f.object) AS distinctObjects,
                 collect({ factId: f.id, object: f.object, confidence: f.confidence }) AS members
            WHERE size(distinctObjects) > 1
            RETURN subject, predicate, ownerKey, members
            ORDER BY subject, predicate
            LIMIT $limit";
}
