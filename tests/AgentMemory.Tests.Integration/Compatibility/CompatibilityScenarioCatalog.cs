namespace AgentMemory.Tests.Integration.Compatibility;

internal sealed record CompatibilityScenario(
    string Id,
    string Tier,
    string Feature,
    string Mode,
    string Description);

internal static class CompatibilityScenarioCatalog
{
    public static IReadOnlyList<CompatibilityScenario> Scenarios { get; } =
    [
        new(
            "NET-TCK-B-001",
            "Bronze",
            "Short-term memory",
            "Upstream-mirrored",
            "Conversation/message/session persistence, newest-first recent reads, vector message search, and session clear."),
        new(
            "NET-TCK-S-001",
            "Silver",
            "Long-term memory",
            "Upstream-mirrored + .NET owner isolation",
            "Entity, fact, and preference round-trips through indexed lookups with private/shared owner boundaries."),
        new(
            "NET-TCK-S-002",
            "Silver",
            "Reasoning memory",
            "Upstream-mirrored + .NET owner isolation",
            "Trace, step, tool-call, completion, and owner-scoped trace listing behavior."),
        new(
            "NET-TCK-G-001",
            "Gold",
            "Relationships and provenance",
            "Upstream-mirrored + .NET owner isolation",
            "Relationship traversal and reasoning-step touched-entity provenance."),
        new(
            "NET-TCK-G-002",
            "Gold",
            "Temporal history and read audit",
            ".NET enhanced",
            "Supersession history, invalidated/live filtering, read access timestamps, and MemoryReadAudit rows."),
        new(
            "NET-TCK-S-003",
            "Silver",
            "Retrieval fixture",
            "Upstream-mirrored",
            "Fixed-vector retrieval fixture that returns the expected target memory at rank one."),
        new(
            "NET-STRICT-R1-001",
            ".NET strict",
            "Owner isolation",
            ".NET stricter than upstream",
            "Negative controls prove another owner's private memories do not leak through reads or relationship queries."),
        new(
            "NET-RANK-D1-001",
            ".NET enhanced",
            "Recency/frequency reranker",
            ".NET enhanced",
            "Opt-in ranking blend proves temporal recency and repeated access frequency can reorder vector recall."),
        new(
            "NET-GOLDEN-001",
            ".NET golden path",
            "AgentWithMemory real-provider seam",
            ".NET golden path",
            "MAF golden path keeps chat and embedding providers replaceable while preserving identity-scoped memory wiring."),
    ];
}
