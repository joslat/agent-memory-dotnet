# ADR 0015 - Memory History Read Model

Status: Accepted

Date: 2026-07-09

## Context

The project now treats invalidation and supersession as first-class memory lifecycle operations. Facts, preferences, and entities can be soft-invalidated with `invalidated_at`; facts and preferences can be linked through `SUPERSEDED_BY`; facts also carry valid-time windows. Those records are intentionally kept for audit, recovery, and as-of recall, but before this decision the only easy operational view was ad hoc graph querying.

The next feature need is a simple way for operators, tests, and future adapters to inspect what happened to long-term memory without exposing raw Neo4j query details or adding another mutation path.

## Decision

Expose memory history as a normalized, read-only service and CLI command:

- `IMemoryHistoryService` in `AgentMemory.Abstractions` returns `MemoryHistoryRecord` rows.
- `Neo4jMemoryHistoryService` projects existing Neo4j fields and relationships without changing the schema.
- `agentmemory history` exposes the same read model for operators.
- The query supports optional kind, id, owner, shared-memory, live-only, and limit filters.
- Records include lifecycle status, timestamps, valid-time fields, supersession links, source message ids, owner id, summary, and metadata.

This is a read model, not a compatibility layer and not a new source of truth. The source of truth remains the graph.

## Consequences

Positive consequences:

- Non-destructive memory behavior is inspectable without hand-written Cypher.
- Operators can review why a memory left live recall.
- Integration tests can assert lifecycle behavior through a public contract.
- Future UI/MCP/SK surfaces can reuse the same read model.

Tradeoffs:

- The first read model intentionally covers only long-term Entity, Fact, and Preference nodes.
- Relationship history, conversation archival history, and richer event timelines remain future work.
- The service must stay aligned with persisted property names because it reads raw node lifecycle fields not represented on all domain records.

## Alternatives Considered

### Add lifecycle fields to every domain record only

Rejected as insufficient. It would make repository reads heavier and still would not provide a cross-kind history view or supersession/provenance aggregation.

### Expose raw graph query only

Rejected. `IGraphQueryService` remains useful for advanced diagnostics, but the common lifecycle view deserves a stable contract and CLI.

### Add a new event-sourcing/event-log schema

Deferred. The current graph already contains enough lifecycle state for a useful first history surface. A full event log may be valuable later, but it should be justified by stronger audit or compliance requirements.

## Verification Anchors

- `src/AgentMemory.Abstractions/Domain/History/MemoryHistory.cs`
- `src/AgentMemory.Abstractions/Services/IMemoryHistoryService.cs`
- `src/AgentMemory.Neo4j/Services/Neo4jMemoryHistoryService.cs`
- `tools/AgentMemory.Cli/Commands/MemoryCommands.cs` (`HistoryCommand`)
- `tests/AgentMemory.Tests.Unit/Cli/CliCommandsTests.cs`
- `tests/AgentMemory.Tests.Integration/ShakedownEndToEndTests.cs`
