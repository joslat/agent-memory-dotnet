## [Unreleased]
### Changed (breaking)
- **`IMemoryService` split into role interfaces** (`AgentMemory.Abstractions`). Its members are now
  declared on three focused interfaces — `IMemoryRecall` (read), `IMemoryIngestion` (write), and
  `IMemoryMaintenance` (upkeep) — and `IMemoryService` composes all three
  (`IMemoryService : IMemoryRecall, IMemoryIngestion, IMemoryMaintenance`). Consumers of
  `IMemoryService` are source-compatible (all members remain available); new code can depend on a
  narrow role for ISP. DI binds all three roles to the same scoped instance. **Migration:** code that
  referenced these members via reflection or re-declared `IMemoryService` members should account for
  the members now originating on the base role interfaces.
- **`IEmbeddingOrchestrator` slimmed to two primitives** (`AgentMemory.Abstractions`). The interface
  now declares only `EmbedAsync(string)` and the new `EmbedBatchAsync(IReadOnlyList<string>)`. The
  six domain-specific methods (`EmbedEntityAsync`, `EmbedFactAsync`, `EmbedPreferenceAsync`,
  `EmbedMessageAsync`, `EmbedQueryAsync`, `EmbedTextAsync`) are preserved as **extension methods**
  in `EmbeddingOrchestratorExtensions` (same namespace), so call sites that `using
  AgentMemory.Abstractions.Services` are source-compatible. **Migration:** code that *implements* or
  *mocks* `IEmbeddingOrchestrator` must now implement/mock `EmbedAsync`/`EmbedBatchAsync` instead of
  the typed methods (the typed methods, being extensions, can no longer be overridden or substituted).

### Changed
- Renamed all NuGet packages from `Neo4j.AgentMemory.*` to `AgentMemory.*` to remove
  implied Neo4j affiliation before first publish. NuGet IDs are permanent once published.
  C# namespaces updated accordingly across all 11 source packages, 3 test projects,
  and 3 sample projects (453 .cs files, 17 .csproj files, 1 .slnx solution file).
- **Retrieval blend modes are now enforced.** `RecallOptions.BlendMode` previously had no effect —
  every mode behaved like `Blended`. `MemoryContextAssembler` now honors it: `MemoryOnly` suppresses
  GraphRAG, `GraphRagOnly` suppresses the memory layers (and the query-embedding call), and
  `GraphRagOnly`/`GraphRagThenMemory` render GraphRAG context ahead of memory in both
  `MemoryContextFormatter` and the MAF context mapper. `MemoryContext` gains a `BlendMode` property
  (defaults to `Blended`, so existing output ordering is unchanged).
- **Upgraded to Microsoft Agent Framework (MAF) 1.9.0** (from 1.1.0) and `Microsoft.Extensions.AI.Abstractions`
  10.5.1 (from 10.4.1, the floor MAF 1.9.0 requires). The migration was source-compatible — no adapter
  code changes — see `docs/plans/maf-1.9.0-migration.md`.

### Fixed
- **Memory-only DI now works.** Two `AddAgentMemoryCore` registrations failed at runtime for consumers
  that don't add the GraphRAG adapter: `MemoryContextAssembler` required `IGraphRagContextSource`
  (now resolved optionally via `GetService`), and `IMemoryExtractionPipeline` was registered by type
  despite an internal constructor (now registered via a factory). Both surfaced when building the real
  MAF agent sample.
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

# Changelog

All notable changes to this project will be documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

> **Note:** This project has not yet published official NuGet releases.  
> This changelog will track releases once versioning begins.

---

## [Unreleased]

### Added

#### Packages

- **`AgentMemory.Abstractions`** — Domain models (31 types across 3 memory tiers), service interfaces (`IMemoryService`, `IShortTermMemoryService`, `ILongTermMemoryService`, `IReasoningMemoryService`, `IMemoryContextAssembler`, `IMemoryExtractionPipeline`, `IEntityResolver`, and more), repository interfaces, and configuration options. Zero external dependencies except `Microsoft.Extensions.AI.Abstractions`.
- **`AgentMemory.Core`** — Memory service implementations, extraction pipeline (`ExtractionStage` → `PersistenceStage`), entity resolution chain (Exact → Fuzzy → Semantic → CreateNew), context assembler with token-budget enforcement, memory decay service (`MemoryDecayService` with configurable half-life), stub implementations for testing.
- **`AgentMemory.Neo4j`** — Neo4j repository implementations for all 9 domain repositories, centralised Cypher constants (145+ in 13 domain files), schema bootstrapper and migration runner with versioned `.cypher` files, GraphRAG retrieval layer (Vector, Fulltext, Hybrid, Graph) internalized from `neo4j-maf-provider`. DI: `AddNeo4jAgentMemory()`.
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
- **Context assembly** — multi-tier recall with configurable token budget, truncation strategies, and 5 blending modes (Union, Intersection, Confidence, Cascade, FirstSuccess)
- **Metadata filtering** — `MetadataFilterBuilder` with `$eq`, `$ne`, `$contains`, `$in`, `$exists` operators
- **Session ID strategies** — `PerConversation`, `PerDay`, and `PersistentPerUser` via `ISessionIdGenerator`

#### Search and retrieval

- Vector similarity search across all memory layers (5 indexes + reasoning-step index)
- Fulltext BM25 search (3 indexes: message content, entity name, fact content)
- Hybrid retrieval (vector + BM25 combined with max-score merge)
- Graph multi-hop traversal (`RELATED_TO*1..2`) via `Neo4jGraphRagContextSource`
- Temporal point-in-time retrieval for entities, facts, and preferences

#### Graph schema

- 12 node labels, 87 node properties, 10 constraints
- 6 vector indexes, 3 fulltext indexes, 1 geospatial Point index
- Versioned migration runner (`MigrationRunner`) with `.cypher` migration files

#### Testing

- Extensive unit test suite covering all packages including stub-based tests without external services
- Integration test suite using Testcontainers (disposable Neo4j 5 containers)
- Semantic Kernel adapter unit tests
- Cypher snapshot tests for query validation

---

[Unreleased]: https://github.com/joslat/agent-memory-dotnet
