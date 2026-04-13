# Implementation Status — Agent Memory for .NET

**Last Updated:** 2026-04-14  
**Author:** Deckard (Lead Architect)  
**For:** Jose Luis Latorre Millas (Project Owner)

---

## 1. Executive Summary

**Current Phase:** Phase 6 — MCP Server (✅ COMPLETE)

**Phase 6 Status: 100% COMPLETE** — MCP Server package with 14 tools, stdio transport, sample host, and Claude Desktop config. All 6 implementation phases are now complete with 398 unit tests passing.

**What's Done:**
- **Phase 1:** Core memory engine with all repositories, services, context assembly, 85 tests
- **Phase 2:** Entity resolution chain (4 strategies: ExactMatch → FuzzyMatch → SemanticMatch → CreateNew), Entity validation, LLM extraction package (4 extractors), real MemoryExtractionPipeline, Neo4j extraction support, FuzzySharp 2.0.2 integration, Microsoft.Extensions.AI IChatClient, DI infrastructure, 210 tests total
- **Phase 3:** Neo4jMemoryContextProvider (AIContextProvider), Neo4jChatMessageStore (MAF-compatible), Neo4jMicrosoftMemoryFacade, MafTypeMapper (bidirectional mapping), MemoryToolFactory (6 tools), AgentTraceRecorder, DI: AddAgentMemoryFramework(), 265 tests total
- **Phase 4:** Neo4jGraphRagContextSource (IGraphRagContextSource via IRetriever delegation), 4 search modes (Vector/Fulltext/Hybrid/Graph), GraphRagAdapterOptions, InstrumentedMemoryService + InstrumentedGraphRagContextSource (OTel decorators), MemoryActivitySource, MemoryMetrics (12 instruments), DI: AddGraphRagAdapter() + AddAgentMemoryObservability(), 295 tests total
- **Phase 5:** Extraction.AzureLanguage (Azure Text Analytics extractors), Enrichment package (Nominatim geocoding + Wikimedia entity enrichment, decorator chain: Cache → RateLimiter → Nominatim), DI: AddAzureLanguageExtraction() + AddEnrichment(), 349 tests total
- **Phase 6:** MCP Server with 14 tools (6 core + 8 extended), ModelContextProtocol SDK 1.2.0, stdio transport, IGraphQueryService interface + Neo4j implementation, sample McpHost app, Claude Desktop config, DI: AddAgentMemoryMcpTools(), 398 tests total

**All Phases Complete.** The project is a full-featured .NET Neo4j Memory Provider with 10 packages.

---

## 2. Implementation Plan Reference

### Source of Truth

We are following **[Agent-memory-for-dotnet-implementation-plan.md](../Agent-memory-for-dotnet-implementation-plan.md)**, Phases 0–6.

The implementation plan is governed by the **[Agent-Memory-for-DotNet-Specification.md](../Agent-Memory-for-DotNet-Specification.md)**, which is the canonical functional and architectural reference. If any ambiguity exists between the two, the specification takes precedence (Spec §8.4).

### Current Work Maps To

- **Phase 0 (Discovery & Design Lock)** → ✅ Complete — architecture decisions, package boundaries, interface contracts frozen
- **Phase 1 (Core Memory Engine)** → 🔧 In Progress — framework-agnostic memory core + Neo4j persistence
  - Impl Plan §7 (Layered Architecture) — implemented
  - Impl Plan §8 (Domain Model) — implemented as Abstractions records
  - Impl Plan §9 (Neo4j Graph Model) — constraints + indexes implemented, repositories pending
  - Impl Plan §10 (Project Responsibilities) — packages created, services/repos pending
  - Impl Plan §14 (Memory Recall Design) — interface defined, implementation pending
  - Impl Plan §16 (Test Strategy) — harness built, tests pending

### Deviations from Plan

| Deviation | Reason |
|---|---|
| Package naming: `Neo4j.AgentMemory.*` instead of `AgentMemory.*` | Naming decision to clarify Neo4j backing. Spec §2.2 lists these as "candidate packages" so this is within spec. |
| `Extraction.Abstractions` and `Extraction.Llm` not created yet | Deferred to Phase 2 as planned. Extraction interfaces live in main Abstractions package for now. |
| `reasoning_step_embedding_idx` added (not in original plan) | Enables future semantic search over reasoning steps. Useful and low-cost. |
| `task_embedding_idx` for ReasoningTrace not yet created | Gap identified during alignment review — see §6 Known Gaps. |

---

## 3. Phase 1 Epic Status

| # | Epic | Description | Status | Commit | Notes |
|---|---|---|---|---|---|
| 1 | Foundation & Scaffold | Solution structure, projects, Directory.Build.props, docker-compose | ✅ Done | `f0c2922` | 3 projects + 2 test projects + deploy/ |
| 2 | Abstractions Package | Domain records, service interfaces, repository interfaces, options, enums | ✅ Done | `f0c2922` | 15 services, 10 repos, ~29 records, 6 enums |
| 3 | Neo4j Infrastructure | Driver factory, session factory, tx runner, schema bootstrapper, migration runner, DI | ✅ Done | `ade9590` | 9 constraints, 3 fulltext, 5 vector, 9 property indexes |
| 4 | Short-Term Memory Repos | ConversationRepository, MessageRepository (Neo4j Cypher implementations) | ✅ Done | `4a30a0e` | 9 Neo4j repositories implemented |
| 5 | Long-Term Memory Repos | EntityRepository, FactRepository, PreferenceRepository, RelationshipRepository | ✅ Done | `4a30a0e` | 5 Core services with DI wiring |
| 6 | Reasoning Memory Repos | ReasoningTraceRepository, ReasoningStepRepository, ToolCallRepository | ✅ Done | `4a30a0e` | Fact.Category field added |
| 7 | Context Assembly | MemoryContextAssembler, MemoryService facade, recall orchestration | ✅ Done | `4a30a0e` | Full test coverage (85/85 passing) |
| 8 | Stubs | StubEmbeddingProvider, StubExtractionPipeline, Stub*Extractors, StubEntityResolver | ✅ Done | `ade9590` | 9 stubs in Core/Stubs/ |
| 9 | Test Harness | Testcontainers fixture, integration base, test data seeders, mock factory | ✅ Done | `ade9590` | 85 unit tests passing |

---

## 3.1 Phase 2 Epic Status

| # | Epic | Description | Status | Commit | Notes |
|---|---|---|---|---|---|
| 10 | Entity Resolution Chain | ExactMatch → FuzzyMatch → SemanticMatch → CreateNew (4-strategy resolver) | ✅ Done | Phase 2 | FuzzySharp 2.0.2 integration |
| 11 | Entity Validation | EntityValidator, IEntityValidator interface, constraint enforcement | ✅ Done | Phase 2 | Validation rules for entities |
| 12 | LLM Extraction Package | Extraction.Llm with 4 extractors: Entity, Fact, Preference, Relationship | ✅ Done | Phase 2 | Microsoft.Extensions.AI IChatClient |
| 13 | Extraction Pipeline | Real MemoryExtractionPipeline: extract → validate → resolve → embed → persist | ✅ Done | Phase 2 | Full orchestration with all steps |
| 14 | Neo4j Extraction Support | SAME_AS relationships, MENTIONS relationships, entity merging, schema updates | ✅ Done | Phase 2 | Graph relationships for extraction |
| 15 | DI Infrastructure Phase 2 | AddAgentMemoryCore(), AddLlmExtraction() extensions | ✅ Done | Phase 2 | Service registration |

---

## 3.2 Phase 3 Epic Status

| # | Epic | Description | Status | Commit | Notes |
|---|---|---|---|---|---|
| 16 | Neo4j Agent Framework Package | Neo4j.AgentMemory.AgentFramework project | ✅ Done | Phase 3 | Microsoft.Agents.AI.Abstractions 1.1.0 |
| 17 | Context Provider | Neo4jMemoryContextProvider extends AIContextProvider | ✅ Done | Phase 3 | Pre-run context injection |
| 18 | Chat Message Store | Neo4jChatMessageStore MAF-compatible persistence | ✅ Done | Phase 3 | Session/conversation storage |
| 19 | Convenience Facade | Neo4jMicrosoftMemoryFacade (pre-run + post-run orchestration) | ✅ Done | Phase 3 | Simplified integration API |
| 20 | Type Mapping | MafTypeMapper bidirectional ChatMessage ↔ internal Message mapping | ✅ Done | Phase 3 | Type conversion |
| 21 | Memory Tools | MemoryToolFactory with 6 tools: search_memory, remember_preference, remember_fact, recall_preferences, search_knowledge, find_similar_tasks | ✅ Done | Phase 3 | Agent tool definitions |
| 22 | Trace Recorder | AgentTraceRecorder captures reasoning traces from agent activity | ✅ Done | Phase 3 | Execution trace storage |
| 23 | DI Infrastructure Phase 3 | AddAgentMemoryFramework() extension | ✅ Done | Phase 3 | Service registration |

---

## 3.3 Phase 4 Epic Status

| # | Epic | Description | Status | Commit | Notes |
|---|---|---|---|---|---|
| 24 | GraphRAG Adapter Package | Neo4j.AgentMemory.GraphRagAdapter project | ✅ Done | Phase 4 | Neo4j.AgentFramework.GraphRAG delegation |
| 25 | IGraphRagContextSource | Neo4jGraphRagContextSource with 4 search modes | ✅ Done | Phase 4 | Vector, Fulltext, Hybrid, Graph |
| 26 | GraphRagAdapterOptions | IndexName, SearchMode, FulltextIndexName, TopK, FilterStopWords | ✅ Done | Phase 4 | Full configuration surface |
| 27 | Observability Package | Neo4j.AgentMemory.Observability project | ✅ Done | Phase 4 | OpenTelemetry.Api 1.12.0 |
| 28 | OTel Decorators | InstrumentedMemoryService + InstrumentedGraphRagContextSource | ✅ Done | Phase 4 | Decorator pattern, no Scrutor |
| 29 | MemoryActivitySource | ActivitySource "Neo4j.AgentMemory" for distributed tracing | ✅ Done | Phase 4 | All memory + GraphRAG spans |
| 30 | MemoryMetrics | Meter with 7 counters + 5 histograms | ✅ Done | Phase 4 | messages, entities, graphrag, recall/persist durations |
| 31 | DI Infrastructure Phase 4 | AddGraphRagAdapter() + AddAgentMemoryObservability() | ✅ Done | Phase 4 | Registration order enforced |
| 32 | BlendedAgent Sample | Combined Memory + GraphRAG sample app with OTel console output | ✅ Done | Phase 4 | 3 retrieval modes demonstrated |

## 3.4 Phase 5 Epic Status

| # | Epic | Description | Status | Commit | Notes |
|---|---|---|---|---|---|
| 33 | Azure Language Extraction Package | Neo4j.AgentMemory.Extraction.AzureLanguage project | ✅ Done | Phase 5 | Azure.AI.TextAnalytics integration |
| 34 | Azure Extractors | Entity, Key Phrase, PII extractors using Azure Text Analytics | ✅ Done | Phase 5 | IEntityExtractor, IFactExtractor implementations |
| 35 | Enrichment Package | Neo4j.AgentMemory.Enrichment project | ✅ Done | Phase 5 | Geocoding + entity enrichment services |
| 36 | Geocoding Service | NominatimGeocodingService with caching and rate limiting | ✅ Done | Phase 5 | IGeocodingService implementation |
| 37 | Entity Enrichment Service | WikimediaEntityEnrichmentService for augmenting entities | ✅ Done | Phase 5 | IEnrichmentService implementation |
| 38 | Decorator Chain | Cache → RateLimiter → Backend services | ✅ Done | Phase 5 | Cross-cutting enrichment concerns |
| 39 | DI Infrastructure Phase 5 | AddAzureLanguageExtraction() + AddEnrichment() extensions | ✅ Done | Phase 5 | Service registration |

---

## 3.5 Phase 6 Epic Status

| # | Epic | Description | Status | Commit | Notes |
|---|---|---|---|---|---|
| 40 | IGraphQueryService Interface | New interface in Abstractions for raw Cypher query execution | ✅ Done | Phase 6 | Clean architecture boundary |
| 41 | Neo4jGraphQueryService | IGraphQueryService implementation using INeo4jTransactionRunner | ✅ Done | Phase 6 | Node/Relationship/Path conversion |
| 42 | MCP Server Package | Neo4j.AgentMemory.McpServer project with ModelContextProtocol 1.2.0 | ✅ Done | Phase 6 | 10th src package |
| 43 | Core MCP Tools (6) | memory_search, memory_get_context, memory_store_message, memory_add_entity, memory_add_preference, memory_add_fact | ✅ Done | Phase 6 | Delegates to IMemoryService, ILongTermMemoryService |
| 44 | Extended MCP Tools (8) | memory_get_conversation, memory_list_sessions, memory_get_entity, memory_create_relationship, memory_start_trace, memory_record_step, memory_complete_trace, graph_query | ✅ Done | Phase 6 | Delegates to all service layers |
| 45 | MCP Server DI | AddAgentMemoryMcpTools() extension on IMcpServerBuilder | ✅ Done | Phase 6 | Registers all 5 tool classes |
| 46 | Sample McpHost App | Console app with stdio transport, Claude Desktop config | ✅ Done | Phase 6 | Ready for Claude Desktop integration |
| 47 | MCP Contract Tests | 49 unit tests covering all 14 tools + options defaults | ✅ Done | Phase 6 | 398 total tests |

---

### Completed Epics

#### Epic 1 — Foundation & Scaffold (commit `f0c2922`)
**Delivered:**
- Solution file (`Neo4j.AgentMemory.slnx`)
- `Directory.Build.props` with shared build settings (.NET 9, nullable, implicit usings)
- Three source projects: Abstractions, Core, Neo4j
- Two test projects: Unit, Integration
- `deploy/docker-compose.dev.yml` for local Neo4j
- `LICENSE` (Apache 2.0)

#### Epic 2 — Abstractions Package (commit `f0c2922`)
**Delivered:**
- **Domain records (26 files):** Conversation, Message, SessionInfo, Entity, Fact, Preference, Relationship, ReasoningTrace, ReasoningStep, ToolCall, MemoryContext, MemoryContextSection, RecallRequest, RecallResult, ExtractedEntity, ExtractedFact, ExtractedPreference, ExtractedRelationship, ExtractionRequest, ExtractionResult, GraphRagContextItem, GraphRagContextRequest, GraphRagContextResult
- **Enums (6):** ToolCallStatus, ExtractionTypes, GraphRagSearchMode, RetrievalBlendMode, SessionStrategy, TruncationStrategy
- **Service interfaces (15):** IMemoryService, IShortTermMemoryService, ILongTermMemoryService, IReasoningMemoryService, IMemoryContextAssembler, IMemoryExtractionPipeline, IEntityExtractor, IFactExtractor, IPreferenceExtractor, IRelationshipExtractor, IEmbeddingProvider, IEntityResolver, IGraphRagContextSource, IClock, IIdGenerator
- **Repository interfaces (10):** IConversationRepository, IMessageRepository, IEntityRepository, IFactRepository, IPreferenceRepository, IRelationshipRepository, IReasoningTraceRepository, IReasoningStepRepository, IToolCallRepository, ISchemaRepository
- **Configuration options (8):** MemoryOptions, ShortTermMemoryOptions, LongTermMemoryOptions, ReasoningMemoryOptions, RecallOptions, ContextBudget, plus enums above
- **Key decision:** Zero external dependencies — .NET 9 BCL only (Decision D6.4)

#### Epic 3 — Neo4j Infrastructure (commit `ade9590`)
**Delivered:**
- `Neo4jDriverFactory` — creates and manages `IDriver` instances
- `Neo4jSessionFactory` — creates read/write sessions with correct access modes
- `Neo4jTransactionRunner` — executes read/write transactions with retry and error handling
- `SchemaBootstrapper` — creates 9 uniqueness constraints, 3 fulltext indexes, 5 vector indexes (configurable dimensions), 9 property indexes
- `MigrationRunner` — runs versioned `.cypher` migration files
- `Neo4jOptions` — connection configuration (URI, credentials, pool size, embedding dimensions)
- `ServiceCollectionExtensions` — DI registration for all infrastructure services
- Infrastructure interfaces: `INeo4jDriverFactory`, `INeo4jSessionFactory`, `INeo4jTransactionRunner`, `ISchemaBootstrapper`, `IMigrationRunner`

#### Epic 8 — Stubs (commit `ade9590`)
**Delivered:**
- `SystemClock : IClock` — real UTC clock implementation
- `GuidIdGenerator : IIdGenerator` — GUID-based ID generation
- `StubEmbeddingProvider : IEmbeddingProvider` — deterministic hash-based vectors (1536 dimensions)
- `StubExtractionPipeline : IMemoryExtractionPipeline` — returns empty ExtractionResult
- `StubEntityExtractor`, `StubFactExtractor`, `StubPreferenceExtractor`, `StubRelationshipExtractor` — return empty lists
- `StubEntityResolver` — creates new entities without deduplication

#### Epic 9 — Test Harness (commit `ade9590`)
**Delivered:**
- `Neo4jTestFixture` — manages Testcontainers Neo4j lifecycle
- `IntegrationTestBase` — base class with container access and cleanup
- `TestDataSeeders` — factory methods for domain test objects
- `MockFactory` — NSubstitute mock creation helpers
- `Neo4jTestCollection` — xUnit collection for shared container
- 85 unit tests across 5 test classes: Domain, Services, Repositories, Stubs, Infrastructure
- Full test coverage for Phase 1 implementations

#### Epic 4 — Short-Term Memory Repositories (commit `4a30a0e`)
**Delivered:**
- `Neo4jConversationRepository : IConversationRepository` — Upsert, GetById, GetBySession, Delete
- `Neo4jMessageRepository : IMessageRepository` — Add, AddBatch, GetById, GetByConversation, GetRecentBySession, SearchByVector, DeleteBySession
- `ShortTermMemoryService : IShortTermMemoryService` — orchestrates conversation + message operations
- Message linking pattern: `FIRST_MESSAGE` + `NEXT_MESSAGE` implemented
- Full unit and integration test coverage

#### Epic 5 — Long-Term Memory Repositories (commit `4a30a0e`)
**Delivered:**
- `Neo4jEntityRepository : IEntityRepository` — Upsert (MERGE pattern), GetById, GetByName (including aliases), SearchByVector, GetByType
- `Neo4jFactRepository : IFactRepository` — Upsert, GetById, GetBySubject, SearchByVector
- `Neo4jPreferenceRepository : IPreferenceRepository` — Upsert, GetById, GetByCategory, SearchByVector
- `Neo4jRelationshipRepository : IRelationshipRepository` — Upsert, GetById, GetByEntity, GetBySource, GetByTarget
- `LongTermMemoryService : ILongTermMemoryService` — orchestrates all long-term operations
- Metadata JSON serialization implemented
- Full unit and integration test coverage

#### Epic 6 — Reasoning Memory Repositories (commit `4a30a0e`)
**Delivered:**
- `Neo4jReasoningTraceRepository : IReasoningTraceRepository` — Add, Update, GetById, ListBySession, SearchByTaskVector
- `Neo4jReasoningStepRepository : IReasoningStepRepository` — Add, GetByTrace, GetById, with `HAS_STEP` relationship
- `Neo4jToolCallRepository : IToolCallRepository` — Add, Update, GetByStep, GetById, with `USED_TOOL` relationship
- `ReasoningMemoryService : IReasoningMemoryService` — orchestrates trace lifecycle
- Full unit and integration test coverage

#### Epic 7 — Context Assembly (commit `4a30a0e`)
**Delivered:**
- `MemoryContextAssembler : IMemoryContextAssembler` — assembles context from all memory layers
- `MemoryService : IMemoryService` — facade coordinating recall, extraction, and storage
- Recall orchestration with multi-layer search
- Full unit and integration test coverage

### Pending Epics

#### Epic 4 — Short-Term Memory Repositories (COMPLETE)

### Pending Epics

**None** — All Phase 1 epics are complete.

---

## 5. Document Inventory

| Path | Purpose | Last Updated | Aligned with Spec? |
|---|---|---|---|
| `Agent-Memory-for-DotNet-Specification.md` | Canonical specification — source of truth | 2026-04-13 | **N/A** (this IS the spec) |
| `Agent-memory-for-dotnet-implementation-plan.md` | Execution guide — phased build order, deliverables | 2026-04-13 | ✅ Yes (updated for Phase 1–3 completion) |
| `docs/architecture.md` | Architecture overview — packages, graph model, boundaries, test strategy | 2026-04-13 | ✅ Yes |
| `docs/design.md` | Software design — domain model, context assembly, extraction pipeline, service catalog | 2026-04-13 | ✅ Yes |
| `docs/neo4j-maf-provider-analysis.md` | Reuse strategy for existing Neo4j GraphRAG provider | 2026-04-13 | ✅ Yes |
| `docs/python-agent-memory-analysis.md` | Reference analysis mapping Python agent-memory to .NET | 2026-04-13 | ✅ Yes |
| `docs/implementation-status.md` | **This document** — status tracker | 2026-04-13 | ✅ Yes |
| `.squad/decisions.md` | Team decisions log (all decisions merged, deduped) | 2026-04-13 | ✅ Yes |

### 5.1 Document Alignment

All documents are current as of 2026-04-13. Phase 1–3 completion has been reflected across all documentation.

---

## 6. Known Gaps & Future Phases

### Phase 2 — Entity Resolution & Extraction Pipeline

| Gap | Scope | Status |
|---|---|---|
| Real entity resolution | Implement 4-strategy chain (exact → fuzzy → semantic → type-aware) | Deferred to Phase 2 |
| LLM extraction pipeline | Prompt templates, model integration, parsing | Deferred to Phase 2 |
| Advanced recall patterns | GraphRAG integration, multi-hop reasoning | Deferred to Phase 3 |

### Completed Phase 1 Features

| Feature | Epic | Commit |
|---|---|---|
| Neo4j repository infrastructure | 3 | `ade9590` |
| Message linking pattern (FIRST_MESSAGE + NEXT_MESSAGE) | 4 | `4a30a0e` |
| Metadata JSON serialization | 5 | `4a30a0e` |
| Cross-memory relationships | 6 | `4a30a0e` |
| Full context assembly & orchestration | 7 | `4a30a0e` |
| 85 unit tests with 100% pass rate | 9 | `4a30a0e` |
| 7 | Centralized Cypher queries | Low | 1 | Python centralizes all Cypher in `queries.py`. Our queries will be inline in repositories for now. Consider centralizing if maintenance becomes an issue. |
| 8 | `SessionInfo` missing previews | Low | Future | Python's `list_sessions` returns first/last message previews. Our `SessionInfo` doesn't include these. |

### Schema Gap Detail

The `SchemaBootstrapper` currently creates 5 vector indexes:
1. `message_embedding_idx` (Message.embedding) ✅
2. `entity_embedding_idx` (Entity.embedding) ✅
3. `preference_embedding_idx` (Preference.embedding) ✅
4. `fact_embedding_idx` (Fact.embedding) ✅
5. `reasoning_step_embedding_idx` (ReasoningStep.embedding) ✅

**Missing:** `task_embedding_idx` on `ReasoningTrace.taskEmbedding` — required by `IReasoningTraceRepository.SearchByTaskVectorAsync`. This index should be added to the SchemaBootstrapper before or during Epic 6.

---

## 7. Phase Roadmap

| Phase | Name | Objective | Status | Key Deliverables |
|---|---|---|---|---|
| **0** | Discovery & Design Lock | Freeze architecture, interfaces, graph schema | ✅ Complete | Spec, impl plan, decisions D1–D6, Squad team |
| **1** | Core Memory Engine | Framework-agnostic memory core + Neo4j persistence | ✅ **Complete** | Abstractions, Core, Neo4j packages; all repositories + services; context assembler |
| **2** | LLM Extraction Pipeline | .NET-native structured extraction using LLMs | ✅ **Complete** | Extraction.Llm; entity resolution (4-strategy chain); vector indexes; 210 unit tests |
| **3** | MAF Adapter | Microsoft Agent Framework integration | ✅ **Complete** | AgentFramework package; context provider, chat store, memory tools, trace recorder; 265 unit tests |
| **4** | GraphRAG + Observability | GraphRAG adapter, blended context, OpenTelemetry | ✅ **Complete** | GraphRagAdapter package; 4 search modes; Observability package; OTel decorators; 295 unit tests |
| **5** | Advanced Extraction & Enrichment | Azure Language extraction, geocoding, entity enrichment | ✅ **Complete** | Extraction.AzureLanguage + Enrichment packages; decorator chain; 349 unit tests |
| **6** | MCP Server | External access via Model Context Protocol | ⏳ Not Started | Mcp package; stdio/HTTP transport; core + extended tool profiles |

### Phase 1 Exit Criteria (from Impl Plan)

- [ ] All repositories implemented with Neo4j persistence
- [ ] All services unit tested
- [ ] All repositories integration tested with real Neo4j via Testcontainers
- [ ] Context assembler functional with configurable budgets
- [x] No MAF or GraphRAG dependencies in Core or Abstractions
- [x] Schema bootstrap creates all constraints and indexes (vector index gap to fix)
- [ ] In-process memory engine works without Agent Framework

---

## 8. Decisions Log Reference

All architectural decisions are recorded in:

- **`.squad/decisions.md`** — Active team decisions (D1–D6)
- **`docs/architecture.md` §5** — Boundary enforcement rules (B1–B8)
- **`docs/design.md` §8** — Design decision rationale table
- **`.squad/agents/deckard/history.md`** — Detailed review findings and reasoning

Key decisions for current work:
- **D1:** Package structure (Abstractions → Core → Neo4j)
- **D2:** Dependency direction (strict inward, no reverse)
- **D3:** Testcontainers for all integration tests
- **D4:** Bootstrap order (Abstractions → Infrastructure → Short-term → Long-term → Reasoning → Assembler)
- **D5:** Stub extraction and embedding in Phase 1
- **D6:** Domain design (records, naming, interfaces, GraphRAG types in Abstractions)

---

## 9. How to Build and Test

### Prerequisites

- .NET 9 SDK
- Docker (for integration tests — Testcontainers auto-provisions Neo4j)

### Build

```bash
dotnet build
```

### Run All Tests

```bash
dotnet test
```

- **Unit tests (21):** Run without Docker. Test stubs, clock, ID generator.
- **Integration tests (2):** Require Docker. Test Neo4j connectivity and basic CRUD via Testcontainers.

### Run Only Unit Tests

```bash
dotnet test tests/Neo4j.AgentMemory.Tests.Unit
```

### Local Neo4j (for manual exploration)

```bash
docker compose -f deploy/docker-compose.dev.yml up -d
```

Connects at `bolt://localhost:7687` with credentials `neo4j/password`.

### Current Test Results

```
Passed!  - Failed: 0, Passed: 349, Skipped: 0 - Neo4j.AgentMemory.Tests.Unit.dll
```

**Test breakdown by phase:**
- Phase 1: 85 unit tests (core memory engine)
- Phase 2: 125 additional tests (extraction pipeline + entity resolution)
- Phase 3: 55 additional tests (MAF adapter + tools + persistence)
- Phase 4: 30 additional tests (GraphRAG adapter + observability)
- Phase 5: 54 additional tests (Azure Language extraction + enrichment)
- **Total: 349 unit tests passing**

---

*This document is the single status reference for the Agent Memory for .NET project. It should be updated whenever epics are completed or project status changes.*
