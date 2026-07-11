namespace AgentMemory.Tests.Integration.Compatibility;

internal sealed record CompatibilityScenario(
    string Id,
    string Tier,
    string Feature,
    string Mode,
    string Description,
    IReadOnlyList<string> UpstreamScenarioIds); // NEW — stable upstream SCN-* IDs this row mirrors

internal static class CompatibilityScenarioCatalog
{
    public static IReadOnlyList<CompatibilityScenario> Scenarios { get; } =
    [
        new(
            "NET-TCK-B-001",
            "Bronze",
            "Short-term memory",
            "Upstream-mirrored",
            "Conversation/message/session persistence, newest-first recent reads, vector message search, and session clear.",
            // All 6 IDs are upstream-confirmed against neo4j-labs/agent-memory-tck's
            // tck/registry/scenario_ids.yaml (commit 4603b91f, main, fetched 2026-07-11 via the
            // GitHub contents API — not the WebFetch summarizer, which hallucinated a wrong
            // description for SCN-B-079 on a first pass; the raw YAML was pulled and grepped
            // directly to get ground truth). Confirmed descriptions:
            //   SCN-B-001 (SPEC-1.1.1) "First message creates conversation node"
            //   SCN-B-002 (SPEC-1.1.2) "Subsequent messages reuse existing conversation"
            //   SCN-B-043 (SPEC-2.2.1) "get_conversation returns messages in insertion order"
            //   SCN-B-044 (SPEC-2.2.2) "get_conversation respects limit parameter"
            //   SCN-B-055 (SPEC-2.3.1) "Search finds relevant messages"
            //   SCN-B-079 (SPEC-2.6.1) "clear_session removes all messages"
            // All match this row's claims exactly; no re-verification caveat remains.
            ["SCN-B-001", "SCN-B-002", "SCN-B-043", "SCN-B-044", "SCN-B-055", "SCN-B-079"]),
        new(
            "NET-TCK-S-001",
            "Silver",
            "Long-term memory",
            "Upstream-mirrored + .NET owner isolation",
            "Entity, fact, and preference round-trips through indexed lookups with private/shared owner boundaries.",
            // Silver/Gold/Platinum SCN-* enumeration is a follow-up slice, not this one.
            []),
        new(
            "NET-TCK-S-002",
            "Silver",
            "Reasoning memory",
            "Upstream-mirrored + .NET owner isolation",
            "Trace, step, tool-call, completion, and owner-scoped trace listing behavior.",
            // Silver/Gold/Platinum SCN-* enumeration is a follow-up slice, not this one.
            []),
        new(
            "NET-TCK-G-001",
            "Gold",
            "Relationships and provenance",
            "Upstream-mirrored + .NET owner isolation",
            "Relationship traversal and reasoning-step touched-entity provenance.",
            // Silver/Gold/Platinum SCN-* enumeration is a follow-up slice, not this one.
            []),
        new(
            "NET-TCK-G-002",
            "Gold",
            "Temporal history and read audit",
            ".NET enhanced",
            "Supersession history, invalidated/live filtering, read access timestamps, and MemoryReadAudit rows.",
            // Silver/Gold/Platinum SCN-* enumeration is a follow-up slice, not this one.
            []),
        new(
            "NET-TCK-S-003",
            "Silver",
            "Retrieval fixture",
            "Upstream-mirrored",
            "Fixed-vector retrieval fixture that returns the expected target memory at rank one.",
            // Silver/Gold/Platinum SCN-* enumeration is a follow-up slice, not this one.
            []),
        new(
            "NET-STRICT-R1-001",
            ".NET strict",
            "Owner isolation",
            ".NET stricter than upstream",
            "Negative controls prove another owner's private memories do not leak through reads or relationship queries.",
            // Silver/Gold/Platinum SCN-* enumeration is a follow-up slice, not this one.
            []),
        new(
            "NET-RANK-D1-001",
            ".NET enhanced",
            "Recency/frequency reranker",
            ".NET enhanced",
            "Opt-in ranking blend proves temporal recency and repeated access frequency can reorder vector recall.",
            // Silver/Gold/Platinum SCN-* enumeration is a follow-up slice, not this one.
            []),
        new(
            "NET-GOLDEN-001",
            ".NET golden path",
            "AgentWithMemory real-provider seam",
            ".NET golden path",
            "MAF golden path keeps chat and embedding providers replaceable while preserving identity-scoped memory wiring.",
            // Silver/Gold/Platinum SCN-* enumeration is a follow-up slice, not this one.
            []),
    ];
}
