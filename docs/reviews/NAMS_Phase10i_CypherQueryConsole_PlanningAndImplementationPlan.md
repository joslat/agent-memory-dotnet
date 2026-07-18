# NAMS Phase 10i: Cypher Query Console — Planning & Implementation Plan

## Scope, and why this phase required explicit approval

Adds the TCK Platinum `cypher_query` capability to `INamsClient`: `POST /v1/query`, a caller-supplied
Cypher passthrough against the tenant's graph database. Unlike every other Phase 10e-10h addition, this
one was **deliberately not started under the general autonomous-execution authorization** — it was
called out from the first scoping pass (`neo4j-meeting-2026-07-nams-priority.md`) as needing the user's
explicit go-ahead, the same treatment as Phase 12 (release), because it's a security/design decision
(raw Cypher passthrough) rather than a routine capability add, even though NAMS enforces read-only
server-side. The user was asked directly after Phase 10h merged and confirmed proceeding.

Same tier as every prior Phase 10 addition otherwise: low-level `INamsClient` capability only (already
`internal`, not part of the public NuGet surface), **not** wired into any higher-level service or MCP
tool in this phase — a caller-facing exposure decision (e.g. a new MCP tool, or a public service method)
is explicitly a separate, later decision, not bundled into this one.

## Design, informed by the Phase 10e live-probe spike

Both the request/response shape and the read-only guarantee were already confirmed live in Phase 10e —
no new probing needed. Key finding from that probe, worth restating because it's the crux of why this
capability is acceptable to add at all: a real `CREATE` write attempt against the live NAMS SaaS was
rejected with **HTTP 400**, `{"error":"write operations are not permitted via this endpoint"}` — this is
a genuine, server-enforced guarantee, not merely a documentation claim. This phase's own live test
re-confirms that guarantee empirically as part of the merged suite (see below), rather than relying on
the Phase 10e finding alone.

```csharp
internal sealed record NamsQueryResult(
    [property: JsonPropertyName("columns")] IReadOnlyList<string> Columns,
    [property: JsonPropertyName("rows")] IReadOnlyList<IReadOnlyDictionary<string, JsonElement>> Rows,
    [property: JsonPropertyName("stats")] IReadOnlyDictionary<string, JsonElement>? Stats);
```

Rows are per-record dictionaries keyed by column name with heterogeneous values (confirmed live:
`{"columns":["cnt"],"rows":[{"cnt":541}],"stats":{...}}`) — modeled as `JsonElement` values, matching the
precedent already established for `NamsExpandNode.Properties` (Phase 10g) and `NamsEntityProvenance`
(Phase 10h) for genuinely heterogeneous/untyped wire payloads. `stats` is likewise untyped per the pinned
schema (`additionalProperties: true`) — the live probe saw a fixed set of int counters, but modeling it
generically avoids brittleness if NAMS adds/removes fields.

`ExecuteCypherQueryAsync(string cypher, IReadOnlyDictionary<string, object?>? parameters, CancellationToken)`
— named with the full "Cypher" qualifier (not just "Query") to avoid ambiguity with this codebase's
existing `Search*Async` methods, which are also loosely "queries." `parameters` accepts a heterogeneous
dictionary (Cypher params can be any JSON-compatible value: string/number/bool/null/nested
list-or-map) and serializes generically via `System.Text.Json`'s built-in object handling.

## Idempotency classification

A `POST` verb, but read-only by the endpoint's own server-enforced contract (confirmed live) — same
idempotent-for-retry treatment as `SearchEntitiesAsync`/`ExpandGraphAsync`.

## Implementation checklist

1. New domain record `NamsQueryResult` (`src/AgentMemory.Nams/Domain/`, one file).
2. `INamsClient`: add `ExecuteCypherQueryAsync`, with a doc comment that states the read-only guarantee
   is server-enforced (confirmed live) and explicitly notes this is not wired into any higher-level
   service or MCP tool — exposing raw Cypher to an agent/end-user is a separate, later decision.
3. `Neo4jNamsClientAdapter`: implement it; wire-only `ExecuteCypherQueryRequestBody` record.
4. Live tests (`tests/AgentMemory.Tests.Integration/Nams/NamsCypherQueryTests.cs`, new file,
   `LiveNamsFactAttribute`-gated):
   - `ExecuteCypherQueryAsync_ReadQuery_ReturnsRealResults`: run a simple, genuinely informative read
     query (`MATCH (n) RETURN count(n) AS cnt`) and assert the response has the expected column name and
     a real (not hardcoded) row value — proves live round-trip, not just "didn't throw."
   - `ExecuteCypherQueryAsync_WriteAttempt_IsRejectedByTheServer`: attempt a real `CREATE` against the
     live dev workspace and assert it throws with a failure kind mapping the documented 400 — the single
     most important test in this phase, since it's the empirical proof the safety property this whole
     phase's approval was conditioned on actually holds, not just documented.
   - `ExecuteCypherQueryAsync_WithParameters_SubstitutesThemCorrectly`: pass a parameterized query
     (`MATCH (n) WHERE n.id = $id RETURN n.id AS id` or similar safe read) with a real param value and
     assert the substitution genuinely happened — proves parameters aren't silently dropped/ignored.
5. Unit-level wire-shape tests in `Neo4jNamsClientAdapterTests.cs`, using the exact confirmed JSON shapes
   above, plus a test confirming a 400 response maps to a client-thrown exception (not silently swallowed).
