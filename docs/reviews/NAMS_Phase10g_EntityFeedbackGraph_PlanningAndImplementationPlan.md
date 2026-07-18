# NAMS Phase 10g: Entity Feedback + Graph — Planning & Implementation Plan

## Scope

Add the TCK Platinum entity-feedback and graph capabilities to `INamsClient`:

- `set_entity_feedback` (`PUT /entities/{id}/feedback`)
- `get_entity_graph` (`GET /entities/graph`)
- graph expand (`POST /graph/expand`) — not itself a named Platinum scenario, but the natural pair to
  `get_entity_graph` per the engineering plan's own §5.6 description of NAMS's graph-view capability,
  and already fully shape-confirmed by the Phase 10e spike, so bundling it into this phase avoids a
  near-duplicate phase later for one more endpoint.

Same tier as every other Phase 10e/10f/10a/10b addition: low-level `INamsClient` capability only, not
wired into any higher-level service or MCP tool. `POST /entities` (manual entity creation) is explicitly
**out of scope** — it's not part of any TCK Platinum scenario for this area, and adding it now would be
scope creep beyond this phase's plan; live tests instead reuse an existing entity discovered via the
already-shipped `ListEntitiesAsync` (Phase 9), the same non-destructive pattern the Phase 10e spike
itself used.

## Design, informed by the Phase 10e live-probe spike

All three shapes were already confirmed live in Phase 10e — no new probing needed before implementing.

### Entity feedback

`PUT /entities/{id}/feedback`, body `{userScore?, confirmed?}` (both optional per the pinned schema),
response `{id, updated: true}`. New minimal result record:

```csharp
internal sealed record NamsEntityFeedbackResult(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("updated")] bool Updated);
```

`SetEntityFeedbackAsync(string entityId, double? userScore, bool? confirmed, CancellationToken)`.

### Entity graph

`GET /entities/graph` (no parameters — returns the whole workspace graph). Live-confirmed node shape is
**identical to the already-existing `NamsEntity`** record (id/name/type/description/confidence/
sourceStage/createdAt/updatedAt) — no new node type needed, just reuse `NamsEntity`. Edges need a new
shared record:

```csharp
internal sealed record NamsGraphEdge(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("sourceId")] string SourceId,
    [property: JsonPropertyName("targetId")] string TargetId,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("legacyType")] string? LegacyType,
    [property: JsonPropertyName("confidence")] double? Confidence,
    [property: JsonPropertyName("method")] string? Method,
    [property: JsonPropertyName("predicate")] string? Predicate,
    [property: JsonPropertyName("sourceMessageCount")] int? SourceMessageCount);

internal sealed record NamsEntityGraph(
    [property: JsonPropertyName("nodes")] IReadOnlyList<NamsEntity> Nodes,
    [property: JsonPropertyName("edges")] IReadOnlyList<NamsGraphEdge> Edges);
```

`GetEntityGraphAsync(CancellationToken)`.

### Graph expand — genuinely different node shape (confirmed Phase 10e)

`POST /graph/expand`, body `{nodeId, loadedIds}`. **Nodes here are NOT `NamsEntity`** — expand can pull
in non-Entity nodes (a `Message` node was observed live), so nodes are generic: `{id, labels: [...],
properties: {...}}`. Reuses `NamsGraphEdge` for edges (same shape confirmed). New records:

```csharp
internal sealed record NamsExpandNode(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("labels")] IReadOnlyList<string> Labels,
    [property: JsonPropertyName("properties")] IReadOnlyDictionary<string, JsonElement> Properties);

internal sealed record NamsExpandTruncation(
    [property: JsonPropertyName("nodeId")] string? NodeId,
    [property: JsonPropertyName("shown")] int Shown,
    [property: JsonPropertyName("total")] int Total);

internal sealed record NamsGraphExpansion(
    [property: JsonPropertyName("nodes")] IReadOnlyList<NamsExpandNode> Nodes,
    [property: JsonPropertyName("edges")] IReadOnlyList<NamsGraphEdge> Edges,
    [property: JsonPropertyName("truncated")] NamsExpandTruncation? Truncated);
```

`ExpandGraphAsync(string nodeId, IReadOnlyList<string> loadedIds, CancellationToken)`. `Properties` uses
`JsonElement` (not a typed record) because the property bag is genuinely heterogeneous across node
labels (`Message` vs. `Entity` vs. whatever else the graph contains) — matching how this codebase already
treats other untyped/heterogeneous wire payloads.

## Implementation checklist

1. New domain records: `NamsEntityFeedbackResult`, `NamsGraphEdge`, `NamsEntityGraph`, `NamsExpandNode`,
   `NamsExpandTruncation`, `NamsGraphExpansion` (`src/AgentMemory.Nams/Domain/`) -- one file per record,
   matching every existing domain type's convention (no bundling multiple records into one file).
2. `INamsClient`: add `SetEntityFeedbackAsync`, `GetEntityGraphAsync`, `ExpandGraphAsync`, each with a
   doc comment following the established convention.
3. `Neo4jNamsClientAdapter`: implement all three. `SetEntityFeedbackAsync` is a `PUT` (new verb for this
   adapter — every existing write is `POST`/`DELETE`); treat as non-idempotent like other writes.
   `GetEntityGraphAsync` is a parameterless `GET`. `ExpandGraphAsync` is a `POST` but read-only (no
   server-side side effects per its own description) — idempotent-for-retry like `SearchEntitiesAsync`.
4. Live tests (`tests/AgentMemory.Tests.Integration/Nams/NamsEntityGraphTests.cs`, new file,
   `LiveNamsFactAttribute`-gated):
   - `SetEntityFeedbackAsync_OnExistingEntity_UpdatesScoreAndConfirmedFlag`: use `ListEntitiesAsync` to
     find an existing entity (non-destructive reuse, matching the Phase 10e spike's own pattern), set
     feedback with a specific score/confirmed value, assert `{id, updated: true}` — genuine because it's
     against a real entity id from a real prior list call, not a hardcoded/guessed id.
   - `GetEntityGraphAsync_ReturnsNodesAndEdgesFromTheWorkspace`: list several (not just one) existing
     entities via `ListEntitiesAsync`, assert at least one appears among the graph's returned nodes (by
     id) — proves the two endpoints are talking about the same real workspace data, not just
     independently well-typed, while avoiding a single-entity dependency on `GET /entities/graph`'s
     undocumented ordering/cap behavior (a real risk a self-review correctness pass flagged: neither
     endpoint's confirmed shape documents any pagination contract linking them).
   - `ExpandGraphAsync_OnASeedEntity_ReturnsANonEmptyNeighborhood`: expand from a known entity id, assert
     the response's `Nodes` are non-empty and `Truncated.NodeId` matches the seed -- avoiding a "trivially
     always empty" assertion given entities in this dev workspace are heavily interlinked (confirmed
     Phase 10e: 21 nodes/27 edges from expanding a single entity). Note: the seed's own id is not expected
     to appear among its own expansion's neighbor nodes (confirmed empirically), so the assertion checks
     the truncation metadata's echoed node id instead.
5. Unit-level wire-shape tests in `Neo4jNamsClientAdapterTests.cs` for all three methods, using the exact
   confirmed JSON shapes above (matching the pattern Phase 10f's self-review established for closing
   deserialization coverage gaps without depending on live timing).
