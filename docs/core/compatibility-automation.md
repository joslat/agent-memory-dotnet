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

`main` already contains local mirrored TCK-style scenarios and the compatibility scenario catalog. On `codex/tck-bridge-scn-mapping`, items #1 and #2 below are done and verified (build clean, full unit suite green, catalog guard tests green, live-Neo4j mirror tests green — see `behavioral-compatibility-pack-status.md`'s Verification Log for 2026-07-11):

| Priority | Task-Feature | Description | Notes |
|---:|---|---|---|
| 1 | Upstream TCK HTTP bridge | **Done.** Added `tools/AgentMemory.TckBridge`, an ASP.NET Minimal API host serving the 9 Bronze endpoints (`setup`, `teardown`, `clear_all_data`, `add_message`, `get_conversation`, `search_messages`, `list_sessions`, `delete_message`, `clear_session`) so `neo4j-labs/agent-memory-tck` can execute against this .NET implementation out of process. | Turns local mirror confidence into canonical upstream-runner-reachable evidence for Bronze. Three response-shape judgment calls (`delete_message`, `get_conversation` unknown-session envelope, `TckSessionInfo` fields) were made without a reference bridge server and still need upstream confirmation. |
| 2 | `SCN-*` scenario mapping | **Done.** Annotated the `NET-TCK-B-001` catalog row (`CompatibilityScenarioCatalog.cs`) with upstream `SCN-B-001/002/043/044/055/079`, plus catalog guard tests enforcing ID pattern and cross-row uniqueness. | Makes compatibility evidence reviewable and prevents drift. `SCN-B-001`/`002` are confirmed against two independent upstream fetches; `SCN-B-043/044/055/079` are single-source and still need confirmation against `tck/registry/scenario_ids.yaml`. Silver/Gold/Platinum `SCN-*` enumeration remains a follow-up, not done here. |
| 3 | PR to `main` | Open the PR from `codex/tck-bridge-scn-mapping` now that #1 and #2 are done. | The PR should be narrowly reviewable as bridge/mapping work; this is the only remaining step in this slice. |

### Running the bridge locally

```bash
dotnet run --project tools\AgentMemory.TckBridge
```

This listens on `http://localhost:3001` by default (override with `ASPNETCORE_URLS` or `--urls`) and reads Neo4j connection settings from the same `Neo4j:*` / `NEO4J_*` conventions as the CLI (defaults: `bolt://localhost:7687`, `neo4j`/`password`, database `neo4j`). It registers a deterministic `StubEmbeddingGenerator` for `search_messages`/`add_message` embeddings unless a real provider is wired in by host configuration.

Optional upstream conformance run (requires an external `neo4j-labs/agent-memory-tck` checkout and Python TCK tooling — not part of this repo, and not executed as part of this slice's verification):

```bash
pytest -m bronze --bridge-url http://localhost:3001
```

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
