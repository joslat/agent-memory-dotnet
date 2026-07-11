# Doing Right Now

**Last updated:** 2026-07-11

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

Paused task: implement the next behavioral compatibility slice on `codex/tck-bridge-scn-mapping`.

The intended slice is:

1. Add an upstream TCK HTTP bridge server for AgentMemory for .NET.
2. Map local compatibility scenarios to upstream stable `SCN-*` scenario IDs.
3. Keep the first implementation narrow and reviewable, starting with the Bronze short-term memory bridge.

Work started before this pause:

- Read local compatibility docs and tests.
- Read local short-term memory service/repository shapes.
- Pulled the upstream `neo4j-labs/agent-memory-tck` bridge protocol reference.
- Confirmed the upstream bridge contract:
  - server accepts `POST /{snake_case_method}`;
  - request body is a flat JSON object;
  - response uses UUID strings, ISO 8601 timestamps, lowercase enum strings, and JSON objects/lists/nulls;
  - Bronze endpoints include `setup`, `teardown`, `clear_all_data`, `add_message`, `get_conversation`, `search_messages`, `list_sessions`, `delete_message`, and `clear_session`.

No TCK bridge implementation code has been committed yet. This document is the pause/resume marker.

## Tasks / Feature Plans Ahead

| Priority | Task / Feature | Plan | Notes |
|---:|---|---|---|
| 1 | Bronze TCK HTTP bridge | Add a small ASP.NET Minimal API host under `tools/AgentMemory.TckBridge` and wire the Bronze short-term endpoints to public services/repositories. | This is the fastest useful compatibility bridge and can be verified locally without claiming full Silver/Gold support. |
| 2 | Bridge configuration | Reuse CLI-style Neo4j config conventions: `Neo4j:*`, `NEO4J_*`, default `bolt://localhost:7687`, and default bridge URL `http://localhost:3001`. | Register `StubEmbeddingGenerator` for deterministic local search behavior unless a real embedding provider is supplied by host configuration. |
| 3 | `SCN-*` scenario mapping | Extend `CompatibilityScenarioCatalog` with upstream scenario ID/tier traceability. | The local IDs such as `NET-TCK-B-001` should remain, but each upstream-mirrored row should name the upstream `SCN-*` IDs it covers. |
| 4 | Tests for mapping and bridge shape | Add unit/source guards for unique local IDs, non-empty upstream mappings where applicable, and documented endpoint names. | Avoid live Neo4j unless endpoint behavior itself is being integration-tested. |
| 5 | Documentation update | Update `behavioral-compatibility-pack-status.md` and `compatibility-automation.md` with the bridge command and current support tier. | Be explicit that the first bridge slice is Bronze; Silver/Gold remain future bridge expansion unless implemented. |
| 6 | Local validation | Run focused tests and build the new bridge project. | Minimum: compile the bridge project, run catalog tests, and run existing TCK mirror/catalog tests if time allows. |
| 7 | Optional upstream run | If Python TCK tooling is available, run `pytest -m bronze --bridge-url http://localhost:3001`. | This may require external tooling/network, so it is optional unless the environment is ready. |

## What Not To Do

- Do not merge or resurrect `loop/aspire-demo`; it was superseded.
- Do not claim Silver/Gold bridge conformance until the corresponding endpoints are implemented.
- Do not weaken .NET owner/store isolation to satisfy upstream assumptions. Mark stricter .NET behavior as an intentional divergence when needed.
- Do not delete generated/ignored Aspire `bin`/`obj` artifacts unless a separate cleanup task explicitly asks for it.

## Resume Point

Resume on `codex/tck-bridge-scn-mapping`.

Start by adding `tools/AgentMemory.TckBridge` with Bronze endpoints, then update `CompatibilityScenarioCatalog` with upstream `SCN-*` mappings and focused tests.
