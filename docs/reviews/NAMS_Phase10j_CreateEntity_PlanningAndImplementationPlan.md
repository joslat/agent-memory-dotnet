# NAMS Phase 10j: `CreateEntityAsync` — Planning & Implementation Plan

## Scope and why now

Adds `POST /v1/entities` (manual entity creation) to `INamsClient`. This was explicitly scoped **out** of
Phase 10g ("`POST /entities` (manual entity creation) is out of scope, not part of any TCK Platinum scenario
here") because none of `test_platinum.py`'s scenarios directly test entity creation. That's still true — but
building a NAMS-backed TCK bridge (the next phase) requires it transitively: the upstream
`test_set_entity_feedback_returns_updated` test calls `adapter.add_entity(...)` *before*
`adapter.set_entity_feedback(...)`, so a bridge without a working `add_entity` route fails that test even
though entity creation itself isn't what it's testing.

## Design, informed by a live probe

`POST /v1/entities` was live-probed for the first time this session (never confirmed before):

Request: `{name, type, description?, properties?}` (`name`/`type` required per the pinned OpenAPI snapshot's
`handlers.createEntityRequest`).

Live response (confirmed): `{"description":"...","id":"...","name":"...","ontologyVersionId":"...",
"resolution":"created","systemAdded":true,"type":"...","validationMode":"permissive"}` — richer than the
pinned snapshot's `EntityResponse` definition (`{description, id, name, type}` only). Reuses the existing
`NamsEntity` domain record (already used by `SearchEntitiesAsync`/`ListEntitiesAsync`/`GetEntityGraphAsync`'s
nodes) rather than a new type — the extra live-only fields (`ontologyVersionId`/`resolution`/`systemAdded`/
`validationMode`) aren't needed by any current caller and are silently ignored on deserialization, matching
the established "one shape covers multiple endpoints" pattern (`NamsMessage`, `NamsReasoningStep`).

A genuine write (creates a new entity) — `NamsRetryEligibility.NonIdempotent`, same tier as
`CreateConversationAsync`.

## Implementation checklist

1. `INamsClient`: add `CreateEntityAsync(string name, string type, string? description, CancellationToken)`.
2. `Neo4jNamsClientAdapter`: implement it; new wire-only `CreateEntityRequestBody` record.
3. Live test (`NamsEntityGraphTests.cs`, new test method): create an entity with a distinctive name, assert
   the response echoes it back with a real id — genuine because it's checking data this test itself just
   created, not an ambient one.
4. Unit wire-shape test in `Neo4jNamsClientAdapterTests.cs` using the exact confirmed live JSON shape above.
5. Patch the 5 fake `INamsClient`/`ThrowingNamsClientStub` test doubles with the new member (mechanical,
   `ThrowingNamsClientStub` needs the one new virtual method; the 5 per-test fakes need nothing since none
   currently override it).

Deliberately not wired into any higher-level service or MCP tool — same low-level-capability-only tier as
every other Phase 10 addition.

## Unplanned discovery: a real flakiness bug in Phase 10g's `ExpandGraphAsync` test

Running the live suite after adding `CreateEntityAsync`'s own test surfaced a genuine, pre-existing bug:
`ExpandGraphAsync_OnASeedEntity_ReturnsANonEmptyNeighborhood` (Phase 10g) used the shared
`GetAnyExistingEntityIdAsync(namsClient, limit: 1, ...)` helper to pick "any" entity and assumed it would be
well-connected. Confirmed live: `GET /entities?limit=` returns **newest-first**, and a just-created entity
(e.g. from this very phase's own `CreateEntityAsync` test) genuinely has zero edges yet — relationship
extraction lags entity creation, the same class of async-worker delay seen before (observations, provenance).
The test failed for real when run in the same session as the new entity-creation test.

**Fixed properly, not papered over:** the test now pulls the full workspace graph via
`GetEntityGraphAsync()` and picks a seed entity *provably* referenced by at least one edge, rather than
gambling on freshness. This is a strictly better test design (guaranteed connected, not probabilistically
likely) that happens to also fix the flakiness.

## Second unplanned discovery: `POST /v1/entities` has THREE response shapes, not one

The `CreateEntityAsync` live test itself then failed for real: its entity name shared the
`"TckBridgeProbeEntity"` prefix reused across many earlier probes in this same session's shared dev
workspace, and NAMS's fuzzy entity-resolution auto-merged it into an existing entity instead of creating a
new one. The response for that outcome (`"resolution":"merged"`) is a **completely different, minimal
shape** -- `{id, resolution, merged_into, confidence}`, no `name`/`type`/`description` at all -- which the
original design (deserializing into the reused `NamsEntity` type) silently accepted as null fields instead
of failing loudly.

Confirmed live, all three outcomes exist:
- `"created"` -- genuinely new: full entity fields, no `duplicate_of`/`merged_into`.
- `"review_pending"` -- probable duplicate: full entity fields **plus** `duplicate_of`.
- `"merged"` -- auto-merged: **only** `id`/`resolution`/`merged_into`/`confidence`, nothing else.

Also confirmed: NAMS's own wire casing is inconsistent between these fields -- the "success" fields
(`ontologyVersionId`/`systemAdded`/`validationMode`) are camelCase, while the "duplicate-detection" fields
(`duplicate_of`/`merged_into`) are snake_case. A real inconsistency in NAMS's own API, not a client bug.

**Fixed properly:** introduced `NamsCreateEntityResult` (replacing the reused `NamsEntity` as
`CreateEntityAsync`'s return type) modeling all three shapes honestly with nullable fields, documented in its
own doc comment. The live test no longer assumes `"created"` will happen -- resolution is inherently
probabilistic (name/semantic-similarity-based), so it asserts correctly on whichever real outcome occurs.
Three unit tests now cover all three confirmed shapes.
