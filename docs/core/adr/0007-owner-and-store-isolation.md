# ADR 0007 - Owner and Store Isolation

Status: Accepted

Date: 2026-07-09

## Context

Agent memory frequently contains private user preferences, facts, and reasoning traces. A memory library that serves multiple users or applications must prevent cross-user and cross-application recall. Early planning and review work identified isolation as a release-critical concern.

Neo4j vector indexes also cannot always pre-filter by owner before vector candidate retrieval, so isolation must be designed into both schema and query behavior.

## Decision

Make store and owner isolation first-class schema/query concerns.

The accepted isolation layers are:

- Store isolation through `ApplicationId` and `IMemoryStoreContext`.
- Owner isolation through `owner_id`, `owner_key`, and `MemoryScope`.
- Session scoping through `session_id` and conversation IDs.

`owner_id = null` means shared/global memory. `owner_key` is used where merge semantics must distinguish shared and owned records. Owner-scoped vector paths must over-fetch candidates, filter by owner/shared rules, and then limit.

`IMemoryStoreContext` and owner/ranking contexts use AsyncLocal-backed implementations so per-request state can flow through async calls without singleton data races.

## Consequences

Positive consequences:

- Private memory does not leak across owners.
- Shared/global memory remains available by policy.
- Existing single-user deployments can continue with null owners.
- Store isolation can remain logical by default and become physical when Enterprise/AuraDB is available.

Tradeoffs:

- Queries are more complex.
- Vector retrieval must over-fetch to preserve scoped result quality.
- Store database routing adds operational constraints for Neo4j editions.
- Tests must cover every leak-prone path, not only the main recall path.

## Alternatives Considered

### Rely only on session IDs

Rejected. Sessions are runtime groupings, not security or tenant boundaries.

### Separate database per user only

Rejected as the default. It is operationally heavy and not compatible with Neo4j Community Edition multi-database limits.

### Filter in adapters only

Rejected. Adapters cannot safely cover every repository, GraphRAG, reasoning, and maintenance path.

## Verification Anchors

- `src/AgentMemory.Abstractions/Options/MemoryScope.cs` defines owner scope.
- `src/AgentMemory.Neo4j/Infrastructure/MemoryStoreOptions.cs` defines store strategy and AsyncLocal context.
- `src/AgentMemory.Neo4j/Queries/FactQueries.cs` uses `owner_key` in fact MERGE paths.
- `src/AgentMemory.Neo4j/Queries/SchemaQueries.cs` defines owner indexes.
- `docs/schema.md` documents owner/store isolation properties and indexes.
