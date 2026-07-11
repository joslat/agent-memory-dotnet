# Compatibility Automation

Status: current as of 2026-07-11.

This document defines how Agent Memory for .NET checks compatibility with the upstream `neo4j-labs/agent-memory` ecosystem.

## Layers

| Layer | Purpose | Current automation |
|---|---|---|
| Static schema parity | Detect accidental drift in labels, relationship types, and shared property names. | `agentmemory schema-parity` plus `UpstreamSchemaParityTests`. |
| Behavioral compatibility | Prove behavior against upstream-style scenarios. | Track `neo4j-labs/agent-memory-tck`; bridge or mirror scenarios into .NET tests. |
| Quality/performance evaluation | Compare deterministic memory behavior, retrieval quality, and latency without grading chat answers first. | Planned scenario runner; see `performance-quality-evaluation.md`. |
| Upstream refresh cadence | Notice tagged releases or material `main` changes and decide whether to capture a new snapshot. | Scheduled upstream compatibility watch workflow with a GitHub Step Summary of embedded baseline, latest tags/release, upstream main SHAs, TCK main SHA, and schema-parity output. |

## Static Schema Parity

Run locally:

```bash
dotnet run --project tools/AgentMemory.Cli/AgentMemory.Cli.csproj -- schema-parity --upstream-version 0.5.0
```

CI runs the same command after the solution build. The command is pure static analysis: it does not require Neo4j.

A compatible report means:

- required upstream labels are present unless explicitly documented as upstream-only;
- required upstream relationship types are present;
- shared property names still use upstream spellings;
- .NET-only labels/relationships/properties are documented in the version policy;
- a divergence note is revisited if upstream catches up to a .NET superset.

## Behavioral TCK Strategy

The upstream `neo4j-labs/agent-memory-tck` project is the right behavioral target. It is not the same as the current schema diff: the TCK describes behavior and data-model conformance.

Preferred order:

1. Implement a thin bridge adapter if the TCK exposes a language-neutral or process-level contract that can call this .NET library.
2. If a direct bridge is not practical, mirror high-value TCK scenarios as .NET integration tests.
3. Keep owner/store isolation scenarios stricter than upstream where this project intentionally improves the safety model.

Do not weaken .NET behavior simply to pass a scenario that assumes upstream's looser isolation semantics. Mark that as an intentional divergence and, where useful, add a .NET-specific stronger assertion.

## Current Next Sequence

`main` already contains local mirrored TCK-style scenarios and the compatibility scenario catalog. On `codex/tck-bridge-scn-mapping`, items #1 and #2 below are done and verified end-to-end against the real upstream runner: `pytest -m bronze --bridge-url http://localhost:3001` against `neo4j-labs/agent-memory-tck` commit `4603b91f` over a live Neo4j 5.26 = **93 passed, 0 failed** (full Bronze tier), alongside a clean build, full unit suite (2684), catalog guard tests, and live-Neo4j mirror tests (see `behavioral-compatibility-pack-status.md`'s Verification Log for 2026-07-11). **Update (2026-07-11, `codex/tck-silver-tier`): item #3 below (the Silver bridge tier) is now also done and verified — `pytest -m silver --bridge-url http://localhost:3001` against the same upstream commit = 67 passed, 0 failed** (full Silver tier), with the existing 17 wire-contract unit tests and 13 compatibility integration tests still green and a clean solution build. Gold and Platinum tiers remain future follow-up.

| Priority | Task-Feature | Description | Notes |
|---:|---|---|---|
| 1 | Upstream TCK HTTP bridge (Bronze) | **Done & verified (93/93).** Added `tools/AgentMemory.TckBridge`, an ASP.NET Minimal API host serving **12 Bronze endpoints** — the 9 short-term (`setup`, `teardown`, `clear_all_data`, `add_message`, `get_conversation`, `search_messages`, `list_sessions`, `delete_message`, `clear_session`) plus 3 schema-tier long-term creates (`add_entity`, `add_preference`, `add_fact`), since the TCK `bronze` marker covers "schema and short-term memory" — so `neo4j-labs/agent-memory-tck` can execute against this .NET implementation out of process. | Turns local mirror confidence into canonical upstream-runner evidence for Bronze. The three response-shape decisions (`delete_message` → `{"deleted": bool}`, `get_conversation` unknown-session empty envelope, `TckSessionInfo` → `{session_id, message_count, created_at, updated_at}`) are now **confirmed** against the real upstream contract (`base_adapter.py` + `bridge-protocol.adoc` + the upstream C# reference server) by the passing conformance run. |
| 2 | `SCN-*` scenario mapping (Bronze) | **Done.** Annotated the `NET-TCK-B-001` catalog row (`CompatibilityScenarioCatalog.cs`) with upstream `SCN-B-001/002/043/044/055/079`, plus catalog guard tests enforcing ID pattern and cross-row uniqueness. | Makes compatibility evidence reviewable and prevents drift. All six IDs are now **confirmed** against upstream `tck/registry/scenario_ids.yaml` (commit `4603b91f`). Silver/Gold/Platinum `SCN-*` enumeration remains a follow-up, not done here. |
| 3 | Upstream TCK HTTP bridge (Silver) | **Done & verified (67/67), on `codex/tck-silver-tier`.** Extended `tools/AgentMemory.TckBridge` with **12 Silver endpoints**: long-term `search_entities`, `search_preferences`, `get_entity_by_name`, `get_related_entities`, `add_relationship` plus reasoning `start_trace`, `add_step`, `record_tool_call`, `complete_trace`, `get_trace_with_steps`, `list_traces`, `get_tool_stats`. New DTOs (`Dtos.cs`): `TckReasoningTrace`, `TckReasoningStep`, `TckToolCall`, `TckToolStats`, `TckRelationship`. | `add_relationship` is nominally a Gold-tier endpoint per `bridge-protocol.adoc`, but the Silver `get_related_entities` scenarios depend on it for fixture setup, so it is included here. Silver's own `SCN-*` catalog mapping is not yet added. Gold and Platinum bridge tiers remain not started. |
| 4 | PR to `main` | Open the PR(s) from `codex/tck-bridge-scn-mapping` (Bronze) and `codex/tck-silver-tier` (Silver) now that #1–#3 are done. | These PRs should stay narrowly reviewable as bridge/mapping work; opening them is the only remaining step in these slices. |

### Running the bridge locally

```bash
dotnet run --project tools\AgentMemory.TckBridge
```

This listens on `http://localhost:3001` by default (override with `ASPNETCORE_URLS` or `--urls`) and reads Neo4j connection settings from the same `Neo4j:*` / `NEO4J_*` conventions as the CLI (defaults: `bolt://localhost:7687`, `neo4j`/`password`, database `neo4j`). It registers a deterministic `StubEmbeddingGenerator` for `search_messages`/`add_message` embeddings unless a real provider is wired in by host configuration.

Upstream conformance run — a manual, non-CI step that requires an external `neo4j-labs/agent-memory-tck` checkout and Python TCK tooling (not part of this repo). It **was executed** for this slice against upstream commit `4603b91f` over a live Neo4j 5.26: **93 passed, 0 failed** (full Bronze tier).

```bash
pytest -m bronze --bridge-url http://localhost:3001
```

The Silver tier's upstream conformance run was likewise executed against the same upstream commit and live Neo4j: **67 passed, 0 failed** (full Silver tier).

```bash
pytest -m silver --bridge-url http://localhost:3001
```

Gold and Platinum tiers are not implemented by the bridge and have no conformance run yet.

The full DONE/TODO ledger for this slice lives in [`behavioral-compatibility-pack-status.md`](behavioral-compatibility-pack-status.md).

## Quality and Performance Evaluation

Compatibility checks answer whether the implementation behaves as expected. Evaluation checks how well and how fast it behaves under deterministic data.

The first evaluation layer should avoid chat-answer grading and model-context grading. Instead, compare Python/upstream and .NET with identical fixture data, embeddings, operation mixes, and normalized result records. Track scenario pass rate, latency percentiles, Recall@K, MRR/NDCG, owner leak count, temporal pass rate, and provenance completeness.

See `performance-quality-evaluation.md` for the canonical evaluation boundary and initial scenario set.

## Snapshot Refresh

Refresh embedded snapshots when one of these is true:

- upstream publishes a new relevant tag or package release;
- upstream `main` changes the Bolt/self-hosted graph schema materially;
- the TCK adds or changes conformance scenarios that affect this project.

Do not refresh solely for upstream docs-only or hosted-service-only changes. The scheduled workflow emits a warning when the latest observed `python-v*` tag differs from the embedded snapshot; that warning is a review trigger, not an automatic adoption mandate.

Refresh checklist:

1. Capture the upstream schema into a new `docs/reference/neo4j-agent-memory/python-vX.Y.Z/` folder.
2. Add the new `schema.json` as an embedded resource in `AgentMemory.Neo4j.csproj`.
3. Add a matching `SchemaParityPolicy` for the version.
4. Update `UpstreamSchemaParityTests` expectations if intentional divergences changed.
5. Run `agentmemory schema-parity --upstream-version X.Y.Z`.
6. Update ADR/documentation if a new divergence is accepted.
