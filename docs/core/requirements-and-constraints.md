# Requirements and Constraints

Status: current as of 2026-07-09.

This document states what Agent Memory for .NET must do, what it should preserve, what constraints shape the implementation, and what remains pending or intentionally deferred.

## Functional Requirements

### FR-1: Three-Layer Memory

The project must provide three memory layers:

| Layer | Required content |
|---|---|
| Short-term | Conversations, messages, session identifiers, roles, timestamps, content, message ordering. |
| Long-term | Entities, facts, preferences, entity-to-entity relationships, provenance, embeddings, confidence, owner scope. |
| Reasoning | Reasoning traces, steps, tool calls, tool aggregate nodes, task embeddings, success/status metadata. |

### FR-2: Unified Memory Facade

The project must expose a high-level `IMemoryService` facade composed from narrower role interfaces:

- `IMemoryRecall`
- `IMemoryIngestion`
- `IMemoryMaintenance`

Consumers should be able to depend on the narrow interface that matches their use case while existing consumers can continue using `IMemoryService`.

### FR-3: Graph Persistence

The primary persistence implementation must use Neo4j. It must provide repositories for conversations, messages, entities, facts, preferences, relationships, reasoning traces, reasoning steps, tool calls, extractors, schemas, graph query, memory history, consolidation, conflict detection, and memory decay.

### FR-4: Schema Bootstrap and Migration

The Neo4j package must bootstrap constraints and indexes idempotently and must track migrations using `:Migration` nodes. Bootstrap must verify vector index dimensions when configured to do so.

### FR-5: Embedding Abstraction

Embedding generation must use `Microsoft.Extensions.AI` through `IEmbeddingGenerator<string, Embedding<float>>`. The core package may provide stubs, but production semantic search depends on a real MEAI-compatible embedding generator.

### FR-6: Extraction Pipeline

The core must provide an extraction pipeline with distinct extraction and persistence stages. It must support entity, fact, preference, and relationship extractors. Default extractors must be no-op stubs; LLM and Azure Language extraction must be opt-in.

### FR-7: Retrieval Modes

The project must support:

- recent-message recall,
- vector search,
- fulltext search,
- hybrid search,
- graph traversal search,
- owner-scoped search,
- temporal/as-of recall,
- reasoning-trace similarity search,
- optional GraphRAG context assembly.

### FR-8: Isolation

The project must isolate memory at three levels:

| Level | Mechanism |
|---|---|
| Store | `ApplicationId` through `IMemoryStoreContext`; default shared database, optional database per application. |
| Owner | `owner_id`, `owner_key`, and `MemoryScope`; null owner means shared/global. |
| Session | `session_id` and conversation identifiers for run-local context. |

### FR-9: Framework Surfaces

The project must provide .NET agent integration surfaces:

- direct DI/API usage,
- Microsoft Agent Framework adapter,
- Semantic Kernel adapter,
- Model Context Protocol server.

Additional framework integrations are optional and demand-driven.

### FR-10: Operational Tools

The project must provide operational commands and APIs for schema bootstrap/checks, migrations, consolidation, decay/invalidation, memory history inspection, conflict inspection, schema parity checks, and maintenance workflows.

### FR-11: Optional Cross-Cutting Capabilities

Observability, enrichment, Azure Language extraction, and analytics must be opt-in. Basic memory storage and recall must not require those dependencies.

### FR-12: Documentation Alignment

The documentation must distinguish current truth from historical plans. Core docs must align with package names, DI methods, schema constants, and shipped functionality.

## Non-Functional Requirements

| Area | Requirement |
|---|---|
| Runtime | Target `net9.0`. |
| Language | Nullable reference types enabled; implicit usings enabled. |
| Build quality | Source projects treat warnings as errors. |
| Async | Library code uses async APIs and enforces CA2007-style `ConfigureAwait(false)` discipline where applicable. |
| Testability | Core behavior must be unit-testable without Neo4j; Neo4j behavior must have integration coverage. |
| Compatibility | Schema names should preserve Python-compatible labels, relationship types, and snake_case properties where relevant. |
| Extensibility | Infrastructure, extraction, ranking, and framework surfaces should be replaceable through DI. |
| Observability | Optional telemetry should decorate behavior without becoming required for construction. |
| Security/privacy | Scoped reads and writes must prevent cross-owner and cross-store memory leaks. |
| Reliability | Schema bootstrap must be idempotent and fail fast on incompatible vector dimensions when validation is enabled. |

## Technical Constraints

### Neo4j Constraint

The supported persistence backend is Neo4j 5.x. Vector indexes, fulltext indexes, point indexes, and Cypher behavior are part of the design. The default database is `neo4j` unless configured otherwise.

### Neo4j Edition Constraint

`MemoryStorageStrategy.SharedDatabase` works with Neo4j Community Edition. `MemoryStorageStrategy.DatabasePerApplication` requires Neo4j Enterprise or AuraDB because it creates/routes to separate databases.

### Embedding Dimension Constraint

Neo4j vector indexes are created with a fixed dimension. `Neo4jOptions.EmbeddingDimensions` must match the model used by the configured embedding generator. Bootstrap validates existing vector index dimensions by default.

### Optional Dependency Constraint

The meta-package references several packages for convenience, but optional behaviors still require explicit registration:

- LLM extraction runs only when the caller passes `configureLlm`.
- Observability runs only through `WithObservability()`.
- Enrichment runs only through `WithEnrichment()`.
- Azure Language extraction runs only through `WithAzureLanguageExtraction(...)`.
- GraphRAG retrieval runs only through `AddGraphRagAdapter(...)` after Neo4j registration.
- Analytics is a separate opt-in package and is not part of the meta-package.

### External Service Constraint

LLM extraction, Azure Language extraction, geocoding, Wikimedia/Diffbot enrichment, OpenAI/Azure OpenAI embeddings, and Neo4j GDS features depend on external services or plugins. The core library must remain usable without them.

### License and Identity Constraint

The repository license is MIT. The project is independent and must not present itself as an official Neo4j product.

## Current Pending Work

These items are not known correctness bugs in the current library. They are forward work toward adoption and `1.0`.

| Item | Status |
|---|---|
| Preview soak | Pending real-world usage feedback on `0.1.0-preview.4`. |
| API stabilization | Pending final public-surface review before `1.0`. |
| Local embedding adapter | Deferred; likely ONNX/sentence-transformers via MEAI. |
| Local NLP extractors | Deferred; GLiNER/ONNX-style extraction remains a demand-driven gap. |
| Additional framework integrations | Deferred; AutoGen.NET/LangChain.NET/Semantic Router style integrations are optional future work. |
| Opik-style observability | Deferred; current observability is OpenTelemetry-focused. |
| CLI breadth | Shipped CLI is substantial; richer import/export/stats/search ergonomics remain possible. |
| GitHub prerelease flag | Minor workflow cleanup: preview GitHub releases can be marked prerelease if desired. |
| Documentation drift checks | Ongoing discipline; this docs pass creates the core set and realigns active docs. |

## Current Known Gaps Versus Python Ecosystem Breadth

The .NET project is not trying to reproduce every Python ecosystem integration. Gaps that remain intentional or demand-driven include:

- spaCy/GLiNER/GLiREL-style local extraction parity,
- Python-specific agent framework integrations,
- a packaged local embedding provider,
- hosted-service/NAMS-style backend abstraction,
- reusable test utility package for downstream consumers.

## Verification Snapshot

For this 2026-07-09 work, local verification passed:

- 2658 Release unit tests,
- 5 targeted live Neo4j shakedown tests for golden-path owner scoping and memory history,
- 0 failures,
- 0 skipped.

The earlier 2026-07-09 docs cleanup also recorded 34 Semantic Kernel tests passing. The latest full integration record remains the ROADMAP entry from 2026-06-21: 236 live Neo4j integration tests passing.
