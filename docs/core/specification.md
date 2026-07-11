# Project Specification

Status: current as of 2026-07-09.

This specification is normative for documentation purposes. It describes the expected behavior, structure, and constraints of Agent Memory for .NET as implemented in the current repository.

## 1. Product Definition

Agent Memory for .NET is an independent, MIT-licensed, .NET 9 library for graph-native persistent memory in AI-agent applications. It stores and retrieves short-term conversation history, long-term extracted knowledge, and reasoning traces using Neo4j as the primary persistence backend.

The project is inspired by `neo4j-labs/agent-memory` and preserves compatible graph concepts where useful, but it is implemented independently for the .NET ecosystem.

## 2. Terminology

| Term | Definition |
|---|---|
| Store | An application-level memory space, selected by `ApplicationId`. |
| Owner | A user or principal inside a store, represented by `owner_id`. |
| Shared/global memory | Memory with `owner_id = null`; visible to scoped reads when `IncludeShared` is true. |
| Session | A run or conversation grouping, represented by `session_id` and conversation identifiers. |
| Short-term memory | Conversations and messages. |
| Long-term memory | Entities, facts, preferences, and relationships. |
| Reasoning memory | Traces, steps, tool calls, and tool aggregates. |
| As-of recall | Retrieval of memory as valid or believed at a previous point in time. |
| Memory history | Read-only lifecycle view over long-term records, including invalidation timestamps, supersession links, valid-time windows, provenance message ids, and owner scope. |
| GraphRAG context source | Optional Neo4j-backed retrieval component exposed as `IGraphRagContextSource`. |

## 3. Runtime and Packaging

### 3.1 Target Framework

All source packages MUST target `net9.0` unless a future ADR changes the target.

### 3.2 Package Set

The project ships the following packages:

| Package | Required role |
|---|---|
| `AgentMemory.Abstractions` | Common domain, contracts, options, schema constants. |
| `AgentMemory.Core` | Portable service implementation and orchestration. |
| `AgentMemory.Neo4j` | Neo4j persistence, schema, migrations, GraphRAG context. |
| `AgentMemory.Extraction.Llm` | Optional LLM extractors. |
| `AgentMemory.Extraction.AzureLanguage` | Optional Azure Language extractors. |
| `AgentMemory.AgentFramework` | Microsoft Agent Framework adapter. |
| `AgentMemory.SemanticKernel` | Semantic Kernel adapter. |
| `AgentMemory.McpServer` | MCP server surface. |
| `AgentMemory.Observability` | Optional OpenTelemetry instrumentation. |
| `AgentMemory.Enrichment` | Optional geocoding and entity enrichment. |
| `AgentMemory.Analytics` | Optional Neo4j GDS analytics. |
| `AgentMemory` | Convenience meta-package. |

### 3.3 Meta-Package Behavior

`AgentMemory` MUST register the common stack through `AddNeo4jAgentMemory(...)`:

- Core services,
- Neo4j infrastructure,
- LLM extraction only when `configureLlm` is supplied,
- optional store configuration.

The meta-package MAY reference optional packages for convenience, but optional behaviors MUST still require explicit registration.

## 4. Dependency Boundaries

### 4.1 Abstractions

`AgentMemory.Abstractions` MUST define contracts and shared model types. It MUST NOT depend on Neo4j infrastructure or framework adapter implementations.

### 4.2 Core

`AgentMemory.Core` MUST implement portable orchestration over repository and extractor ports. It MUST NOT require Neo4j, LLM extraction, observability, enrichment, or framework adapters to construct the basic memory services.

### 4.3 Infrastructure and Adapters

Infrastructure and adapter packages MUST depend inward on abstractions and core contracts. They SHOULD remain thin where possible: translate framework/runtime concepts into memory service calls and preserve identity, cancellation, and scope.

## 5. Public Service Surface

The project MUST expose:

- `IMemoryService`,
- `IMemoryRecall`,
- `IMemoryIngestion`,
- `IMemoryMaintenance`,
- `IMemoryQueryFacade`,
- `IMemoryHistoryService`,
- repository interfaces,
- extraction interfaces,
- identity/scope context interfaces,
- Neo4j bootstrap/migration interfaces.

`IMemoryService` MUST remain a facade over the narrower role interfaces.

## 6. Configuration

### 6.1 Core Memory Configuration

`MemoryOptions` MUST hold sub-options for short-term memory, long-term memory, reasoning memory, recall, context budget, extraction, decay, ranking, and GraphRAG enablement.

Core automatic extraction on message persist MUST NOT be implied by a root option. Automatic extraction is an adapter concern.

### 6.2 Neo4j Configuration

`Neo4jOptions` MUST include connection URI, username, password, database, pool settings, encryption flag, embedding dimensions, and vector-index dimension validation.

### 6.3 Store Configuration

`MemoryStoreOptions` MUST support:

- `SharedDatabase`, default,
- `DatabasePerApplication`, opt-in,
- default database inheritance,
- database prefix,
- auto-provisioning.

## 7. Memory Data Model

### 7.1 Required Node Labels

The schema MUST include these labels where the relevant features are used. All except `Migration` are declared as `SchemaConstants.NodeLabels` constants; `Migration` is emitted only as a Cypher literal by the schema/migration queries.

- `Conversation`
- `Message`
- `Entity`
- `Fact`
- `Preference`
- `ReasoningTrace`
- `ReasoningStep`
- `ToolCall`
- `Tool`
- `Extractor`
- `Schema`
- `ConsolidationRun`
- `MemoryReadAudit` (recall-time read-audit node; see §9.7)
- `Migration` (Cypher literal only, not a `NodeLabels` constant)

### 7.2 Required Relationship Types

The schema MUST use the relationship types declared by `SchemaConstants.RelationshipTypes`, including:

- `HAS_MESSAGE`
- `FIRST_MESSAGE`
- `NEXT_MESSAGE`
- `MENTIONS`
- `RELATED_TO`
- `ABOUT`
- `SAME_AS`
- `SUPERSEDED_BY`
- `HAS_STEP`
- `USES_TOOL`
- `INSTANCE_OF`
- `TOUCHED`
- `HAS_TRACE`
- `IN_SESSION`
- `INITIATED_BY`
- `TRIGGERED_BY`
- `EXTRACTED_FROM`
- `EXTRACTED_BY`
- `HAS_FACT`
- `HAS_PREFERENCE`

### 7.3 Property Naming

Cypher-facing property names MUST use the snake_case constants defined in `SchemaConstants.Properties`. C# domain models MAY use PascalCase.

### 7.4 Identity Properties

All primary node types with identity MUST have `id` or the identity key documented in the schema. `Tool` is keyed by `name`. `Extractor` has a unique `name`. `Migration` is keyed by `version`.

### 7.5 Fact Merge Semantics

Fact upsert MUST merge by `{subject, predicate, object, owner_key}` in both single and batch paths. `id` MUST be set on create and preserved on match. Reasserting a matched fact MUST restore live recall by clearing `invalidated_at`.

### 7.6 Temporal Semantics

Records that support non-destructive invalidation MUST use `invalidated_at` as the transaction-time belief axis. Facts and preferences SHOULD use `valid_from` and `valid_until` as valid-time fields. Supersession MUST preserve the superseded record and link to the winner with `SUPERSEDED_BY`.

## 8. Isolation Semantics

### 8.1 Store Scope

When `MemoryStorageStrategy.SharedDatabase` is used, all stores route to the configured default database and rely on owner/session scope for logical isolation.

When `MemoryStorageStrategy.DatabasePerApplication` is used, non-null `ApplicationId` values MUST route to a database derived from the configured prefix and sanitized application ID. Auto-provisioning MAY create and bootstrap the database on first use.

### 8.2 Owner Scope

Owner-scoped writes MUST stamp the owner when the operation has an owner. Owner-scoped reads MUST restrict results to the owner and optionally shared/global records according to `IncludeShared`.

Owner-scoped deletes, invalidation, supersession, relationship reads, reasoning trace reads, GraphRAG retrieval, non-vector reads, and vector reads MUST NOT leak another owner private records.

### 8.3 Shared Memory

`owner_id = null` means shared/global memory. Shared memory MUST remain distinct from owned memory in merge keys that would otherwise collapse nulls; `owner_key` exists for this reason.

### 8.4 Ambient Identity

Framework adapters and LLM-invokable tools SHOULD use trusted ambient owner/store context rather than trusting model-supplied identity values.

## 9. Retrieval Semantics

### 9.1 Recall

Recall MUST assemble relevant short-term, long-term, reasoning, and optional GraphRAG context according to `RecallRequest`, options, scope, ranking, and context budget.

### 9.2 Vector Search

Vector search MUST query the configured vector indexes and apply score thresholds. Owner-scoped vector queries MUST over-fetch, filter by owner/shared rules, and then limit.

### 9.3 Fulltext Search

Fulltext search MUST use Neo4j fulltext indexes and escape Lucene-sensitive input where applicable.

### 9.4 Hybrid Search

Hybrid search SHOULD combine vector and fulltext results through scale-resistant ranking fusion.

### 9.5 Graph Traversal

Graph traversal search MUST respect configured hop limits and owner scope.

### 9.6 Ranking

Ranking MAY include semantic score, recency, structural hop decay, and query-intent presets. The default profile SHOULD preserve parity-style semantic ranking unless configured otherwise.

### 9.7 Read Audit

Long-term recall hits MUST update access telemetry on the matched node: set `last_accessed_at` to the read time and increment `access_count`. Each such read MUST also `CREATE` a `(:MemoryReadAudit)` node capturing `memory_id`, `owner_id`, `read_at`, and the post-increment `access_count`. These signals feed recency/frequency ranking (§9.6) and are non-destructive to the audited record.

## 10. Extraction Semantics

### 10.1 Default Behavior

Core extraction MUST resolve without external LLM dependencies by using stub extractors. Stub extractors MUST return no extracted memory rather than fabricating content.

### 10.2 LLM Extraction

LLM extraction MUST be opt-in and registered through `AddLlmExtraction(...)` or the meta-package `configureLlm` delegate. It SHOULD use `Microsoft.Extensions.AI` chat abstractions.

### 10.3 Azure Language Extraction

Azure Language extraction MUST be opt-in through `WithAzureLanguageExtraction(...)`.

### 10.4 Persistence

Persistence MUST owner-stamp extracted memory from `ExtractionRequest.UserId` or equivalent trusted context. Persistence MUST use repository ports and preserve provenance links to source messages where available.

## 11. Framework Adapter Semantics

### 11.1 Microsoft Agent Framework

The MAF adapter MUST support context injection, post-run persistence, trace recording, memory tools, and identity propagation from session state.

### 11.2 Semantic Kernel

The SK adapter MUST expose memory functions as a plugin and respect owner/session parameters supported by the facade.

### 11.3 MCP

The MCP server MUST expose memory operations through MCP tools/resources/prompts. It SHOULD depend on abstractions and host wiring rather than constructing a fixed persistence stack internally.

## 12. Optional Capability Semantics

| Capability | Required behavior |
|---|---|
| Observability | Must be opt-in and decorator-based. |
| Enrichment | Must be opt-in and resilient to external failures. |
| Analytics | Must be opt-in and gracefully no-op when GDS is unavailable. |
| GraphRAG | Must be opt-in through Neo4j registration and optional in context assembly. |

## 13. Operations and CLI

Operational tooling MUST support schema bootstrap/checks, migration, schema parity checks, consolidation, decay/invalidation, supersession, memory history inspection, conflict inspection, and graph query workflows. Commands SHOULD fail clearly when required external services are unavailable or misconfigured.

## 14. Testing Specification

The project SHOULD maintain:

- unit tests for portable behavior,
- integration tests for Neo4j behavior,
- adapter tests for framework mappings,
- schema/query tests for Cypher drift,
- regression tests for owner/store isolation,
- tests that reproduce defect triggers before fixes.

Documentation MUST date test counts.

## 15. Compatibility and Versioning

Preview releases may refine public APIs. The road to `1.0` requires preview soak and API stabilization. Once `1.0` is reached, package APIs SHOULD follow SemVer expectations.

Upstream compatibility MUST be treated as a verification guardrail. The project SHOULD preserve shared graph concepts and snake_case spellings where useful, but intentional .NET supersets such as `owner_id`, `owner_key`, and `invalidated_at` are valid when documented by ADR and `SchemaParityPolicy`.

Static compatibility checks MUST use `agentmemory schema-parity` and the same `SchemaParityVerifier` covered by unit tests. Behavioral compatibility SHOULD track `neo4j-labs/agent-memory-tck` through a bridge adapter or mirrored .NET integration scenarios. Snapshot refreshes SHOULD target tagged upstream releases or material Bolt/schema changes, not docs-only upstream churn.

## 16. Non-Goals

The project does not currently specify:

- a hosted memory service backend,
- complete Python ecosystem adapter parity,
- built-in local NLP extraction parity,
- a built-in local embedding model package,
- a hard dependency on Neo4j GDS,
- official Neo4j product support.
