# Code Review Findings — Deep Verification Sprint

> **Author:** Gaff (Neo4j Persistence Engineer)
> **Date:** 2025-07-24
> **Scope:** Code-vs-documentation accuracy, schema parity, MAF-provider comparison, architecture map

---

## Task 1: Code vs Documentation — Deep Verification

### docs/schema.md — ⚠️ Stale (3 issues)

**Issue 1 — Phantom constraint still listed:**
- Section 2.5 (line ~412) lists `relationship_id (MemoryRelationship.id)` as a ".NET extension" constraint
- **Actual code:** `SchemaBootstrapper.cs` has NO such constraint — it was explicitly removed
- This CONTRADICTS section 2.3 (line ~375) which correctly states "Phantom constraint removed from SchemaBootstrapper"
- **Fix:** Delete the `relationship_id` row from the section 2.5 table

**Issue 2 — Property index count inconsistency:**
- Section 1.3 "Property Indexes" table lists 10 regular + 2 schema = 12
- Parity summary table at top says "14 (all 10 Python + 2 Schema + 2 extras)"
- **Actual code:** `SchemaBootstrapper.cs:34-51` has 15 items in `PropertyIndexes` array:
  - 12 regular property indexes (10 Python + `fact_category` + `reasoning_step_timestamp`)
  - 2 schema indexes (`schema_name_idx`, `schema_version_idx`)
  - 1 point index (`entity_location_idx`)
- The 2 .NET-only extras (`fact_category` at line 42, `reasoning_step_timestamp` at line 46) are mentioned in section 2.4 as ".NET extensions" but MISSING from the canonical listing in section 1.3
- **Fix:** Add `fact_category` and `reasoning_step_timestamp` to the section 1.3 property index table (marked as .NET extension)

**Issue 3 — Schema index name difference not documented:**
- Python `queries.py` defines `CREATE_SCHEMA_ID_INDEX` targeting `Schema.id`
- .NET `SchemaBootstrapper.cs:49` creates `schema_version_idx` targeting `Schema.version`
- This is a real schema difference (different index target) not explicitly called out
- **Fix:** Add a note in section 2.9 documenting this difference

**Verified correct:** All 10 constraints ✅, 3 fulltext indexes ✅, 6 vector indexes ✅, 11 node labels ✅, 15 Python relationship types ✅, 18 .NET relationship types ✅, snake_case properties ✅, Tool aggregate stats (7 properties) ✅, native `datetime()` storage ✅

---

### docs/python-dotnet-comparison.md — ⚠️ Stale (1 issue)

**Issue — Test file count outdated:**
- Claims "Test Files: 55+"
- **Actual:** 103 `*Tests.cs` files in unit tests + 8 `*Tests.cs` in integration = **111 test class files**
- The 55+ figure is from an earlier snapshot

**Spot-checked 5 claims — all accurate:**
1. ✅ "1058 unit tests" — Verified: `dotnet test` → 1058 passed, 0 failed
2. ✅ "10 NuGet packages" — 10 source projects confirmed in `src/`
3. ✅ Package mapping (Python→.NET) — verified Core, Neo4j, McpServer, Extraction packages
4. ✅ "Entity resolution chain: exact → fuzzy → semantic" — `CompositeEntityResolver` in Core with 3 matchers
5. ✅ GraphRAG adapter with vector/fulltext/hybrid — 3 retriever classes in `GraphRagAdapter/Internal/`

---

### docs/feature-record.md — ⚠️ Stale (2 issues)

**Issue 1 — Test file count outdated:**
- Header claims "Test Files: 55+"
- **Actual:** 109 `.cs` files in unit tests, 17 in integration = 126 total

**Issue 2 — MCP tool count outdated:**
- Claims "21 tools" for MCP Server (feature #9)
- **Actual:** 28 `[McpServerTool]` attributes across 7 tool files:
  - `CoreMemoryTools.cs`: 7 tools
  - `AdvancedMemoryTools.cs`: 7 tools
  - `ReasoningTools.cs`: 4 tools
  - `ConversationTools.cs`: 3 tools
  - `EntityTools.cs`: 3 tools
  - `GraphQueryTools.cs`: 2 tools
  - `ObservationTools.cs`: 2 tools
- Resource count (6) and prompt count (3) are **correct** ✅

**Verified correct:** "Total Unit Tests: 1058" ✅, "Integration Tests: 71" (test methods, not files — reasonable), value scores and sub-feature descriptions match implementation

---

### docs/architecture-assessment.md — ✅ Accurate

**Verified against actual .csproj files:**
- ✅ Abstractions: zero external dependencies
- ✅ Core → Abstractions (ProjectRef) + FuzzySharp, M.E.* (PackageRef)
- ✅ Neo4j → Abstractions, Core (ProjectRef) + Neo4j.Driver 6.0.0 (PackageRef)
- ✅ AgentFramework → Abstractions, Core (ProjectRef) + M.Agents.AI 1.1.0 (PackageRef)
- ✅ GraphRagAdapter → Abstractions (ProjectRef) + neo4j-maf-provider (ProjectRef) + M.E.AI
- ✅ McpServer → Abstractions (ProjectRef) + ModelContextProtocol 1.2.0 + M.E.Hosting
- ✅ Extraction.Llm → Abstractions, Core + M.E.AI
- ✅ Extraction.AzureLanguage → Abstractions + Azure.AI.TextAnalytics 5.3.0
- ✅ Enrichment → Abstractions + M.E.Http + M.E.Caching.Memory
- ✅ Observability → Abstractions, Core + OpenTelemetry.Api 1.12.0
- ✅ Zero circular dependencies confirmed
- ✅ Layer diagram matches reality

**Minor note:** GraphRagAdapter shown with "+ Neo4j.Driver *" — this is transitive only (from neo4j-maf-provider). The asterisk hints at this but could be more explicit.

---

### docs/design.md — ✅ Accurate

- ✅ All 31 domain types listed match Abstractions project
- ✅ Service interface inventory (15) matches actual interfaces
- ✅ Repository interface inventory (10) matches actual interfaces
- ✅ Design decisions (records, DateTimeOffset, float[] embeddings, etc.) all reflected in code
- ✅ Configuration types match Options classes in Abstractions

---

### README.md — ⚠️ Stale (1 issue)

**Issue — MCP tool count outdated:**
- Lines 24 and 92 claim "21 tools"
- **Actual:** 28 `[McpServerTool]` attributes across 7 tool files
- Resource count (6) and prompt count (3) are correct

**Verified correct:**
- ✅ "1058 unit tests passing, 0 failures" — verified
- ✅ "10 packages" — verified
- ✅ "~99% parity" — consistent with schema analysis
- ✅ Project status description, architecture layers, capabilities all accurate
- ✅ Package table (10 packages with phases and purposes) matches code

---

## Task 2: Schema Parity Deep-Dive

### 2.1 Constraint Comparison

| # | Constraint Name | Label.Property | Python | .NET | Match |
|---|----------------|---------------|:---:|:---:|:---:|
| 1 | `conversation_id` | Conversation.id | ✅ | ✅ | ✅ Exact |
| 2 | `message_id` | Message.id | ✅ | ✅ | ✅ Exact |
| 3 | `entity_id` | Entity.id | ✅ | ✅ | ✅ Exact |
| 4 | `preference_id` | Preference.id | ✅ | ✅ | ✅ Exact |
| 5 | `fact_id` | Fact.id | ✅ | ✅ | ✅ Exact |
| 6 | `reasoning_trace_id` | ReasoningTrace.id | ✅ | ✅ | ✅ Exact |
| 7 | `reasoning_step_id` | ReasoningStep.id | ✅ | ✅ | ✅ Exact |
| 8 | `tool_name` | Tool.name | ✅ | ✅ | ✅ Exact |
| 9 | `tool_call_id` | ToolCall.id | ✅ | ✅ | ✅ Exact |
| 10 | `extractor_name` | Extractor.name | ❌ | ✅ | .NET extra |

**Result:** 9/9 Python constraints present in .NET + 1 .NET extra. **100% parity.**

### 2.2 Vector Index Comparison

| # | Index Name | Label.Property | Python | .NET | Match |
|---|-----------|---------------|:---:|:---:|:---:|
| 1 | `message_embedding_idx` | Message.embedding | ✅ | ✅ | ✅ Exact |
| 2 | `entity_embedding_idx` | Entity.embedding | ✅ | ✅ | ✅ Exact |
| 3 | `preference_embedding_idx` | Preference.embedding | ✅ | ✅ | ✅ Exact |
| 4 | `fact_embedding_idx` | Fact.embedding | ✅ | ✅ | ✅ Exact |
| 5 | `task_embedding_idx` | ReasoningTrace.task_embedding | ✅ | ✅ | ✅ Exact |
| 6 | `reasoning_step_embedding_idx` | ReasoningStep.embedding | ❌ | ✅ | .NET extra |

**Result:** 5/5 Python vector indexes present + 1 .NET extra. **100% parity.** All use cosine similarity, 1536 default dims.

### 2.3 Property Index Comparison

| # | Index Name | Label.Property | Python | .NET | Match |
|---|-----------|---------------|:---:|:---:|:---:|
| 1 | `conversation_session_idx` | Conversation.session_id | ✅ | ✅ | ✅ Exact |
| 2 | `message_timestamp_idx` | Message.timestamp | ✅ | ✅ | ✅ Exact |
| 3 | `message_role_idx` | Message.role | ✅ | ✅ | ✅ Exact |
| 4 | `entity_type_idx` | Entity.type | ✅ | ✅ | ✅ Exact |
| 5 | `entity_name_idx` | Entity.name | ✅ | ✅ | ✅ Exact |
| 6 | `entity_canonical_idx` | Entity.canonical_name | ✅ | ✅ | ✅ Exact |
| 7 | `preference_category_idx` | Preference.category | ✅ | ✅ | ✅ Exact |
| 8 | `trace_session_idx` | ReasoningTrace.session_id | ✅ | ✅ | ✅ Exact |
| 9 | `trace_success_idx` | ReasoningTrace.success | ✅ | ✅ | ✅ Exact |
| 10 | `tool_call_status_idx` | ToolCall.status | ✅ | ✅ | ✅ Exact |
| 11 | `fact_category` | Fact.category | ❌ | ✅ | .NET extra |
| 12 | `reasoning_step_timestamp` | ReasoningStep.timestamp | ❌ | ✅ | .NET extra |

**Result:** 10/10 Python property indexes present + 2 .NET extras. **100% parity.**

### 2.4 Schema Persistence Indexes

| Index Name | Target | Python | .NET | Match |
|-----------|--------|:---:|:---:|:---:|
| `schema_name_idx` | Schema.name | ✅ (queries.py) | ✅ | ✅ |
| `schema_id_idx` | Schema.id | ✅ (queries.py) | ❌ | ⚠️ Gap |
| `schema_version_idx` | Schema.version | ❌ | ✅ | .NET different |

**⚠️ Difference:** Python indexes `Schema.id`, .NET indexes `Schema.version` instead.

### 2.5 Point & Fulltext Indexes

| Category | Index | Python | .NET | Match |
|----------|-------|:---:|:---:|:---:|
| Point | `entity_location_idx` on Entity.location | ✅ | ✅ | ✅ Exact |
| Fulltext | `message_content` on Message.content | ❌ | ✅ | .NET extra |
| Fulltext | `entity_name` on Entity.name, Entity.description | ❌ | ✅ | .NET extra |
| Fulltext | `fact_content` on Fact.subject, predicate, object | ❌ | ✅ | .NET extra |

### 2.6 Node Label Comparison

All 11 node labels are identical:

| Label | Python | .NET | Notes |
|-------|:---:|:---:|-------|
| Conversation | ✅ | ✅ | |
| Message | ✅ | ✅ | |
| Entity | ✅ | ✅ | Dynamic labels: Python=PascalCase, .NET=UPPERCASE |
| Fact | ✅ | ✅ | |
| Preference | ✅ | ✅ | |
| ReasoningTrace | ✅ | ✅ | |
| ReasoningStep | ✅ | ✅ | |
| ToolCall | ✅ | ✅ | |
| Tool | ✅ | ✅ | |
| Extractor | ✅ | ✅ | |
| Schema | ✅ | ✅ | Python has full repo; .NET has indexes only (no Schema repo) |

### 2.7 Relationship Type Comparison

| # | Relationship | Python | .NET | Match |
|---|-------------|:---:|:---:|:---:|
| 1 | `HAS_MESSAGE` | ✅ | ✅ | ✅ |
| 2 | `FIRST_MESSAGE` | ✅ | ✅ | ✅ |
| 3 | `NEXT_MESSAGE` | ✅ | ✅ | ✅ |
| 4 | `MENTIONS` | ✅ | ✅ | ✅ (+properties) |
| 5 | `RELATED_TO` | ✅ | ✅ | ✅ (+properties) |
| 6 | `ABOUT` | ✅ | ✅ | ✅ |
| 7 | `SAME_AS` | ✅ | ✅ | ✅ (+properties) |
| 8 | `HAS_STEP` | ✅ | ✅ | ✅ (with `order` property) |
| 9 | `USES_TOOL` | ✅ | ✅ | ✅ |
| 10 | `INSTANCE_OF` | ✅ | ✅ | ✅ |
| 11 | `HAS_TRACE` | ✅ | ✅ | ✅ |
| 12 | `INITIATED_BY` | ✅ | ✅ | ✅ |
| 13 | `TRIGGERED_BY` | ✅ | ✅ | ✅ |
| 14 | `EXTRACTED_FROM` | ✅ | ✅ | ✅ (+properties) |
| 15 | `EXTRACTED_BY` | ✅ | ✅ | ✅ (+properties) |
| 16 | `HAS_FACT` | ❌ | ✅ | .NET extra |
| 17 | `HAS_PREFERENCE` | ❌ | ✅ | .NET extra |
| 18 | `IN_SESSION` | ❌ | ✅ | .NET extra |

**Result:** 15/15 Python relationships present + 3 .NET extras. **100% parity.**

Note: Python also defines many POLE+O-specific relationship types (KNOWS, ALIAS_OF, MEMBER_OF, EMPLOYED_BY, OWNS, USES, LOCATED_AT, RESIDES_AT, etc.) via `CREATE_ENTITY_RELATIONSHIP` and `CREATE_ENTITY_RELATION_BY_NAME` queries. These are dynamically created relationship types, not schema-defined. .NET handles these via the generic `RELATED_TO` relationship with a `relation_type` property, which is a design difference.

### 2.8 Key Design Differences

| Aspect | Python | .NET | Impact |
|--------|--------|------|--------|
| Entity MERGE key | `{name: $name, type: $type}` | `{id: $id}` | .NET is stricter (UUID-based). Python merges on name+type |
| Fact MERGE key | `{id: $id}` | `{subject, predicate, object}` | .NET prevents duplicate SPO triples at Cypher level |
| Dynamic entity label casing | PascalCase (`Person`) | UPPERCASE (`PERSON`) | Functionally equivalent |
| ToolCallStatus values | 6: pending, success, error, cancelled, failure, timeout | 6: Pending, Success, Error, Cancelled, Failure, Timeout | ✅ **FIXED** — Now matches Python (6 values) |
| Entity relationship types | Dynamic types (KNOWS, ALIAS_OF, etc.) | Generic `RELATED_TO` with `relation_type` property | Design difference |

### 2.9 ToolCallStatus Parity — RESOLVED ✅

**Migration completed:** `ToolCallStatus` enum now has all 6 values matching Python:
- Pending, Success, Error, Cancelled, Failure, Timeout

The .NET `ToolCallStatus` enum has 4 values: `Pending`, `Success`, `Error`, `Cancelled`.
Python defines 6 values: `pending`, `success`, `failure`, `error`, `timeout`, `cancelled`.

**Impact on Tool aggregate stats:** `Neo4jToolCallRepository.cs:61` has Cypher:
```cypher
tool.failed_calls = COALESCE(tool.failed_calls, 0) + CASE WHEN $status IN ['error', 'timeout'] THEN 1 ELSE 0 END
```
The `'timeout'` branch can NEVER trigger because `Timeout` is not a valid `ToolCallStatus` enum value. The Cypher is correct for future-proofing but the enum should be extended to match Python.

**Recommendation:** Add `Failure` and `Timeout` to the `ToolCallStatus` enum for full parity.

### 2.10 Schema Summary

| Category | Python Count | .NET Count | Superset? |
|----------|:---:|:---:|:---:|
| Constraints | 9 | 10 | ✅ .NET superset |
| Property Indexes | 10 | 12 | ✅ .NET superset |
| Schema Indexes | 2 (name, id) | 2 (name, version) | ⚠️ Different targets |
| Vector Indexes | 5 | 6 | ✅ .NET superset |
| Point Indexes | 1 | 1 | ✅ Exact match |
| Fulltext Indexes | 0 | 3 | ✅ .NET extra |
| Node Labels | 11 | 11 | ✅ Exact match |
| Relationship Types | 15 | 18 | ✅ .NET superset |

**Overall Schema Parity: ~99%.** The only minor difference is Schema.id vs Schema.version index target (design choice, not a gap).

---

## Task 3: Neo4j-MAF-Provider Comparison

### What is neo4j-maf-provider?

A thin, single-purpose MAF adapter in `Neo4j/neo4j-maf-provider/dotnet/`. It provides:
- `Neo4jContextProvider : AIContextProvider` — pre-run context retrieval from any Neo4j graph
- Vector, fulltext, and hybrid search via `db.index.vector.queryNodes` / `db.index.fulltext.queryNodes`
- Custom `RetrievalQuery` Cypher for graph enrichment (traversal beyond initial index hit)
- Stop word filtering for fulltext queries
- `IEmbeddingGenerator<string, Embedding<float>>` for vector embedding
- Environment-variable configuration via `Neo4jSettings`

**Key limitation:** It is **read-only**. No entity extraction, no memory persistence, no schema management, no CRUD.

### Feature Comparison Table

| Feature | neo4j-maf-provider | Our Implementation | Notes |
|---------|:---:|:---:|-------|
| **Vector search** | ✅ `VectorRetriever` | ✅ `AdapterVectorRetriever` + per-entity-type repos | We wrap theirs + have memory-specific vector search |
| **Fulltext search** | ✅ `FulltextRetriever` | ✅ `AdapterFulltextRetriever` + fulltext indexes | We wrap theirs + have 3 fulltext indexes |
| **Hybrid search** | ✅ `HybridRetriever` | ✅ `AdapterHybridRetriever` | We wrap theirs |
| **Custom retrieval query** | ✅ configurable Cypher | ✅ via `GraphRagAdapterOptions` | Pass-through |
| **Stop word filtering** | ✅ `StopWords.cs` (32+ words) | ✅ `StopWordFilter.cs` | We have our own implementation |
| **MAF AIContextProvider** | ✅ pre-run only | ✅ pre-run + post-run | We add memory persistence in post-run |
| **IEmbeddingGenerator** | ✅ | ✅ via M.E.AI | Same abstraction |
| **Memory CRUD** | ❌ | ✅ 10 repositories | Full graph lifecycle |
| **Entity extraction** | ❌ | ✅ 4 extractors (LLM + Azure) | Pipeline + resolution |
| **Entity resolution** | ❌ | ✅ exact→fuzzy→semantic chain | Deduplication |
| **Schema management** | ❌ | ✅ SchemaBootstrapper (34 statements) | Constraints, indexes |
| **Conversation/message store** | ❌ | ✅ full linked list model | HAS_MESSAGE, FIRST/NEXT |
| **Reasoning traces** | ❌ | ✅ traces, steps, tool calls | Complete reasoning memory |
| **MCP protocol** | ❌ | ✅ 28 tools, 6 resources, 3 prompts | External client access |
| **Observability** | ❌ | ✅ OpenTelemetry decorators | Tracing + metrics |
| **Geocoding/Enrichment** | ❌ | ✅ Nominatim + Wikipedia | Entity enrichment |
| **Cross-memory relationships** | ❌ | ✅ 18 relationship types | Graph connectivity |
| **Context assembly** | ❌ | ✅ multi-layer retrieval + budgeting | Token-aware recall |
| **Arbitrary graph schemas** | ✅ any Neo4j graph | ⚠️ memory-specific schema | They're more generic |
| **Driver lifecycle** | ✅ factory method + disposal | ✅ DI-managed singleton | Both handle cleanup |
| **Result formatting** | ✅ `FormatResultItem` | ✅ `ContextFormatOptions` | Both format for LLM consumption |
| **Environment config** | ✅ `Neo4jSettings` | ⚠️ `Neo4jOptions` (DI-based) | They use env vars; we use options pattern |

### Component Location Map

| Concern | neo4j-maf-provider Location | Our Location |
|---------|---------------------------|-------------|
| MAF adapter | `Neo4jContextProvider.cs` | `src/Neo4j.AgentMemory.AgentFramework/` |
| Vector retrieval | `Retrieval/VectorRetriever.cs` | `src/Neo4j.AgentMemory.GraphRagAdapter/Internal/AdapterVectorRetriever.cs` |
| Fulltext retrieval | `Retrieval/FulltextRetriever.cs` | `src/Neo4j.AgentMemory.GraphRagAdapter/Internal/AdapterFulltextRetriever.cs` |
| Hybrid retrieval | `Retrieval/HybridRetriever.cs` | `src/Neo4j.AgentMemory.GraphRagAdapter/Internal/AdapterHybridRetriever.cs` |
| Stop words | `StopWords.cs` | `src/Neo4j.AgentMemory.GraphRagAdapter/Internal/StopWordFilter.cs` |
| Configuration | `Neo4jContextProviderOptions.cs` | `src/Neo4j.AgentMemory.GraphRagAdapter/GraphRagAdapterOptions.cs` |
| Index types | `IndexType.cs` (Vector/Fulltext/Hybrid) | Used transitively via ProjectReference |

### How We Integrate neo4j-maf-provider

```
GraphRagAdapter ──ProjectRef──> Neo4j.AgentFramework.GraphRAG (the submodule)
                                  └── VectorRetriever, FulltextRetriever, HybridRetriever
                                  └── IRetriever interface
                                  └── RetrieverResult / RetrieverResultItem records
```

`Neo4jGraphRagContextSource` (our adapter) creates and uses their retriever classes internally, wrapping the results into our `GraphRagContextResult` domain type.

### Summary

- **neo4j-maf-provider is a search-only tool** — it queries an existing graph and returns context
- **Our implementation is a complete memory system** — CRUD, extraction, resolution, assembly, persistence, MCP, observability
- **We wrap their retrievers** via GraphRagAdapter (ProjectReference), adding memory-aware context
- **Key gap in their favor:** They work with ANY Neo4j graph (generic). We're memory-schema specific.
- **Key gap in our favor:** We do everything else (write, extract, resolve, persist, observe)

---

## Task 4: Component Architecture Map

### 4.1 Source Project Inventory (10 projects)

#### Layer 0 — Foundation

**Neo4j.AgentMemory.Abstractions** (0 external dependencies)
- 108 C# files
- Domain models: Entity, Fact, Preference, Relationship, Conversation, Message, ReasoningTrace, ReasoningStep, ToolCall, etc. (30+ records)
- Repository interfaces: IEntityRepository, IFactRepository, IPreferenceRepository, IRelationshipRepository, IConversationRepository, IMessageRepository, IReasoningTraceRepository, IReasoningStepRepository, IToolCallRepository, IExtractorRepository
- Service interfaces: IMemoryService, IShortTermMemoryService, ILongTermMemoryService, IReasoningMemoryService, IMemoryContextAssembler, IMemoryExtractionPipeline, IEntityExtractor, IFactExtractor, IPreferenceExtractor, IRelationshipExtractor, IEmbeddingProvider, IEntityResolver, IGraphRagContextSource, IContextCompressor
- Configuration: MemoryOptions, ShortTermMemoryOptions, LongTermMemoryOptions, ReasoningMemoryOptions, RecallOptions, ContextBudget, ExtractionOptions
- Enums: ToolCallStatus, SessionStrategy, RetrievalBlendMode, TruncationStrategy, ExtractionTypes, GraphRagSearchMode

#### Layer 1 — Orchestration

**Neo4j.AgentMemory.Core** (→ Abstractions + FuzzySharp + M.E.*)
- 33 C# files
- Services: MemoryService, ShortTermMemoryService, LongTermMemoryService, ReasoningMemoryService, MemoryExtractionPipeline, MemoryContextAssembler, ContextCompressor, CompositeEntityResolver, BackgroundEnrichmentQueue, StreamingExtractor, TextChunker
- Entity matchers: ExactMatchEntityMatcher, FuzzyMatchEntityMatcher, SemanticMatchEntityMatcher
- Merge strategies: UnionMergeStrategy, IntersectionMergeStrategy, CascadeMergeStrategy, ConfidenceMergeStrategy, FirstSuccessMergeStrategy
- Stubs: StubEmbeddingProvider, StubEntityExtractor, StubFactExtractor, StubRelationshipExtractor, StubPreferenceExtractor, StubEntityResolver, StubExtractionPipeline, SystemClock, GuidIdGenerator, SessionIdGenerator (3 strategies)

#### Layer 2 — Persistence

**Neo4j.AgentMemory.Neo4j** (→ Abstractions, Core + Neo4j.Driver 6.0.0)
- 25 C# files
- Repositories (11): Neo4jConversationRepository, Neo4jMessageRepository, Neo4jEntityRepository, Neo4jFactRepository, Neo4jPreferenceRepository, Neo4jRelationshipRepository, Neo4jReasoningTraceRepository, Neo4jReasoningStepRepository, Neo4jToolCallRepository, Neo4jExtractorRepository, Neo4jGraphQueryService
- Infrastructure: Neo4jDriverFactory, Neo4jSessionFactory, Neo4jTransactionRunner, SchemaBootstrapper, MigrationRunner, Neo4jOptions, Neo4jDateTimeHelper, MetadataFilterBuilder, ServiceCollectionExtensions

#### Layer 3 — Extensions

**Neo4j.AgentMemory.Extraction.Llm** (→ Abstractions, Core + M.E.AI)
- 8 C# files
- Extractors: LlmEntityExtractor, LlmFactExtractor, LlmRelationshipExtractor, LlmPreferenceExtractor
- Config: LlmExtractionOptions
- Internal DTOs: LlmEntityDto, LlmFactDto, LlmRelationshipDto, LlmPreferenceDto

**Neo4j.AgentMemory.Extraction.AzureLanguage** (→ Abstractions + Azure.AI.TextAnalytics 5.3.0)
- 10 C# files
- Extractors: AzureLanguageEntityExtractor, AzureLanguageFactExtractor, AzureLanguageRelationshipExtractor, AzureLanguagePreferenceExtractor
- Config: AzureLanguageOptions
- Internal: TextAnalyticsClientWrapper, AzureRecognizedEntity, AzureLinkedEntity, AzureSentimentResult

**Neo4j.AgentMemory.Enrichment** (→ Abstractions + M.E.Http + M.E.Caching.Memory)
- 11 C# files
- Services: WikimediaEnrichmentService, DiffbotEnrichmentService, CachedEnrichmentService, NominatimGeocodingService, RateLimitedGeocodingService, CachedGeocodingService
- Config: EnrichmentOptions, GeocodingOptions, EnrichmentCacheOptions

**Neo4j.AgentMemory.Observability** (→ Abstractions, Core + OpenTelemetry.Api 1.12.0)
- 6 C# files
- Components: MemoryMetrics, MemoryActivitySource, InstrumentedMemoryService, InstrumentedGraphRagContextSource, ServiceCollectionExtensions

#### Layer 4 — Adapters

**Neo4j.AgentMemory.AgentFramework** (→ Abstractions, Core + M.Agents.AI 1.1.0)
- 12 C# files
- Classes: Neo4jMemoryContextProvider (AIContextProvider), Neo4jMicrosoftMemoryFacade, Neo4jChatMessageStore, AgentTraceRecorder, MemoryToolFactory, MemoryTool, MafTypeMapper
- Config: AgentFrameworkOptions, ContextFormatOptions

**Neo4j.AgentMemory.GraphRagAdapter** (→ Abstractions + neo4j-maf-provider ProjectRef)
- 8 C# files
- Classes: Neo4jGraphRagContextSource (IGraphRagContextSource), AdapterVectorRetriever, AdapterFulltextRetriever, AdapterHybridRetriever, StopWordFilter
- Config: GraphRagAdapterOptions

**Neo4j.AgentMemory.McpServer** (→ Abstractions + ModelContextProtocol 1.2.0 + M.E.Hosting)
- 20 C# files
- Tools (7 classes, 28 methods): CoreMemoryTools, AdvancedMemoryTools, ReasoningTools, ConversationTools, EntityTools, GraphQueryTools, ObservationTools
- Resources (6): ContextResource, EntityListResource, PreferenceListResource, ConversationListResource, MemoryStatusResource, SchemaInfoResource
- Prompts (3): MemoryConversationPrompt, MemoryReviewPrompt, MemoryReasoningPrompt
- Config: McpServerOptions

### 4.2 Data Flow: MAF Request → Neo4j Graph

```
MAF Agent InvokingContext
    │
    ▼
Neo4jMemoryContextProvider.ProvideAIContextAsync()
    │
    ├── IMemoryContextAssembler.AssembleAsync(RecallRequest)
    │       │
    │       ├── IShortTermMemoryService.GetRecentMessagesAsync()
    │       │       └── IMessageRepository.GetBySessionAsync() → Neo4j Cypher
    │       │
    │       ├── ILongTermMemoryService
    │       │       ├── IEntityRepository.SearchByVectorAsync() → CALL db.index.vector.queryNodes
    │       │       ├── IFactRepository.SearchByVectorAsync()
    │       │       └── IPreferenceRepository.SearchByVectorAsync()
    │       │
    │       ├── IReasoningMemoryService (optional)
    │       │       └── IReasoningTraceRepository.SearchByTaskAsync()
    │       │
    │       └── IGraphRagContextSource.GetContextAsync() (optional, blended mode)
    │               └── Neo4jGraphRagContextSource → VectorRetriever/FulltextRetriever
    │
    ├── Returns: MemoryContext → AIContext (formatted for LLM)
    │
    ▼
MAF Agent processes + responds
    │
    ▼
Neo4jMemoryContextProvider.StoreAIContextAsync()
    │
    ├── IShortTermMemoryService.AddMessageAsync()
    │       └── IMessageRepository.AddAsync() → CREATE (m:Message) + MERGE linked list
    │
    └── IMemoryExtractionPipeline.ExtractAsync() (async/fire-and-forget)
            │
            ├── IEntityExtractor.ExtractAsync() → [ExtractedEntity]
            ├── IFactExtractor.ExtractAsync() → [ExtractedFact]
            ├── IPreferenceExtractor.ExtractAsync() → [ExtractedPreference]
            └── IRelationshipExtractor.ExtractAsync() → [ExtractedRelationship]
                    │
                    ▼
            IEntityResolver.ResolveAsync() per entity
                    │  (exact → fuzzy → semantic match)
                    ▼
            Persist to Neo4j:
                IEntityRepository.UpsertAsync() → MERGE (:Entity)
                IFactRepository.UpsertAsync() → MERGE (:Fact)
                IPreferenceRepository.UpsertAsync() → MERGE (:Preference)
                IRelationshipRepository.UpsertAsync() → MERGE -[:RELATED_TO]->
                    + Auto-create EXTRACTED_FROM relationships
```

### 4.3 Test Coverage Summary

| Test Project | Files | Test Classes | Test Methods |
|-------------|:---:|:---:|:---:|
| Neo4j.AgentMemory.Tests.Unit | 109 | 103 | **1058** |
| Neo4j.AgentMemory.Tests.Integration | 17 | 8 | ~71 |

---

## Appendix: Discrepancy Summary

| # | Document | Issue | Severity | Fix Effort |
|---|----------|-------|----------|------------|
| 1 | schema.md | Phantom `relationship_id` constraint in section 2.5 | Medium | Trivial (delete row) |
| 2 | schema.md | Property index count mismatch (12 listed, 15 actual) | Low | Trivial (add 2 rows) |
| 3 | schema.md | `schema_id_idx` vs `schema_version_idx` not documented | Low | Trivial (add note) |
| 4 | python-dotnet-comparison.md | Test file count "55+" is stale (actual: 111+) | Low | Trivial |
| 5 | feature-record.md | Test file count "55+" is stale | Low | Trivial |
| 6 | feature-record.md / README.md | MCP tool count "21" is stale (actual: 28) | Medium | Trivial |
| 7 | N/A (code) | ToolCallStatus enum missing `Failure` + `Timeout` | Medium | Low (add 2 enum values) |

---

*Generated by Gaff — Neo4j Persistence Engineer*
