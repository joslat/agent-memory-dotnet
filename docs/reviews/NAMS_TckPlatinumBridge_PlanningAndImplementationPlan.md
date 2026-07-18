# NAMS TCK Platinum Bridge — Planning & Implementation Plan

## Purpose

A new sibling project, `tools/AgentMemory.TckBridge.Nams`, implementing the upstream
`neo4j-labs/agent-memory-tck` bridge protocol's Platinum-tier routes against the real NAMS SaaS via
`INamsClient`. Its purpose is to let the **official** upstream `pytest -m platinum` conformance suite run
against our NAMS integration, rather than relying only on our own hand-written live tests (Phases 10e-10j).
Separate project from the existing `AgentMemory.TckBridge` (direct/self-hosted backend) because the DI/backend
wiring is completely different (`AddNamsAgentMemory` + `INamsClient`, not `AddNeo4jAgentMemory` + Core/Neo4j
services) — mixing both into one binary would be a worse design than two small, focused ones.

## Research: what the official suite actually requires

Read the upstream TCK's own client code directly (`tck/adapters/http_bridge.py`, `tck/adapters/base_adapter.py`,
`tck/tests/v1/test_platinum.py` — checked out at `/c/tmp/agent-memory-tck`, commit `4603b91f`, same one
used for the existing bridge's Bronze/Silver/Gold conformance runs) rather than guessing at the protocol.

### The 11 scenarios in `test_platinum.py` map to 10 already-built `INamsClient` methods + 1 new one

| Bridge route | `INamsClient` method | Notes |
|---|---|---|
| `create_conversation` | `CreateConversationAsync` | |
| `list_conversations` | `ListConversationsAsync` | |
| `delete_conversation` | `DeleteConversationAsync` | |
| `bulk_add_messages` | `AddMessagesAsync` | |
| `get_context` | `GetContextAsync` | |
| `get_observations` | `GetObservationsAsync` | |
| `set_entity_feedback` | `SetEntityFeedbackAsync` | |
| `get_entity_graph` | `GetEntityGraphAsync` | edges need `sourceId`/`targetId` → `source`/`target` |
| `record_step` | `RecordReasoningStepAsync` | |
| `get_trace_by_conversation` | `GetReasoningTraceAsync` | |
| `cypher_query` | `ExecuteCypherQueryAsync` | |
| `add_entity` (transitive — used by `test_set_entity_feedback_returns_updated` before scoring) | `CreateEntityAsync` (Phase 10j, built specifically for this) | |

### Required regardless of tier: `setup`/`teardown`/`clear_all_data`

`tck/tests/conftest.py`'s session-scoped `adapter` fixture calls `setup()`/`teardown()` unconditionally, and
an `autouse=True` fixture calls `clear_all_data()` before **every** test. All three are required even for a
Platinum-only run, or the whole session fails immediately (confirmed: `_call`'s `RuntimeError` on any missing
route, not a graceful skip). NAMS has no schema/bootstrap step and — checked the pinned OpenAPI snapshot for
any wipe/reset/purge endpoint — **no bulk-wipe capability of any kind**. `setup`/`teardown` are no-ops;
`clear_all_data` does best-effort cleanup (delete every conversation found via `ListConversationsAsync`).
Entities/reasoning traces are NOT wiped (no such NAMS capability exists) — acceptable because every Platinum
assertion is permissive (`hasattr`/`isinstance`/asserts-only-on-data-the-test-itself-just-created), never an
exact-total-count check that accumulated entities would break.

### Wire-shape translation required (cross-referenced against the TCK client's own strict `_xxx_from_dict` parsers)

The TCK client's parsers use plain dict indexing (`d["field"]`, raises `KeyError` if absent) for some fields
and `.get()` with defaults for others — read every parser used by the 11 routes to know exactly which fields
must be present and under what name:

- **`_conversation_from_dict`** (used by `create_conversation`/`list_conversations`) requires `id`,
  `session_id`, `created_at`. NAMS's conversation model has no `session_id` concept at all (a conversation
  *is* the session) and its create-response has no `created_at` field at all (confirmed live, Phase 2/10e).
  Bridge fix: echo the conversation's own `id` as `session_id`; synthesize `created_at` as `DateTimeOffset.UtcNow`
  for `create_conversation` (list_conversations already has a real `createdAt`).
- **`_message_from_dict`** (used inside `bulk_add_messages`'s response and `get_context`'s `recent_messages`)
  requires `id`, `role`, `content`, `timestamp` — NAMS's message shape uses `createdAt`, not `timestamp`.
  Bridge fix: a bridge-local message DTO with a `Timestamp` property (naming-policy-converts to `timestamp`)
  populated from `NamsMessage.CreatedAt`.
- **`_entity_from_dict`** (used by `add_entity`) requires `id`, `name`, `type`, `created_at` — NAMS's
  `POST /entities` response never includes `created_at` in ANY resolution outcome (confirmed live, Phase 10j),
  and its `"merged"` resolution outcome omits `name`/`type` entirely. Bridge fix: synthesize `created_at`
  always; fall back to the original request's `name`/`type` when NAMS's response is the minimal "merged" shape
  (a deliberate, disclosed compromise — the bridge's job is satisfying the test harness's stricter contract,
  not the client, which stays honest about NAMS's real, sometimes-incomplete response per Phase 10j).
- **`get_entity_graph`**'s parser requires edge `id`/`source`/`target` (not `sourceId`/`targetId`) — rename.
- Everything else (`set_entity_feedback`, `record_step`/`get_trace_by_conversation`'s steps/tool_calls,
  `cypher_query`, `delete_conversation`) maps cleanly via the existing snake_case naming policy with no
  renaming, confirmed by reading each parser: `set_entity_feedback` only needs `id`/`updated` (matches
  `NamsEntityFeedbackResult` exactly); reasoning steps/traces' snake_case-converted `conversation_id`/
  `tool_calls` match `NamsReasoningStep`/`NamsReasoningTrace` directly; `cypher_query`'s result isn't parsed
  into a typed model at all (raw `columns`/`rows`/`stats` passthrough).
- `get_observations`/`get_context`'s `reflections`/`observations` tiers are exercised only against a fresh
  conversation by the actual test scenarios (empty lists) — their per-item shape doesn't matter for passing
  the official suite, but is modeled correctly anyway for robustness.

## Implementation checklist

1. New project `tools/AgentMemory.TckBridge.Nams` (Minimal API, `Microsoft.NET.Sdk.Web`, `IsPackable=false`,
   `ProjectReference` to `AgentMemory.Nams` only — no meta-package, no Core/Neo4j).
2. `AgentMemory.Nams.csproj`: add an `InternalsVisibleTo` grant for `AgentMemory.TckBridge.Nams` (the shared
   `src/Directory.Build.props` grant already covers the OTHER, direct-backend `AgentMemory.TckBridge` project
   by that exact name — a different name needs its own explicit grant, matching the project's own existing
   pattern for `AgentMemory.Tests.Unit`/`AgentMemory.Tests.Integration`).
3. Add the new project to `AgentMemory.slnx`.
4. `Program.cs`: config (`NAMS_API_KEY`/`NAMS_WORKSPACE_ID` env vars, default listen port distinct from the
   direct-backend bridge's 3001 — use 3002), snake_case JSON naming policy (matching the existing bridge),
   DI via `AddNamsAgentMemory`, all 14 routes (11 Platinum + `add_entity` + `setup`/`teardown`/`clear_all_data`).
5. `Dtos.cs`: bridge-local response records with the renames identified above.
6. Verify by actually running the upstream suite: `pytest -m platinum --bridge-url http://localhost:3002`
   against the real NAMS SaaS dev workspace — this is the actual point of the whole exercise, not optional.
7. Self-review (2 parallel agents) before merge, same discipline as every other Phase 10 PR.

Deliberately NOT reusing this new bridge to also implement Bronze/Silver/Gold for NAMS — those tiers assume
short-term/long-term/reasoning memory *service* semantics the direct backend has and NAMS's REST surface
doesn't expose the same way (e.g. no owner-scoped repository-level API); Platinum is what NAMS's REST surface
was actually designed to test.

## Result: verified against the real upstream suite, 10/11 passing

Ran `pytest -m platinum --bridge-url http://localhost:3002` against the real NAMS SaaS dev workspace (not a
mock) after fixing every real bug the first run surfaced:

1. **Missing `/add_message` route** — `test_get_context_shape` calls the single-message `add_message`
   (Bronze-shared contract), not `bulk_add_messages`. Added, mapping `session_id` (a NAMS conversation IS the
   "session") to `AddMessagesAsync` with a single-item list.
2. **Reusing a Nams domain record directly as a bridge response type is unsafe.** System.Text.Json's
   `PropertyNamingPolicy` only applies to properties WITHOUT an explicit `[JsonPropertyName]` attribute — an
   explicit attribute always wins. Every Nams domain record has explicit camelCase attributes matching NAMS's
   own wire casing, so reusing one directly (as the original design did for `record_step`,
   `get_trace_by_conversation`'s tool calls, and `set_entity_feedback`) silently emitted camelCase instead of
   snake_case, breaking the TCK client's strict dict-key parsers (`KeyError: 'conversation_id'`, etc.).
   Fixed by introducing bridge-local DTOs with no `JsonPropertyName` attributes of their own for every route
   that had reused a domain type directly — documented as a standing rule in `Dtos.cs`'s own comment.
3. **`get_entity_graph`'s edge `id` isn't a UUID.** NAMS's real edge id is a compound
   `"sourceId|TYPE|targetId"` string (confirmed live, Phase 10g) — the TCK client's edge parser requires a
   parseable UUID. Synthesizes a fresh one per response (display-only, never looked up again).
4. **`cypher_query`'s rows are the wrong shape.** NAMS returns each row as a column-name-keyed dict
   (`{"total": 296}`); the TCK's `TCKCypherResult.rows` Pydantic model requires a **positional list** ordered
   by `columns` (`[296]`). Fixed by transforming each row through the confirmed `columns` order, with a
   `JsonElement`-to-CLR-object unwrap helper for clean re-serialization.

**One test remains unfixable, and confirmed to be a genuine bug in the upstream TCK suite itself, not our
bridge or NAMS integration:** `test_create_conversation_returns_uuid` asserts
`conv.user_id == "alice" or conv.user_id is None`, but `TCKConversation`'s own Pydantic model
(`tck/adapters/base_adapter.py`) declares no `user_id` field at all — only `id`/`session_id`/`messages`/
`title`/`created_at`/`updated_at`. `_conversation_from_dict` never constructs the model with a `user_id`
kwarg either. No bridge response could make this attribute exist; this would fail identically against any
correctly-implemented Platinum adapter. **Final result: 10/11 official Platinum scenarios passing, with the
1 failure independently attributable to the upstream test suite.**
