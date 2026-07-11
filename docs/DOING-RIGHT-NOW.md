# Doing Right Now

**Last updated:** 2026-07-11 (Full Bronze TCK bridge — schema + short-term, 12 endpoints, 93/93 upstream-conformant against the real neo4j-labs/agent-memory-tck)

## Current Branch

`codex/tck-bridge-scn-mapping`

This branch tracks `origin/codex/tck-bridge-scn-mapping` and was created from current `main` after branch cleanup and documentation realignment.

Current base context:

- `main` was fast-forwarded to include the behavioral compatibility pack.
- `main` was then updated with `4d3311b` (`Clean branch status and salvage samples solution`).
- `codex/tck-bridge-scn-mapping` was created from that updated `main` and pushed to origin.

## What Was Just Done

1. Merged `codex/behavioral-compatibility-pack` into `main` and pushed `main`.
2. Deleted already-merged local branches:
   - `codex/behavioral-compatibility-pack`
   - `feat/expose-invalidate-supersede`
   - `feat/gds-analytics`
   - `fix/cycle-2`
   - `fix/review-cycle-1`
   - `remediation/analysis-review-hardening`
   - `rename/agentmemory-package-ids`
   - `test/harden-isolation`
3. Deleted already-merged remote branches:
   - `origin/codex/behavioral-compatibility-pack`
   - `origin/feat/expose-invalidate-supersede`
   - `origin/feat/gds-analytics`
   - `origin/fix/cycle-2`
   - `origin/fix/review-cycle-1`
   - `origin/loop/delete-session-gap`
   - `origin/remediation/analysis-review-hardening`
   - `origin/rename/agentmemory-package-ids`
4. Reviewed stale `loop/aspire-demo` and confirmed it should not be merged as-is:
   - it was 9 commits ahead of its old base but 190 commits behind current `main`;
   - current `main` already contains `samples/AspireDemo`;
   - direct merge would have risked rewinding/removing newer work.
5. Salvaged the only useful low-risk artifact from the stale Aspire branch:
   - added `samples/samples.sln`;
   - documented it in `samples/README.md`;
   - verified it with `dotnet build samples\samples.sln --no-restore` successfully, 0 warnings and 0 errors.
6. Deleted stale unmerged branches after salvage:
   - local `loop/aspire-demo`
   - local `loop/delete-session-gap`
   - remote `origin/loop/aspire-demo`
7. Updated status docs so they no longer describe the compatibility pack as a pre-PR branch:
   - `CONTINUE-HERE.md`
   - `docs/ROADMAP.md`
   - `docs/core/behavioral-compatibility-pack-status.md`
   - `docs/core/compatibility-automation.md`
   - `docs/core/implementation-plan-golden-path-compatibility.md`
   - `docs/nextsteps.md`

## Current Task Being Done

**Done — full Bronze tier, EXPANDED beyond the original plan, 2026-07-11.** The behavioral compatibility slice planned in `docs/core/tck-bridge-implementation-plan.md` is implemented and verified against the **real upstream Technology Compatibility Kit** on `codex/tck-bridge-scn-mapping`. The original plan scoped only "Bronze short-term memory"; this slice was expanded to the **full Bronze tier (schema + short-term)** because the TCK "bronze" marker is defined as "schema and short-term memory" and its schema tests assert the round-tripped shape of created entities/facts/preferences.

1. **Upstream TCK HTTP bridge** — `tools/AgentMemory.TckBridge`, an ASP.NET Minimal API host, now serves **12 endpoints (was 9)**:
   - 9 Bronze **short-term** endpoints: `setup`, `teardown`, `clear_all_data`, `add_message`, `get_conversation`, `search_messages`, `list_sessions`, `delete_message`, `clear_session`.
   - 3 Bronze **schema-tier long-term** create endpoints: `add_entity`, `add_preference`, `add_fact`. Long-term records are embedded via the deterministic `StubEmbeddingGenerator` and default `Confidence` to `1.0`.
   - Default `http://localhost:3001`; added to `AgentMemory.slnx`, `IsPackable=false`. `/setup` now returns `{"ok": true}` (matching the upstream C# reference conformance server) instead of `{"status":"ok"}`.
2. **`SCN-*` scenario mapping** — extended `CompatibilityScenario` in `CompatibilityScenarioCatalog.cs` with an `UpstreamScenarioIds` field; mapped the `NET-TCK-B-001` Bronze mirror to `SCN-B-001`, `SCN-B-002`, `SCN-B-043`, `SCN-B-044`, `SCN-B-055`, `SCN-B-079`; added catalog guard tests (`UpstreamScenarioIds_MatchScnPattern`, `UpstreamScenarioIds_AreUniqueAcrossCatalog`, `BronzeUpstreamMirror_HasMapping`).

**Real upstream conformance result:** ran `pytest -m bronze --bridge-url http://localhost:3001` against **neo4j-labs/agent-memory-tck at commit `4603b91f` (main)**, driving `tools/AgentMemory.TckBridge` over HTTP against a live **Neo4j 5.26 (Docker)**. Result: **93 passed, 0 failed** (96 deselected = the Silver/Gold/Platinum scenarios). This is the full Bronze tier.

**Five real defects found and fixed via the conformance run** — the upstream runner uses its OWN Pydantic models on both ends (unlike the in-process mirror tests, which used the bridge's own DTOs and so could not catch a contract mismatch):

1. **`TckSessionInfo` shape was wrong.** Was `{session_id, conversation_count, message_count, last_message_preview, last_activity}`; the TCK `TCKSessionInfo` model requires `{session_id, message_count, created_at, updated_at}` and reads `created_at` as a required key. Corrected the DTO + mapping.
2. **Invalid Cypher in the vector-index readiness poll** — `SHOW INDEXES WHERE ... RETURN count(*)` (missing a `YIELD`) is a Neo4j 5.x syntax error that the `catch` swallowed, so the poll silently burned its FULL timeout on every call (30s in the bridge `/setup`, 60s in the test fixture). Existed in BOTH the bridge and the repo's `Neo4jIntegrationFixture.WaitForVectorIndexesAsync` (a genuine latent product-test bug). Fixed both with `SHOW INDEXES YIELD type, state WHERE ...`. Integration compatibility run dropped from ~1m52s to ~22s.
3. **`delete_message` id-format mismatch** — `IIdGenerator` stores ids as unhyphenated 32-char hex ("N" format), but the Python runner round-trips ids through `UUID()` and re-emits canonical dashed form, so `delete_message` looked up the dashed id, matched nothing, and returned `False`. Fixed by normalizing the incoming id to "N" format in the `delete_message` handler.
4. **`add_fact` request field is `obj`, not `object`** — the request DTO property was named `Object` (→ `"object"` under snake_case), so it never bound and the fact object arrived null, failing the Neo4j `MERGE`. Renamed the request property to `Obj`.
5. **`get_conversation` on an unknown session returned the raw `session_id` as the envelope `id`**, which the runner parses via `UUID()`; TCK session ids are not UUIDs (fixture: `f"tck-{uuid4()}"`), so this threw. Fixed to fall back to the nil UUID (`Guid.Empty`).

**Judgment calls from the plan — now RESOLVED against the real upstream contract** (`tck/adapters/base_adapter.py` Pydantic models + `docs/reference/bridge-protocol.adoc` + `clients/csharp` reference server):
- `delete_message` response shape `{"deleted": bool}` — **CONFIRMED correct.**
- `TckSessionInfo` field names — **CORRECTED.** The earlier "superset is safe" guess was wrong: `created_at` is a required key. Field set is now `{session_id, message_count, created_at, updated_at}`.
- `get_conversation` empty-envelope shape on an unknown session — **CONFIRMED** (with the `id` → nil-UUID fix noted in defect #5).

**Verified state (2026-07-11):**
- Full solution build — 0 warnings / 0 errors.
- Full unit suite (`AgentMemory.Tests.Unit`) — **2684/2684 passing** (`TckBridgeWireContractTests` now 17 tests, including new entity/fact/preference DTO field-name locks and an `add_fact` "obj"-binding regression test).
- Compatibility integration tests — **13/13 passing** against Testcontainers Neo4j (including the new `TckBridgeHttpRoundTripTests` `WebApplicationFactory` end-to-end test and the fixture query fix).
- Upstream Bronze TCK — **93/93.**

**Still open / not done:** Silver/Gold/Platinum bridge tiers (the long-term search/reasoning/relationship endpoints) remain future follow-up slices.

## Tasks / Feature Plans Ahead

| Priority | Task / Feature | Plan | Notes |
|---:|---|---|---|
| 1 | Full Bronze TCK HTTP bridge | **Done — expanded to 12 endpoints.** `tools/AgentMemory.TckBridge` Minimal API host wired to public services/repositories; serves the 9 short-term + 3 schema-tier long-term (`add_entity`, `add_preference`, `add_fact`) endpoints. | Verified against the real upstream TCK, 93/93. |
| 2 | Bridge configuration | **Done.** CLI-style Neo4j config conventions (`Neo4j:*`, `NEO4J_*`, default `bolt://localhost:7687`, default bridge URL `http://localhost:3001`); `StubEmbeddingGenerator` registered for deterministic local search + long-term embedding; long-term `Confidence` defaults to `1.0`. | No real embedding provider wired in; host config can override. |
| 3 | `SCN-*` scenario mapping | **Done.** `CompatibilityScenarioCatalog` extended with `UpstreamScenarioIds`; `NET-TCK-B-001` mapped to 6 `SCN-B-*` IDs. | Silver/Gold `[]` placeholders remain a documented follow-up, not this slice. |
| 4 | Tests for mapping and bridge shape | **Done.** Catalog guard tests (uniqueness/pattern/non-empty Bronze mapping); `TckBridgeWireContractTests` now 17 tests (entity/fact/preference DTO field-name locks + `add_fact` "obj"-binding regression); `TckBridgeHttpRoundTripTests` `WebApplicationFactory` end-to-end test. | Contract shapes now locked by wire-contract + round-trip tests, not just build success. |
| 5 | Documentation update | **Done** (this update). | Update `behavioral-compatibility-pack-status.md` / `compatibility-automation.md` separately if they still describe the 9-endpoint short-term-only scope. |
| 6 | Local validation | **Done.** Full solution build 0-warn/0-error; full unit suite 2684/2684; compatibility integration 13/13 against Testcontainers Neo4j. | — |
| 7 | Upstream conformance run | **Done.** `pytest -m bronze --bridge-url http://localhost:3001` against neo4j-labs/agent-memory-tck `4603b91f` over live Neo4j 5.26 (Docker): **93 passed, 0 failed** (96 Silver/Gold/Platinum deselected). | Uncovered and drove the fix of 5 real contract defects. |
| 8 | Open the PR | Open the PR from `codex/tck-bridge-scn-mapping` into `main`. | This is now the only remaining step for this slice. Everything is verified but **uncommitted**. |

## What Not To Do

- Do not merge or resurrect `loop/aspire-demo`; it was superseded.
- Do not claim Silver/Gold bridge conformance until the corresponding endpoints are implemented.
- Do not weaken .NET owner/store isolation to satisfy upstream assumptions. Mark stricter .NET behavior as an intentional divergence when needed.
- Do not delete generated/ignored Aspire `bin`/`obj` artifacts unless a separate cleanup task explicitly asks for it.

## Resume Point

The **full Bronze TCK bridge** (schema + short-term, 12 endpoints) + `SCN-*` mapping slice is **complete and verified** on `codex/tck-bridge-scn-mapping`: full solution build 0-warn/0-error, unit suite 2684/2684, compatibility integration 13/13, upstream Bronze TCK **93/93**. The three earlier judgment calls are now RESOLVED against the real upstream contract (`delete_message` shape CONFIRMED; `TckSessionInfo` fields CORRECTED; `get_conversation` empty-envelope CONFIRMED). Everything is verified but **uncommitted**.

**Next:** open the PR from `codex/tck-bridge-scn-mapping` into `main`.

Silver/Gold/Platinum bridge tiers (the long-term search/reasoning/relationship endpoints, additional `SCN-*` enumeration) are the next future follow-up slice and have **not** been started.
