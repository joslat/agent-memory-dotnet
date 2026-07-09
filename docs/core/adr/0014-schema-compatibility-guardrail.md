# ADR 0014 - Schema Compatibility Guardrail

Status: Accepted

Date: 2026-07-09

## Context

Agent Memory for .NET is inspired by `neo4j-labs/agent-memory` and preserves compatible graph concepts where they help users understand, migrate, or compare implementations. The upstream project is active, but recent activity has focused heavily on NAMS, docs, SDK alignment, and hosted-backend concerns. The .NET project is a self-hosted Neo4j/.NET implementation with stronger owner/store isolation, temporal invalidation, and .NET-specific framework integrations.

The repository already contains a static parity kit: embedded upstream schema snapshots, `SchemaParityVerifier`, `SchemaParityPolicy`, unit tests, and the `agentmemory schema-parity` CLI command. The open question is whether this should grow into a broad runtime compatibility layer or remain a verification guardrail.

## Decision

Maintain upstream compatibility as a tested guardrail, not as a runtime compatibility layer.

The project will:

- preserve upstream-compatible labels, relationship types, and snake_case property names where practical;
- embed dated upstream schema snapshots and compare them through `agentmemory schema-parity`;
- keep a documented divergence policy for each embedded upstream version;
- fail CI when shared labels, relationship types, or properties drift accidentally;
- track `neo4j-labs/agent-memory-tck` as the behavioral compatibility reference;
- periodically check upstream tags/main and refresh snapshots only for tagged releases or material schema changes;
- document intentional .NET supersets rather than hiding them.

The project will not:

- contort the .NET schema to match upstream when doing so would weaken owner isolation, store isolation, temporal invalidation, or .NET hosting ergonomics;
- claim exact Python runtime compatibility;
- add a runtime adapter layer unless a concrete import/export or migration use case requires it.

## Consequences

Positive consequences:

- Compatibility regressions are visible and automatable.
- The schema can remain understandable to users coming from the Python project.
- Stronger .NET safety features such as `owner_id`, `owner_key`, and `invalidated_at` remain first-class.
- The project can follow useful upstream ideas without inheriting hosted-backend or Python-specific constraints.

Tradeoffs:

- Exact graph interchange is not guaranteed for every upstream release.
- New upstream schema versions require an explicit snapshot and policy update.
- Behavioral TCK conformance needs either a bridge adapter or mirrored .NET scenarios.
- Intentional divergences must be documented each time they are introduced.

## Alternatives Considered

### Full runtime compatibility layer

Rejected. A broad compatibility layer would add maintenance cost and could pressure the .NET implementation to preserve weaker upstream behavior, especially around multi-user isolation and temporal semantics.

### Exact schema lockstep with upstream

Rejected. Upstream and this project have different product directions. Exact lockstep would make self-hosted .NET safety improvements harder to ship.

### No upstream compatibility checks

Rejected. The upstream project remains the conceptual reference point, and accidental drift in shared graph concepts would make migration, comparison, and user understanding worse.

## Verification Anchors

- `tools/AgentMemory.Cli` exposes `schema-parity`.
- `src/AgentMemory.Neo4j/Schema/Parity/` contains the static parity verifier, registry, report, descriptor, and divergence policy.
- `docs/reference/neo4j-agent-memory/python-v0.5.0/schema.json` is embedded into `AgentMemory.Neo4j` as the current upstream snapshot.
- `tests/AgentMemory.Tests.Unit/Infrastructure/UpstreamSchemaParityTests.cs` verifies compatibility and drift detection.
- `.github/workflows/squad-ci.yml` runs static schema parity in CI.
- `.github/workflows/upstream-compatibility-watch.yml` records upstream state on a schedule for snapshot-review cadence.
