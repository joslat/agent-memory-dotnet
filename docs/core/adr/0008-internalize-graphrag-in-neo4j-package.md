# ADR 0008 - Internalize GraphRAG in the Neo4j Package

Status: Accepted

Date: 2026-07-09

## Context

Earlier plans referenced a separate GraphRAG adapter package and external GraphRAG integration. The current implementation provides `IGraphRagContextSource` from `AgentMemory.Neo4j` through `AddGraphRagAdapter(...)`.

GraphRAG retrieval is tightly coupled to Neo4j schema, indexes, owner filtering, fulltext escaping, vector dimensions, traversal limits, and ranking behavior. Keeping it near the Neo4j queries reduces drift.

## Decision

Implement and register GraphRAG retrieval inside `AgentMemory.Neo4j`.

GraphRAG remains optional:

- Core context assembly resolves `IGraphRagContextSource` through `GetService`, so it is absent-safe.
- Consumers call `AddGraphRagAdapter(...)` after Neo4j registration when they want it.
- GraphRAG retrieval must respect owner scope and configured search mode.

## Consequences

Positive consequences:

- GraphRAG queries live with the Neo4j schema and repository infrastructure.
- Owner-scope and traversal behavior can share the same implementation discipline as other Neo4j queries.
- Consumers do not need a historical extra package.
- Core remains optional-GraphRAG and can run without it.

Tradeoffs:

- `AgentMemory.Neo4j` owns more retrieval behavior.
- Documentation must avoid referring to a stale standalone GraphRAG adapter package.
- Consumers must explicitly register GraphRAG even if they reference the meta-package.

## Alternatives Considered

### Keep a separate GraphRAG adapter package

Rejected for current code. It created an extra dependency boundary for behavior that is fundamentally Neo4j-query-specific.

### Always register GraphRAG from Core

Rejected. Core should not depend on Neo4j-specific retrieval and memory-only consumers should not be forced to configure GraphRAG.

### Remove GraphRAG support

Rejected. Blended retrieval is a core project capability and an important compatibility story.

## Verification Anchors

- `src/AgentMemory.Neo4j/Infrastructure/ServiceCollectionExtensions.cs` defines `AddGraphRagAdapter(...)`.
- `src/AgentMemory.Core/ServiceCollectionExtensions.cs` resolves optional `IGraphRagContextSource` with `GetService`.
- `docs/getting-started.md` documents explicit GraphRAG registration.
- `docs/architecture.md` and `docs/specification.md` no longer refer to a separate GraphRAG adapter package.
