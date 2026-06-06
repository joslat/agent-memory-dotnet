# Changelog

All notable changes to this project will be documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.0-preview.1] - 2026-06-06

First public preview release. This is a pre-release; public APIs may still change before 1.0.
NuGet package IDs are permanent once published.

### Added

#### Packages

- **`AgentMemory.Abstractions`** — Domain models (31 types across 3 memory tiers), service interfaces (`IMemoryService`, `IShortTermMemoryService`, `ILongTermMemoryService`, `IReasoningMemoryService`, `IMemoryContextAssembler`, `IMemoryExtractionPipeline`, `IEntityResolver`, and more), repository interfaces, and configuration options. Zero external dependencies except `Microsoft.Extensions.AI.Abstractions`.
- **`AgentMemory.Core`** — Memory service implementations, extraction pipeline (`ExtractionStage` → `PersistenceStage`), entity resolution chain (Exact → Fuzzy → Semantic → CreateNew), context assembler with token-budget enforcement, memory decay service (`MemoryDecayService` with configurable half-life), stub implementations for testing.
- **`AgentMemory.Neo4j`** — Neo4j repository implementations for all 9 domain repositories, centralised Cypher constants (145 in 14 domain files), schema bootstrapper and migration runner with versioned `.cypher` files, GraphRAG retrieval layer (Vector, Fulltext, Hybrid, Graph) internalized from `neo4j-maf-provider`. DI: `AddNeo4jAgentMemory()`.
- **`AgentMemory.Extraction.Llm`** — LLM-driven entity, fact, preference, and relationship extractors using `IChatClient` from `Microsoft.Extensions.AI`. DI: `AddLlmExtraction()`.
- **`AgentMemory.Extraction.AzureLanguage`** — Azure Text Analytics extractors for named entity recognition, fact extraction, and PII detection. DI: `AddAzureLanguageExtraction()`.
- **`AgentMemory.AgentFramework`** — Microsoft Agent Framework adapter: `Neo4jMemoryContextProvider` (`IContextProvider`), `Neo4jChatMessageStore`, `Neo4jMicrosoftMemoryFacade`, `MemoryToolFactory` (6 `AIFunction` tools), `AgentTraceRecorder`. DI: `AddAgentMemoryFramework()`.
- **`AgentMemory.SemanticKernel`** — Semantic Kernel adapter: memory plugin, text search, native SK DI integration. DI: `AddAgentMemorySemanticKernel()`.
- **`AgentMemory.Enrichment`** — Nominatim geocoding service and Wikimedia entity enrichment, both with caching and rate limiting. DI: `AddEnrichment()`.
- **`AgentMemory.Observability`** — OpenTelemetry decorator pattern wrapping `IMemoryService` and `IGraphRagContextSource` with distributed tracing spans and metrics. DI: `AddAgentMemoryObservability()`.
- **`AgentMemory.McpServer`** — MCP server with 21 tools, 6 resources (`memory://conversations`, `memory://entities`, `memory://preferences`, `memory://context/{sessionId}`, `memory://status`, `memory://schema`), and 3 prompts. Supports stdio and HTTP transports. DI: `AddAgentMemoryMcpTools()`.
- **`AgentMemory`** — Convenience meta-package bundling `Abstractions` + `Core` + `Neo4j` + `Extraction.Llm`. Single install for the most common use case.

#### Memory capabilities

- **Short-term memory** — session-scoped conversation history with participant tracking, recent message recall, semantic vector search, batch add
- **Long-term memory** — entities with canonical names, aliases, and dynamic labels; facts as SPO triples with confidence and validity periods; preferences by category; relationships between entities; all backed by vector and fulltext search
- **Reasoning memory** — reasoning traces from agent chains, steps (thought/action/observation), tool call recording with status and outcomes, similar-trace retrieval
- **Memory decay** — exponential decay scoring (`confidence × exp(−λ×days) + boost×access`) with configurable half-life and optional auto-prune
- **Temporal recall** — `RecallAsOfAsync` and point-in-time snapshot queries across all memory tiers using native Neo4j `datetime()` comparisons
- **Context assembly** — multi-tier recall with configurable token budget, truncation strategies, and blending modes
- **Metadata filtering** — `MetadataFilterBuilder` with `$eq`, `$ne`, `$contains`, `$in`, `$exists` operators
- **Session ID strategies** — `PerConversation`, `PerDay`, and `PersistentPerUser` via `ISessionIdGenerator`

#### Multi-user & multi-store isolation

- **Multi-user memory isolation (R1).** Memories now carry an optional `owner_id` so a single store can
  hold per-user memory alongside shared/global memory. Reads are scoped through **`MemoryScope`**
  (`{ string? OwnerId, bool IncludeShared = true }`): `owner_id = $ownerId OR (includeShared AND owner_id
  IS NULL)`. A `null` owner means shared/global (the prior behaviour), so existing single-tenant callers
  are unaffected. Facts additionally carry an `owner_key = coalesce(owner_id, '*')` sentinel that is part
  of the Fact MERGE key, so the same SPO triple asserted by different owners stays distinct. Vector-index
  reads over-fetch (topK × 5, floor 50) and post-filter by owner to avoid per-owner starvation. Every
  read/write surface — short-term, long-term (entities, facts, preferences, relationships), reasoning
  traces/steps, temporal recall, and all four retrievers — is owner-aware.
- **Ambient owner context (`IMemoryOwnerContext` / `IWritableMemoryOwnerContext`).** `AsyncLocal`-backed
  `DefaultMemoryOwnerContext` lets adapters set the current user once per request/agent flow so the
  LLM-invokable facade tools scope by owner **without trusting the model** to pass an id. Safe to register
  as a singleton (the value flows per async context, not process-wide).
- **Multi-store / application isolation (R1b).** `MemoryStorageStrategy` (`SharedDatabase` |
  `DatabasePerApplication`) plus `IMemoryStoreContext` / `IMemoryStoreProvisioner` map an `ApplicationId`
  to a Neo4j database, with an `AsyncLocal`-backed `DefaultMemoryStoreContext` ambient. Defaults to
  `SharedDatabase` inheriting `Neo4jOptions.Database`, so existing deployments are unchanged.

#### Consolidation, dedup & extraction

- **Dedup-on-create for facts and preferences.** New `LongTermMemoryOptions`
  (`DeduplicateOnCreate = true`, `DeduplicationSimilarityThreshold = 0.95`, `DeduplicationConfidenceBump
  = 0.05`): on add, a near-duplicate (same subject/predicate/owner for facts; same category/owner for
  preferences, above the similarity threshold) is updated in place with a confidence bump instead of
  creating a second node.
- **Consolidation / hygiene service (`IConsolidationService`).** Opt-in maintenance pass
  (`ConsolidationOptions`, `DryRun = true` by default) that archives expired conversations, removes
  duplicate preferences, and reports duplicate entities and over-long reasoning traces, emitting a
  `ConsolidationReport` and recording each run. Backed by migration `0004_consolidation.cypher`.
- **Conflict / contradiction detection (`IConflictDetectionService`).** Detect-only (never mutates):
  finds fact contradictions — same subject + predicate within an owner scope asserting ≥2 distinct
  objects — grouped per owner so it respects R1 isolation, with an optional confidence gate. Pairs with
  the consolidation service for the memory-hygiene story. Exposed via the `agentmemory conflicts` CLI command.
- **Streaming (chunked) extraction is DI-registered** (`IStreamingExtractor`). It is a standalone
  text → chunks → entities helper and does **not** persist; callers persist its output through their
  own ingestion path (where owner stamping applies). A built-in streaming persistence path is not
  yet wired.
- **`Conversation.Archived` reads back.** The consolidation pass sets `archived` on expired
  conversations; the domain model and `Neo4jConversationRepository` mapping now surface it (archival
  remains a consolidation-only write, not an upsert). `ConsolidationRun` is now declared in
  `SchemaConstants.NodeLabels` for parity with the Cypher that creates it.
- **MCP `memory_add_fact` accepts `category` and `metadata`.** The tool now surfaces the `Fact.Category`
  and `Fact.Metadata` fields (metadata as a JSON-object string) that were previously dropped.

#### Operational safety

- **Vector-index dimension validation at bootstrap.** Schema bootstrap (and per-application store
  provisioning) verifies every existing Neo4j vector index was created with the configured
  `Neo4jOptions.EmbeddingDimensions`, throwing `EmbeddingDimensionMismatchException` (which lists each
  offending index) when they differ. Because `CREATE VECTOR INDEX ... IF NOT EXISTS` never alters an
  existing index, switching embedding models would otherwise produce an opaque query-time failure; this
  is a fail-fast guard. Opt out via `Neo4jOptions.ValidateVectorIndexDimensions = false` (default `true`).

#### Reasoning provenance

- **`:TOUCHED` reasoning-audit edges.** `IReasoningMemoryService.RecordTouchedEntitiesAsync` /
  `GetTouchedEntitiesAsync` record and read which entities a reasoning step read or acted upon, as
  `(:ReasoningStep)-[:TOUCHED]->(:Entity)` edges (a `recorded_at` timestamp is stamped on create).
  Linking is by entity id to **existing** entities (it never creates entities, preserving the
  resolution/dedup pipeline), is idempotent, and silently skips ids that do not resolve. Ports the
  upstream `(:ReasoningStep)-[:TOUCHED]->(:Entity)` provenance edge (neo4j-labs/agent-memory PR #113).
- **Point-in-time reasoning-trace recall.** `IReasoningMemoryService.SearchSimilarTracesAsOfAsync` and
  `IReasoningTraceRepository.SearchByTaskVectorAsOfAsync` restrict task-vector search to traces that had
  started at or before the as-of instant, and `MemoryContextAssembler.AssembleContextAsOfAsync` now
  includes reasoning traces (previously omitted) — completing temporal recall across all memory tiers.

#### Entity auditability & feedback

- **`memory_get_entity_provenance` MCP tool.** Surfaces the (already-implemented) `EntityProvenance` —
  the source messages an entity was extracted from (with span/confidence) and the extractors that
  produced it — for auditability.
- **`Entity.UpdatedAtUtc` reads back.** Entity nodes already stamped `updated_at` on modification; the
  domain model and `Neo4jEntityRepository` mapping now surface it (last-modified semantics — null until
  first update), exposed on `memory_get_entity`.
- **Entity feedback.** `ILongTermMemoryService.RecordEntityFeedbackAsync` (and the
  `memory_record_entity_feedback` MCP tool) nudge an entity's confidence — positive reinforces, negative
  penalizes — clamped to [0,1], with the magnitude configurable via
  `LongTermMemoryOptions.FeedbackConfidenceDelta` (default 0.1). Owner-scoped (R1): with a `userId`/scope
  it only affects the user's own or shared entities, never another user's private entity.

#### Operational tooling

- **`agentmemory` CLI** (`tools/AgentMemory.Cli`) — an operations command-line front end over the
  shipped maintenance services: `migrate` (apply Cypher migrations), `bootstrap` (create schema
  constraints/indexes), `consolidate [--apply]` (memory-hygiene pass, dry-run by default),
  `conflicts` (detect fact contradictions), and `decay` (prune decayed memories). Connection resolves from CLI options, `Neo4j:*`
  config, or `NEO4J_*` env vars. Built for CI/CD migrations, K8s init containers, and scheduled
  pruning. (Not a published NuGet package.)

#### Search and retrieval

- Vector similarity search across all memory layers (5 indexes + reasoning-step index)
- Fulltext BM25 search (3 indexes: message content, entity name, fact content)
- Hybrid retrieval (vector + BM25 combined with max-score merge)
- Graph multi-hop traversal (`RELATED_TO*1..2`) via `Neo4jGraphRagContextSource`
- Temporal point-in-time retrieval for entities, facts, and preferences

#### Graph schema

- 13 node labels, 11 uniqueness constraints
- 6 vector indexes, 3 fulltext indexes, 1 geospatial Point index, plus owner-scope and consolidation property indexes
- Versioned migration runner (`MigrationRunner`) with `.cypher` migration files

#### Testing

- Extensive unit test suite covering all packages including stub-based tests without external services
- Integration test suite using Testcontainers (disposable Neo4j 5 containers)
- Semantic Kernel adapter unit tests
- Cypher snapshot tests for query validation

### Changed

> The items below describe API shaping done before first publish.

- **`IMemoryService` split into role interfaces** (`AgentMemory.Abstractions`). Its members are now
  declared on three focused interfaces — `IMemoryRecall` (read), `IMemoryIngestion` (write), and
  `IMemoryMaintenance` (upkeep) — and `IMemoryService` composes all three
  (`IMemoryService : IMemoryRecall, IMemoryIngestion, IMemoryMaintenance`). Consumers of
  `IMemoryService` are source-compatible (all members remain available); new code can depend on a
  narrow role for ISP. DI binds all three roles to the same scoped instance.
- **`IEmbeddingOrchestrator` slimmed to two primitives** (`AgentMemory.Abstractions`). The interface
  now declares only `EmbedAsync(string)` and the new `EmbedBatchAsync(IReadOnlyList<string>)`. The
  six domain-specific methods (`EmbedEntityAsync`, `EmbedFactAsync`, `EmbedPreferenceAsync`,
  `EmbedMessageAsync`, `EmbedQueryAsync`, `EmbedTextAsync`) are preserved as **extension methods**
  in `EmbeddingOrchestratorExtensions` (same namespace), so call sites that `using
  AgentMemory.Abstractions.Services` are source-compatible. Code that *implements* or *mocks*
  `IEmbeddingOrchestrator` must now implement/mock `EmbedAsync`/`EmbedBatchAsync`.
- Renamed all NuGet packages from `Neo4j.AgentMemory.*` to `AgentMemory.*` to remove the implied Neo4j
  affiliation before first publish. C# namespaces updated accordingly across all 11 source packages, 3
  test projects, and 3 sample projects.
- **Retrieval blend modes are now enforced.** `RecallOptions.BlendMode` previously had no effect —
  every mode behaved like `Blended`. `MemoryContextAssembler` now honors it: `MemoryOnly` suppresses
  GraphRAG, `GraphRagOnly` suppresses the memory layers (and the query-embedding call), and
  `GraphRagOnly`/`GraphRagThenMemory` render GraphRAG context ahead of memory in both
  `MemoryContextFormatter` and the MAF context mapper. `MemoryContext` gains a `BlendMode` property
  (defaults to `Blended`, so existing output ordering is unchanged).
- **Microsoft Agent Framework (MAF) 1.9.0** (from 1.1.0) and `Microsoft.Extensions.AI.Abstractions`
  10.5.1 (from 10.4.1, the floor MAF 1.9.0 requires). The migration was source-compatible — no adapter
  code changes — see `docs/plans/maf-1.9.0-migration.md`.

### Fixed

- **The meta `AddNeo4jAgentMemory` is now self-sufficient.** It now registers default `IClock`
  (`SystemClock`) and `IIdGenerator` (`GuidIdGenerator`) via `TryAdd`, so consolidation, reasoning,
  the context assembler, and dedup resolve out of the box. Previously these were *registered* but not
  *resolvable* — every consumer (and every sample) had to register the two primitives by hand. Consumers
  can still override by registering their own first.
- **`DatabasePerApplication` provisioning now works.** `Neo4jMemoryStoreProvisioner` inlined the store
  database name into `CREATE DATABASE … IF NOT EXISTS WAIT` unquoted. The default `DatabasePrefix` is
  `mem-`, so every provisioned name contains a dash — which is a Cypher syntax error unquoted, breaking
  store provisioning for all real `DatabasePerApplication` users. The name is now backtick-quoted.
  (Caught by a new live Neo4j Enterprise integration test; previously only mock-tested.)
- **Memory-only DI now works.** Two `AddAgentMemoryCore` registrations failed at runtime for consumers
  that don't add the GraphRAG adapter: `MemoryContextAssembler` required `IGraphRagContextSource`
  (now resolved optionally via `GetService`), and `IMemoryExtractionPipeline` was registered by type
  despite an internal constructor (now registered via a factory).
- **`ReasoningStep.TimestampUtc` / `ToolCall.TimestampUtc` are now read back.** Both nodes are created
  with a server `timestamp` that was previously write-only from .NET; the domain records gained the
  property and the Neo4j mappers now populate it.
- **`Fact.Category` is now persisted and read back.** It was defined on the domain model and indexed
  (`fact_category`) but omitted from the upsert queries and mapping, so it was silently dropped on
  write and always returned `null`. `FactQueries.Upsert`/`UpsertBatch`, the repository parameters, and
  `MapToFact` now round-trip it (mirroring `Preference.Category`).
- **CI now builds and tests.** `.github/workflows/squad-ci.yml` was a placeholder that ran no
  commands; it now restores, builds, and runs unit + SemanticKernel tests plus the Testcontainers
  integration suite. Fixed `Directory.Build.props` so the src-only `TreatWarningsAsErrors` condition
  evaluates correctly on non-Windows CI runners, and tagged `Neo4jConnectivityTests` with
  `[Trait("Category", "Integration")]` so it no longer leaks into the unit-test filter.

---

[Unreleased]: https://github.com/joslat/agent-memory-dotnet/compare/v0.1.0-preview.1...HEAD
[0.1.0-preview.1]: https://github.com/joslat/agent-memory-dotnet/releases/tag/v0.1.0-preview.1
