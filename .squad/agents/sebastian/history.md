# Sebastian — History

## Learnings

### 2025-07-13 — G14: Custom YAML/JSON Schema Support

**Deliverables (all in `Domain/Schema/`):**

1. **`EntityTypeConfig`** — sealed record with Name, Description, Subtypes, Attributes, Color
2. **`RelationTypeConfig`** — sealed record with Name, Description, SourceTypes, TargetTypes, Properties
3. **`EntitySchemaConfig`** — sealed record with POLE+O defaults, `GetEntityTypeNames()`, `GetSubtypes()`, `IsValidType()`, `NormalizeType()`, `GetRelationTypeNames()`
4. **`SchemaModel`** enum — Poleo, Legacy, Custom
5. **`SchemaListItem`** — sealed record for schema listing
6. **`DefaultSchemas`** — static class with `GetPoleoEntityTypes()` (5 types), `GetPoleoRelationTypes()` (16 relations), `GetLegacyEntityTypes()` (8 types), `LegacyToPoleoMapping` dictionary
7. **`ISchemaManager`** — interface in Services with LoadSchemaAsync, SaveSchemaAsync, ListSchemasAsync, etc.
8. **`SchemaLoader`** — static class in Core.Schema with JSON loading (path + stream overloads), `CreateForTypes()`, `GetDefaultSchema()`, `GetLegacySchema()`

**Tests:** 91 passing tests across 6 test classes:
- `EntityTypeConfigTests`, `RelationTypeConfigTests`, `SchemaListItemTests` — record property defaults and equality
- `DefaultSchemasTests` — POLE+O entity/relation types, legacy mapping
- `EntitySchemaConfigTests` — default schema validation, GetSubtypes, IsValidType, NormalizeType
- `SchemaLoaderTests` — JSON loading (file/stream), CreateForTypes, GetDefaultSchema, GetLegacySchema

**Key decisions:**
- Used `#pragma warning disable CS1591` on all Abstractions types (consistent with SchemaConstants.cs pattern)
- DTOs kept private inside `SchemaLoader` — no leakage of deserialization internals
- YAML intentionally excluded (no third-party deps)
- `SchemaListItem` placed in `Domain/Schema/` (not a separate Services namespace) for clean domain cohesion

**Pre-existing fix:** `TextChunkerTests.cs` used `HaveCountGreaterOrEqualTo` (FluentAssertions 7 API) — replaced with `HaveCountGreaterThanOrEqualTo` (FA8+). This was blocking all test runs.

---

## Project Context
- **Project:** Agent Memory for .NET
- **User:** Jose Luis Latorre Millas
- **Stack:** .NET 9, C#, Neo4j.AgentFramework.GraphRAG
- **Role focus:** GraphRAG adapter — wrap existing provider, blended retrieval
- **Reference:** /Neo4j/neo4j-maf-provider/dotnet/ for existing GraphRAG provider

## Learnings

### 2025-07-12 — neo4j-maf-provider Analysis & Read/Write Split

**What neo4j-maf-provider IS:**
- Entirely read-only. Zero write operations in any class.
- 10 classes: `Neo4jContextProvider`, `Neo4jContextProviderOptions`, `Neo4jSettings`, `IndexType`, `StopWords`, and retrieval layer (`IRetriever`, `VectorRetriever`, `FulltextRetriever`, `HybridRetriever`, `RetrieverResult`/`RetrieverResultItem`).
- Its only function: given query text, embed it, search a Neo4j knowledge graph index (vector/fulltext/hybrid), return ranked string results.
- MAF-coupled only at the top layer (`Neo4jContextProvider` extends `AIContextProvider` from `Microsoft.Agents.AI`). The retrieval layer (`IRetriever` and implementations) has zero MAF dependency — only `Neo4j.Driver` + `Microsoft.Extensions.AI`.

**How we used it (class-by-class):**

| Reference Class | Our Treatment | Reason |
|---|---|---|
| `Neo4jContextProvider` | **Not used** | MAF-specific (`AIContextProvider` base). We have `Neo4jMemoryContextProvider` instead, which delegates to `IMemoryService`. |
| `Neo4jContextProviderOptions` | **Not used** | MAF-coupled config. We have `GraphRagAdapterOptions` with similar fields plus `GraphRagSearchMode.Graph`. |
| `Neo4jSettings` | **Not used** | We use `IOptions<Neo4jOptions>` via DI. |
| `IndexType` | **Not used** | Replaced by `GraphRagSearchMode` enum with an extra `Graph` mode. |
| `StopWords` | **Cloned** → `StopWordFilter` | Identical implementation, different namespace/class name. Decoupled from reference package. |
| `IRetriever` | **Interface referenced** | We implement `IRetriever` in our internal adapters and use `RetrieverResult`/`RetrieverResultItem` from the package. |
| `VectorRetriever` | **Re-implemented** as `AdapterVectorRetriever` | Same Cypher, same patterns. Internal class, no MAF dep. We own the implementation. |
| `FulltextRetriever` | **Re-implemented** as `AdapterFulltextRetriever` | Same. |
| `HybridRetriever` | **Re-implemented** as `AdapterHybridRetriever` | Same concurrent Task.WhenAll + max-score merge pattern. |
| `RetrieverResult` / `RetrieverResultItem` | **Used directly** | Simple record types. No reason to replace. |

Key insight: `Neo4jGraphRagContextSource` references `IRetriever` and `RetrieverResult` from the package for type interop, but all the actual retrieval logic is re-implemented in our `Internal/` adapters. The reference package is a compile-time dependency only for those two types — not for its internal implementation.

**Our write layer — complete:**
- `Neo4jEntityRepository`: `UpsertAsync`, `AddMentionAsync`, `AddMentionsBatchAsync`, `AddSameAsRelationshipAsync`, `MergeEntitiesAsync`
- `Neo4jFactRepository`: `UpsertAsync`
- `Neo4jMessageRepository`: `AddAsync`, `AddBatchAsync`, `DeleteBySessionAsync`
- `Neo4jPreferenceRepository`: `UpsertAsync`
- `Neo4jRelationshipRepository`: `UpsertAsync`
- `Neo4jReasoningTraceRepository` + `Neo4jReasoningStepRepository`: reasoning trace persistence
- `Neo4jConversationRepository`, `Neo4jToolCallRepository`: conversation/tool call persistence
- All use `_tx.WriteAsync(...)` via `INeo4jTransactionRunner` for write isolation

### 2025-07-12 — neo4j-maf-provider Analysis & Read/Write Split

**What neo4j-maf-provider IS:**
- Entirely read-only. Zero write operations in any class.
- 10 classes: `Neo4jContextProvider`, `Neo4jContextProviderOptions`, `Neo4jSettings`, `IndexType`, `StopWords`, and retrieval layer (`IRetriever`, `VectorRetriever`, `FulltextRetriever`, `HybridRetriever`, `RetrieverResult`/`RetrieverResultItem`).
- Its only function: given query text, embed it, search a Neo4j knowledge graph index (vector/fulltext/hybrid), return ranked string results.
- MAF-coupled only at the top layer (`Neo4jContextProvider` extends `AIContextProvider` from `Microsoft.Agents.AI`). The retrieval layer (`IRetriever` and implementations) has zero MAF dependency — only `Neo4j.Driver` + `Microsoft.Extensions.AI`.

**How we used it (class-by-class):**

| Reference Class | Our Treatment | Reason |
|---|---|---|
| `Neo4jContextProvider` | **Not used** | MAF-specific (`AIContextProvider` base). We have `Neo4jMemoryContextProvider` instead, which delegates to `IMemoryService`. |
| `Neo4jContextProviderOptions` | **Not used** | MAF-coupled config. We have `GraphRagAdapterOptions` with similar fields plus `GraphRagSearchMode.Graph`. |
| `Neo4jSettings` | **Not used** | We use `IOptions<Neo4jOptions>` via DI. |
| `IndexType` | **Not used** | Replaced by `GraphRagSearchMode` enum with an extra `Graph` mode. |
| `StopWords` | **Cloned** → `StopWordFilter` | Identical implementation, different namespace/class name. Decoupled from reference package. |
| `IRetriever` | **Interface referenced** | We implement `IRetriever` in our internal adapters and use `RetrieverResult`/`RetrieverResultItem` from the package. |
| `VectorRetriever` | **Re-implemented** as `AdapterVectorRetriever` | Same Cypher, same patterns. Internal class, no MAF dep. We own the implementation. |
| `FulltextRetriever` | **Re-implemented** as `AdapterFulltextRetriever` | Same. |
| `HybridRetriever` | **Re-implemented** as `AdapterHybridRetriever` | Same concurrent Task.WhenAll + max-score merge pattern. |
| `RetrieverResult` / `RetrieverResultItem` | **Used directly** | Simple record types. No reason to replace. |

Key insight: `Neo4jGraphRagContextSource` references `IRetriever` and `RetrieverResult` from the package for type interop, but all the actual retrieval logic is re-implemented in our `Internal/` adapters. The reference package is a compile-time dependency only for those two types — not for its internal implementation.

**Our write layer — complete:**
- `Neo4jEntityRepository`: `UpsertAsync`, `AddMentionAsync`, `AddMentionsBatchAsync`, `AddSameAsRelationshipAsync`, `MergeEntitiesAsync`
- `Neo4jFactRepository`: `UpsertAsync`
- `Neo4jMessageRepository`: `AddAsync`, `AddBatchAsync`, `DeleteBySessionAsync`
- `Neo4jPreferenceRepository`: `UpsertAsync`
- `Neo4jRelationshipRepository`: `UpsertAsync`
- `Neo4jReasoningTraceRepository` + `Neo4jReasoningStepRepository`: reasoning trace persistence
- `Neo4jConversationRepository`, `Neo4jToolCallRepository`: conversation/tool call persistence
- All use `_tx.WriteAsync(...)` via `INeo4jTransactionRunner` for write isolation

**Architecture assessment:**
- Read/write split is clean and intentional.
- The GraphRAG adapter reads from arbitrary user-configured Neo4j indexes (external KGs).
- Our repositories read from and write to our own schema (Entity, Fact, Message, etc.).
- These two read paths are orthogonal and complementary.
- One minor concern: `AdapterVectorRetriever` duplicates `VectorRetriever` almost verbatim. This is acceptable for decoupling, but could diverge in edge cases. Track for future sync.

### 2026-04-13 — Comprehensive neo4j-maf-provider Architecture Review (Orchestration)

**Trigger:** Multi-agent review session assessed Sebastian's analysis for completeness and architectural implications.

**Verified Findings:**
1. **Read-only nature confirmed** — Grep of reference source verified zero write operations across all 10 classes
2. **Type interop minimal** — Only `IRetriever`, `RetrieverResult`, `RetrieverResultItem` used from reference package
3. **Write layer complete** — All 9 repository classes have full CRUD implementations
4. **Boundary clean** — No MAF coupling in retriever layer, no leak of reference implementation details into our code
5. **Duplication tracked** — `AdapterVectorRetriever` mirrors `VectorRetriever` verbatim; acceptable trade-off for decoupling

**Write Layer Gaps Identified:**

| Gap | Severity | Recommendation |
|-----|----------|-----------------|
| No bulk entity upsert (`UpsertBatchAsync`) | MEDIUM | Add to `IEntityRepository` for extraction pipeline performance at scale |
| No bulk fact upsert (`UpsertBatchAsync`) | MEDIUM | Add to `IFactRepository` for consistency |
| No explicit preference deletion | LOW | Add `DeleteAsync()` to support user preference retraction |
| No entity index refresh after merge | LOW | After `MergeEntitiesAsync`, consider re-embedding canonical entity text |

**Recommendations for Team:**
1. **Batch operations** — Priority MEDIUM. Extraction pipelines process tens of entities per message; single-entity upserts will be slow at scale.
2. **Preference lifecycle** — Priority LOW. Agents should support user preference changes. Add `DeleteAsync()` to `IPreferenceRepository`.
3. **Use own schema as KG** — Document that GraphRagAdapter can be configured with `entity_embedding_idx` as `IndexName` to perform graph-enriched retrieval over our own entity schema. Powerful use case.
4. **Retriever sync tracking** — Establish mechanism to track divergence between our adapters and reference implementations. Monthly manual review or automated diff check.

**Architecture Assessment: SOUND ✅**
- Read/write split is well-structured and intentional
- Two read paths (internal memory vs. external KG) are orthogonal and complementary
- Boundary is clean with minimal coupling to reference package
- Full write capability implemented and verified
- Ready for production use with identified improvements tracked

### 2026-07-12 — Full Forensic Trace (Jose Luis Request)

**Full report written to:** `.squad/decisions/inbox/sebastian-maf-provider-trace.md`

**Key forensic findings (confirmed from source):**

#### Q1: Neo4jContextProvider anatomy
- File: `Neo4j/neo4j-maf-provider/dotnet/src/Neo4j.AgentFramework.GraphRAG/Neo4jContextProvider.cs`
- Extends `AIContextProvider` (MAF) + `IAsyncDisposable`
- Fields: `IDriver`, `bool _ownsDriver`, `Neo4jContextProviderOptions`, `IRetriever`
- `ProvideAIContextAsync`: concatenates last N User+Assistant messages → `_retriever.SearchAsync()` → formats to `ChatMessage` list in `AIContext`
- Dual construction: takes existing driver OR factory `Create(uri, user, pass)` with driver ownership

#### Q2: Neo4jMemoryContextProvider vs reference — REIMPLEMENTED
- Same: `AIContextProvider` base class, `ProvideAIContextAsync` hook, `AIContext { Messages }` output
- Different: constructor injects `IMemoryService + IEmbeddingProvider` (not IDriver); searches via `RecallAsync()` (full memory stack) not direct index; extracts session/conversationId; three try/catch layers for graceful degradation; also implements post-run store via `StoreAIContextAsync`
- Classification: REIMPLEMENTED — same MAF pattern, completely different internals

#### Q3: Compile-time dependency confirmed
- **NOT a NuGet package** — `GraphRagAdapter.csproj` has a `<ProjectReference>` to local source tree
- Types used from reference: only `IRetriever`, `RetrieverResult`, `RetrieverResultItem`
- Zero dependency in AgentFramework, Core, Abstractions, Neo4j packages

#### Q4: Class-by-class status
- `IRetriever`, `RetrieverResult`, `RetrieverResultItem` → **REUSED** (compile-time dep, 3 types only)
- `StopWords` → **COPIED** verbatim as `StopWordFilter` (same 107 words, same regex, different namespace/name)
- `VectorRetriever`, `FulltextRetriever`, `HybridRetriever` → **REIMPLEMENTED** (near-verbatim Cypher and logic; internal; use `StopWordFilter` instead of `StopWords`)
- `Neo4jContextProvider`, `Neo4jContextProviderOptions`, `Neo4jSettings` → **IGNORED** entirely
- `IndexType` → **REIMPLEMENTED** as `GraphRagSearchMode` (+ added `Graph` mode)

#### Q5: Write-layer gap analysis (forensic)

**Gap 1 — No UpsertBatchAsync:**
- Pipeline does: `foreach entity → UpsertAsync()` → 2 DB round-trips per entity
- 1000 messages × 10 entities = 20,000 DB trips. Fix: UNWIND batch Cypher. Priority: MEDIUM-HIGH.

**Gap 2 — No DeleteAsync for preferences:**
- Every extraction creates NEW Preference node via GUID (no overwrite). Contradicting preferences accumulate.
- No workaround exists via API. Priority: MEDIUM (correctness bug).

**Gap 3 — No re-embedding after merge:**
- `MergeEntitiesAsync` Cypher never touches `target.embedding`. Alias added to target but embedding not refreshed.
- Embeddings are based on `entity.Name` only. Alias-form queries may miss merged entity.
- Fix: re-embed in `CompositeEntityResolver` after auto-merge, using `name + aliases` text. Priority: LOW-MEDIUM.

### 2025-07-12 — Python vs .NET Comprehensive Comparison

**Full comparison document written to:** `docs/python-dotnet-comparison.md`

**Key findings:**

#### Python project structure
- Located at `Neo4j/agent-memory/src/neo4j_agent_memory/`
- Modules: `core/`, `memory/short_term.py`, `memory/long_term.py`, `memory/reasoning.py`,
  `extraction/`, `resolution/`, `enrichment/`, `graph/`, `schema/`, `config/settings.py`,
  `mcp/`, `observability/`, `integrations/`, `embeddings/`, `services/geocoder.py`, `cli/`
- Entry point: `MemoryClient` context manager + `MemorySettings` (pydantic-settings, `NAM_*` env vars)
- Schema model: POLE+O (`POLEOEntityType`) with subtypes, custom YAML/JSON schema support

#### Extraction gap (major)
- Python has 3 extractor types: LLM, GLiNER2, spaCy. .NET has 2: LLM + Azure Language.
- Python `ExtractionPipeline` chains extractors with 5 merge strategies (UNION, INTERSECTION, CONFIDENCE, CASCADE, FIRST_SUCCESS).
- .NET runs a single extractor per type in `Task.WhenAll` — no multi-extractor merging yet.
- Streaming extraction (`extraction/streaming.py`) for chunked large-doc processing: Python only.

#### MCP surface differences
- Python: 15 tools (core 6 + extended 9) + 4 MCP resources + 3 MCP prompts.
- .NET: 13 tools (core 6 + extended 7) + 0 resources + 0 prompts.
- Python lacks: `memory_record_tool_call`, `memory_find_duplicates`, `extract_and_persist` (these are .NET additions).
- .NET lacks: `memory_get_observations` (token-budget observation compression).

#### Enrichment gap
- Python: `BackgroundEnrichmentQueue` — async, non-blocking, retry, Wikipedia + Diffbot.
- .NET: `WikimediaEnrichmentService` — synchronous, no queue, Wikipedia only.

#### .NET exclusive features
- `Neo4j.AgentMemory.Abstractions` (interface-only package — Python has no equivalent)
- `Neo4jGraphRagContextSource` (GraphRAG adapter for external knowledge graphs)
- `UpsertBatchAsync` on entity and fact repositories
- `DeletePreferenceAsync` (preference retraction — Python API has no delete)
- Granular extractors: `LlmEntityExtractor`, `LlmFactExtractor`, `LlmPreferenceExtractor`, `LlmRelationshipExtractor` as 4 separate classes
- `extract_and_persist`, `memory_record_tool_call`, `memory_find_duplicates` MCP tools

#### Top gaps to consider
1. Multi-stage pipeline with merge strategies (High priority)
2. Fact deduplication (High priority)  
3. Background enrichment queue (Medium priority)
4. MCP resources + prompts (Medium priority)
5. Streaming extraction (Medium priority)

### 2025-07-13 — Feature Record Document (Comprehensive Audit)

**Output:** `docs/feature-record.md`

**Scope:** Full audit of all 10 source packages and 55 test files.

**Key Findings:**
- **20 features** cataloged with value scores 50–95
- **~429 unit tests** across 55 test files — strong unit test coverage
- **2 integration tests** — critical gap; no repository-level integration tests
- **Top 3 critical gaps:** repository integration tests, fact deduplication, multi-extractor merge pipeline
- **Architecture is clean:** Abstractions → Core → Neo4j dependency direction respected. All packages have DI registration. Options pattern used consistently.
- **Highest-value features (95/100):** Short-Term Memory, Long-Term Memory
- **Best-tested features:** MCP Server (59 tests), AgentFramework (58 tests), Services (72 tests), Resolution (33 tests)
- **Weakest-tested areas:** Integration tests (2 total), Configuration options (no dedicated tests), Cross-memory relationships (tested via Cypher string verification only)

### 2025-07-13 — MCP Resources (G6), Observation Tool (G11), POLE+O Entity Types (G15)

**Deliverables:**

1. **MCP Resources (G6)** — 4 new resources in `src/Neo4j.AgentMemory.McpServer/Resources/`:
   - `MemoryStatusResource` (`memory://status`) — entity/fact/preference/conversation/message counts
   - `EntityListResource` (`memory://entities`) — paginated entity list with type filter
   - `ConversationListResource` (`memory://conversations`) — recent conversations with message counts
   - `SchemaInfoResource` (`memory://schema`) — graph schema introspection (labels, rel types, property keys)
   - All use `[McpServerResourceType]` + `[McpServerResource]` pattern from MCP SDK 1.2.0
   - Registered via `AddAgentMemoryMcpResources()` extension method

2. **Observation Tool (G11)** — `memory_get_observations` in `ObservationTools.cs`:
   - Token-budget-aware observation retrieval via `IContextCompressor`
   - Respects include flags (entities, facts, preferences)
   - Returns structured JSON + formatted markdown summary
   - Handles empty sessions gracefully

3. **POLE+O Entity Types (G15)** — `EntityType.cs` in Abstractions:
   - Static class with `Person`, `Object`, `Location`, `Event`, `Organization`, `Unknown` constants
   - `All` collection (5 POLE+O types, excludes Unknown)
   - `IsKnownType()` — case-insensitive recognition
   - `Normalize()` — canonical form normalization

4. **Tests** — 58 new tests (target was 28+):
   - `MemoryResourcesTests.cs` — 16 tests covering all 4 resources
   - `ObservationToolsTests.cs` — 8 tests covering compression, flags, empty sessions
   - `EntityTypeTests.cs` — 34 tests covering constants, All collection, IsKnownType, Normalize

**Key decisions:**
- Resources use `IGraphQueryService` with Cypher queries (same pattern as GraphQueryTools/AdvancedMemoryTools)
- `EntityType.Unknown` excluded from `All` collection since it's not a POLE+O type
- Resources registered separately via `AddAgentMemoryMcpResources()` — not bundled into existing methods to avoid breaking changes
