namespace AgentMemory.Neo4j.Infrastructure;

/// <summary>
/// One pre-canonical fact awaiting key backfill. Named rather than anonymous so the migration's
/// read shape is part of the type system and can be stubbed in tests.
/// </summary>
internal sealed record CanonicalKeyBackfillRow(
    string Id,
    string Subject,
    string Predicate,
    string Object);
