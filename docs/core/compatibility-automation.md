# Compatibility Automation

Status: current as of 2026-07-09.

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
