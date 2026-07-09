# ADR 0004 - Neo4j-Native Persistence

Status: Accepted

Date: 2026-07-09

## Context

Agent memory involves entities, relationships, provenance, temporal validity, vector search, fulltext search, graph traversal, and reasoning traces. A relational or document-only store would require rebuilding graph behavior outside the database.

The project is also explicitly inspired by a Neo4j-backed memory model and wants schema compatibility where practical.

## Decision

Use Neo4j as the primary native persistence backend.

Neo4j is responsible for:

- memory graph storage,
- node and relationship persistence,
- vector indexes,
- fulltext indexes,
- property and point indexes,
- graph traversal retrieval,
- schema bootstrap and migration tracking,
- optional GDS analytics.

The Core package remains port-based, but the shipped persistence implementation is Neo4j.

## Consequences

Positive consequences:

- Graph relationships are first-class.
- Retrieval can blend vector, fulltext, hybrid, and graph traversal.
- Provenance and temporal relationships are queryable.
- Schema can align with the Python graph-memory ecosystem.

Tradeoffs:

- Users need Neo4j 5.x.
- Vector index dimensions must match the embedding model.
- Physical database-per-application isolation requires Enterprise or AuraDB.
- Integration tests require live Neo4j infrastructure.

## Alternatives Considered

### In-memory or file-backed default store

Rejected as the primary implementation. It could help tests or demos, but it would not exercise the graph-native design.

### Relational database persistence

Rejected for the current project. Relationship traversal and graph-shaped recall are central, not incidental.

### Document database persistence

Rejected for the current project. It would work for message history but would weaken graph relationships and provenance.

## Verification Anchors

- `src/AgentMemory.Neo4j/` implements infrastructure, repositories, queries, schema bootstrap, and migrations.
- `src/AgentMemory.Neo4j/Queries/SchemaQueries.cs` defines constraints and indexes.
- `docs/schema.md` documents the current graph schema.
- Integration tests under `tests/AgentMemory.Tests.Integration` validate live Neo4j behavior.
