# Core Documentation

Status: current as of 2026-07-09.

This folder is the canonical concept and contract layer for Agent Memory for .NET. It explains what the project is trying to be, what constraints it accepts, how the system is designed, what behavior it specifies, and which architectural decisions are accepted.

The older root-level documents remain useful reference material, but they serve different jobs:

| Location | Role |
|---|---|
| `docs/core/` | Canonical core docs: philosophy, requirements, design, specification, implementation plan, behavioral compatibility status, compatibility automation, performance/quality evaluation, ADRs, and summaries. |
| `docs/architecture.md` | Detailed package, layer, dependency, and graph-model reference. |
| `docs/design.md` | Domain-model and service/interface catalog. |
| `docs/schema.md` | Current Neo4j schema reference. |
| `docs/ROADMAP.md` | Current project status, release state, and forward work. |
| `docs/archive/` and `docs/reference/` | Historical plans and external parity research; not current truth unless explicitly cited by current docs. |

## Reading Order

1. [`philosophy.md`](philosophy.md) - why the project exists and what kind of system it is.
2. [`requirements-and-constraints.md`](requirements-and-constraints.md) - functional requirements, non-functional requirements, constraints, and pending work.
3. [`design-document.md`](design-document.md) - system architecture, flows, package roles, and operational model.
4. [`specification.md`](specification.md) - normative project specification.
5. [`implementation-plan-golden-path-compatibility.md`](implementation-plan-golden-path-compatibility.md) - current execution plan for the golden path and compatibility automation.
6. [`behavioral-compatibility-pack-status.md`](behavioral-compatibility-pack-status.md) - live status for the behavioral compatibility pack.
7. [`compatibility-automation.md`](compatibility-automation.md) - automated schema parity, TCK strategy, and upstream refresh cadence.
8. [`performance-quality-evaluation.md`](performance-quality-evaluation.md) - how to evaluate memory quality and performance without grading chat answers first.
9. [`adr/`](adr/) - accepted architecture decision records.
10. [`summaries.md`](summaries.md) - high-level to low-level summaries of this folder.

## Alignment Rules

- Code is the implementation truth.
- `docs/core/specification.md` is the normative documentation truth.
- `docs/core/implementation-plan-golden-path-compatibility.md` records the active sample/compatibility execution plan.
- `docs/schema.md` must mirror the schema declared in `src/AgentMemory.Neo4j/Queries/SchemaQueries.cs` and `src/AgentMemory.Abstractions/Schema/SchemaConstants.cs`.
- `docs/architecture.md` and `docs/design.md` should explain the implementation shape without inventing future packages or interfaces.
- Historical docs must stay labeled as historical when their claims have been superseded.
- Test counts and release status must be dated. Durable docs should avoid pretending a count is timeless.

## Current Reality Snapshot

Agent Memory for .NET is a .NET 9, Neo4j-backed, graph-native persistent memory library for AI agents. It ships 11 adapter/library packages plus the `AgentMemory` meta-package. The meta-package wires Core + Neo4j + LLM extraction references, while observability, enrichment, Azure Language extraction, analytics, Agent Framework, Semantic Kernel, MCP, and GraphRAG retrieval are opt-in registrations or packages.

The shipped model has three memory layers:

- Short-term memory: conversations and messages.
- Long-term memory: entities, facts, preferences, and entity relationships.
- Reasoning memory: traces, steps, tool calls, and tool aggregates.

Isolation is layered as store -> owner -> session. Store isolation can be logical in one database or physical via a database per application. Owner isolation uses `owner_id`, `owner_key`, and `MemoryScope`. Session identity remains the local conversation/runtime grouping.

Verification for this 2026-07-09 work: 2658 Release unit tests passed, plus a 5-test live Neo4j shakedown for the golden-path/history changes. The earlier docs cleanup also recorded 34 Semantic Kernel tests. The latest full live Neo4j integration count remains the ROADMAP record from 2026-06-21: 236 integration tests.
