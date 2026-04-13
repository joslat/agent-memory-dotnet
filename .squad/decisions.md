# Squad Decisions

## Active Decisions

### D1: Package Structure (Deckard, 2025-01-28)

**Status:** Approved  
**Scope:** Phase 1 (Core Memory Engine)

We commit to the following package structure for Phase 1:

```
src/
  Neo4j.AgentMemory.Abstractions/
  Neo4j.AgentMemory.Core/
  Neo4j.AgentMemory.Neo4j/

tests/
  Neo4j.AgentMemory.Tests.Unit/
  Neo4j.AgentMemory.Tests.Integration/

deploy/
  docker-compose.dev.yml
```

**Rationale:** Establishes clear layering with abstractions as contracts, core as business logic, and Neo4j as persistence adapter. Testcontainers support enables real integration testing.

---

### D2: Dependency Direction (Deckard, 2025-01-28)

**Status:** Approved  
**Scope:** Phase 1 (Core Memory Engine)

Strictly enforce:
- Core → Abstractions only
- Neo4j → Abstractions + Core
- No reverse dependencies
- No MAF/GraphRAG types in Phase 1

**Rationale:** Maintains clean architecture boundaries. Contracts-first design ensures dependency direction is correct from day one.

---

### D3: Test Harness (Deckard, 2025-01-28)

**Status:** Approved  
**Scope:** Phase 1 (Core Memory Engine)

Use Testcontainers for .NET with Neo4j for all integration tests. Every repository and service must have integration test coverage before Phase 1 completion.

**Rationale:** Ensures real Neo4j integration testing without manual infrastructure setup. All Phase 1 tests must run in CI with Testcontainers.

---

### D4: Bootstrap Order (Deckard, 2025-01-28)

**Status:** Approved  
**Scope:** Phase 1 (Core Memory Engine)

Build in this sequence:
1. Abstractions (contracts first)
2. Neo4j infrastructure (driver, schema, transactions)
3. Short-term memory (messages, conversations)
4. Long-term memory (entities, facts, preferences, relationships)
5. Reasoning memory (traces, steps, tool calls)
6. Context assembler

**Rationale:** Enables parallel work after abstractions stabilize. Lower layers unblock higher layers.

---

### D5: Stubbing Strategy (Deckard, 2025-01-28)

**Status:** Approved  
**Scope:** Phase 1 (Core Memory Engine)

Stub `IEmbeddingProvider` and `IExtractionService` in Phase 1. Implement in Phase 2.

**Rationale:** Extraction workflows testable with stub implementations until Phase 2. Allows memory core to progress independently.

---

### D6: Domain Models and Interface Design (Roy, 2025-01-27)

**Status:** Approved  
**Scope:** Abstractions Package Foundation

#### 6.1 Domain Models as C# Records

All domain models use C# records for immutability, value semantics, and concise init-only property syntax.

**Rationale:** Records provide structural equality, prevent accidental mutation, improve readability, and fit naturally for data transfer between layers.

---

#### 6.2 Repository Pattern with Consistent Naming

All repositories follow standard naming:
- `UpsertAsync` for add-or-update operations
- `GetByXAsync` for lookups
- `SearchByVectorAsync` for semantic searches
- Return tuples `(Entity, double Score)` for scored results

**Rationale:** Consistency reduces cognitive load. Tuple returns avoid extra wrapper types. Clear semantics and testability.

---

#### 6.3 Layered Service Interfaces

- **IMemoryService** — facade for high-level operations
- **IShortTermMemoryService** — conversation and message operations
- **ILongTermMemoryService** — entities, facts, preferences, relationships
- **IReasoningMemoryService** — traces, steps, tool calls
- **IMemoryContextAssembler** — orchestrates recall across layers
- **IMemoryExtractionPipeline** — coordinates extraction
- Individual extractors: IEntityExtractor, IFactExtractor, etc.

**Rationale:** Clear separation of concerns. Each layer independently testable. Facade simplifies common operations. Pipeline pattern allows composition.

---

#### 6.4 Zero Framework Dependencies in Abstractions

Abstractions package has NO dependencies on:
- Neo4j.Driver
- Microsoft.Agents.*
- GraphRAG SDKs
- Any infrastructure concerns

**Rationale:** Maintains clean architecture boundaries. Core logic remains portable. Adapters evolve independently. Easier to test in isolation.

---

#### 6.5 Provenance and Metadata Throughout

All extracted long-term memory includes:
- `SourceMessageIds` for traceability
- `Metadata` dictionaries for extensibility
- `CreatedAtUtc` timestamps

**Rationale:** Debugging and auditing support. Enables future features (expiration, user corrections). Meets spec requirement for provenance.

---

#### 6.6 Strong Typing with Enums

Use enums for all status/strategy/mode values:
- `ToolCallStatus` (Pending, Success, Error, Cancelled)
- `SessionStrategy` (PerConversation, PerDay, PersistentPerUser)
- `RetrievalBlendMode` (MemoryOnly, GraphRagOnly, Blended, etc.)
- `TruncationStrategy` (OldestFirst, LowestScoreFirst, Proportional, Fail)
- `ExtractionTypes` (flags enum)

**Rationale:** Compile-time safety, IntelliSense support, avoids stringly-typed APIs, clear intent.

---

#### 6.7 GraphRAG Types in Abstractions

Define `IGraphRagContextSource`, `GraphRagContextRequest`, `GraphRagContextResult` in Abstractions, not adapter.

**Rationale:** Dependency inversion principle. Core depends on abstraction. Adapter implements abstraction. Enables testing with mocks.

---

### D7: LLM-Based Extraction Framework (Roy, 2026-04-12)

**Status:** Approved  
**Scope:** Phase 2 (Extraction & Resolution)

Implement extraction via `Neo4j.AgentMemory.Extraction.Llm` using `Microsoft.Extensions.AI.Abstractions.IChatClient` for LLM-based structured extraction. This provides vendor-neutral LLM integration (supports OpenAI, Anthropic, local models, etc.) via dependency injection.

**Rationale:** LLM-based extraction avoids Python runtime dependency. IChatClient abstracts over any LLM backend. Extraction services for entities, facts, and preferences use a common template-based prompt pattern.

---

### D8: FuzzySharp for Entity Resolution (Roy, 2026-04-12)

**Status:** Approved  
**Scope:** Phase 2 (Extraction & Resolution)

Add `FuzzySharp 2.0.2` to `Neo4j.AgentMemory.Core.csproj` for entity name fuzzy matching. Uses `Fuzz.TokenSortRatio` to handle name permutations (e.g., "John Smith" vs "Smith John").

**Rationale:** Provides .NET-native equivalent to Python's `rapidfuzz.fuzz.token_sort_ratio`. Phase 2 entity resolution chain uses fuzzy matching as the second stage (exact → fuzzy → semantic → new).

---

### D9: Entity Resolution Chain Lives in Core (Roy, 2026-04-12)

**Status:** Approved  
**Scope:** Phase 2 (Extraction & Resolution)

Implement `CompositeEntityResolver` and matcher chain (`ExactMatchEntityMatcher`, `FuzzyMatchEntityMatcher`, `SemanticMatchEntityMatcher`) in `Neo4j.AgentMemory.Core`, not in the Neo4j persistence layer. Resolution is business logic, not a persistence concern.

**Rationale:** Clean separation: Core owns resolution strategy, Neo4j repositories own persistence of SAME_AS relationships and entity merges. Testable in isolation. Reusable across multiple persistence backends.

---

### D10: EntityResolutionResult for Metadata (Roy, 2026-04-12)

**Status:** Approved  
**Scope:** Phase 2 (Extraction & Resolution)

New `EntityResolutionResult` record in `Abstractions/Domain/Extraction/` captures `MatchType` (Exact/Fuzzy/Semantic), `Confidence` (0.0–1.0), and `MergedFrom` metadata. Internal to resolution chain; public `IEntityResolver.ResolveAsync()` still returns `Task<Entity>` for backward compatibility.

**Rationale:** Separates resolution metadata (needed for SAME_AS relationship creation) from the public interface contract. Prevents breaking changes when resolution internals evolve.

---

### D11: AgentFramework as Thin Adapter (Deckard, 2026-04-12)

**Status:** Approved  
**Scope:** Phase 3 (MAF Adapter)

The `Neo4j.AgentMemory.AgentFramework` package is a thin translation layer only. Contains `MemoryContextProvider : AIContextProvider`, type mappers (ChatMessage ↔ Message, session ↔ conversation), DI registration, and MAF-specific configuration. All memory logic stays in Core/Abstractions.

**Rationale:** Clean architecture. If MAF API changes or a different framework emerges, only this package changes. Core remains framework-agnostic. Adapter logic is testable in isolation via `InternalsVisibleTo`.

---

### D12: Explicit Type Mapping Strategy (Deckard, 2026-04-12)

**Status:** Approved  
**Scope:** Phase 3 (MAF Adapter)

Dedicated stateless mapper classes (`ChatMessageMapper`, `SessionMapper`, `ContextMapper`) in `AgentFramework/Mapping/` namespace handle all MAF ↔ domain type conversions. Mappers are `internal`, exposed to tests via `InternalsVisibleTo`.

**Rationale:** Prevents MAF types from leaking into domain logic. When MAF evolves, changes are isolated. Clear, testable, single-responsibility functions.

---

### D13: Dual-Lifecycle Implementation (Deckard, 2026-04-12)

**Status:** Approved  
**Scope:** Phase 3 (MAF Adapter)

`MemoryContextProvider` implements:
- **Pre-run:** `ProvideAIContextAsync(InvokingContext)` → assemble memory context via `IMemoryContextAssembler`, return populated `AIContext`
- **Post-run:** `StoreAIContextAsync(InvokedContext)` → persist messages, trigger extraction pipeline, handle exceptions gracefully

Graceful degradation: pre-run failures return empty context (agent works without memory); post-run failures are logged but don't break agent response.

**Rationale:** Maps directly to MAF's `AIContextProvider` dual lifecycle. No custom middleware required. Ensures agent framework never breaks due to memory subsystem failures.

---

### D14: OpenTelemetry API for Observability (Deckard, 2026-04-12)

**Status:** Approved  
**Scope:** Phase 4 (Observability)

Observability implemented via decorator pattern (`InstrumentedMemoryService`, `InstrumentedGraphRagContextSource`) in a new `Neo4j.AgentMemory.Observability` package. Depends on `OpenTelemetry.Api` only (not SDK). Uses `System.Diagnostics.ActivitySource` for tracing and `System.Diagnostics.Metrics.Meter` for metrics. Consumers bring their own SDK/exporters.

Manual decoration (not Scrutor); opt-in via `AddAgentMemoryObservability()` DI extension. Zero overhead when not used.

**Rationale:** Keeps library lightweight. Consumers control telemetry pipeline. No opinionated exporter choice. Core library uncoupled from telemetry. Follows OpenTelemetry semantic conventions with `memory.*` namespace tags.

---

### D15: MAF 1.1.0 API Integration Blueprint (Deckard, 2026-04-12)

**Status:** Reference/Approved  
**Scope:** Phase 3 (MAF Adapter) — Reference Analysis

Documented comprehensive MAF 1.1.0 API surface analysis covering primary extension points (`AIContextProvider`, `InvokingContext`, `InvokedContext`, `AIContext`), supporting types (`ChatHistoryProvider`, `AIAgent`, `AgentSession`, `AgentResponse`), integration patterns for context recall/persist, message type mapping (`ChatMessage` from M.E.AI), and session state management via `AgentSessionStateBag`.

**Rationale:** Reference for adapter implementation. Clarifies what MAF provides, what we extend, and how the dual lifecycle maps to our recall/persist pattern. Decouples from rapid MAF evolution by documenting specific version (1.1.0) assumptions.

---

## Findings & Recommendations

### F1: Cross-Memory Relationships Missing (HIGH) — Deckard, 2026-04-13

**Finding:** Python reference implements 6+ cross-memory relationship types that link traces to messages, tool calls to messages, and conversations to traces. Our .NET implementation has only SAME_AS.

**Missing Relationships:**
- `INITIATED_BY` — reasoning trace → initiating message
- `TRIGGERED_BY` — tool call → triggering message
- `HAS_TRACE` — conversation → reasoning trace
- `EXTRACTED_FROM` — entity/fact → source message (as graph relationship)
- `ABOUT` — preference → associated entity

**Impact:** Context assembly cannot traverse across memory tiers via graph relationships. Limits graph-native query power.

**Recommendation:** Add to SchemaBootstrapper and repository UpsertAsync/AddAsync methods. Single highest-impact improvement available.

---

### F2: Entity Alias Merging Incomplete (MEDIUM) — Deckard, 2026-04-13

**Finding:** When entity resolution finds match at ≥0.95 confidence, SAME_AS relationship created but target entity's `Aliases` array not updated with source entity's name.

**Recommendation:** Update `Neo4jEntityRepository.MergeEntitiesAsync` to consolidate aliases into target entity's aliases array.

---

### F3: Cypher Query Centralization (MEDIUM) — Deckard, 2026-04-13

**Finding:** Python's `graph/queries.py` centralizes 60+ Cypher queries as constants. Our implementation has queries inline in 9 repository classes.

**Recommendation:** Create `Neo4j/Queries/CypherQueries.cs` with organized constants. Improves maintainability, enables Cypher review without implementation logic, reduces duplication.

---

### F4: Test Documentation Stale (LOW) — Deckard, 2026-04-13

**Finding:** `docs/implementation-status.md` claims 398 unit tests. Actual count is 419 (Holden's +21 update).

**Recommendation:** Update test counts and Phase status references in documentation.

---

### F5: Post-Run Extraction Not Automated (LOW) — Deckard, 2026-04-13

**Finding:** Spec §4.4 says MAF adapter should "trigger extraction on newly persisted content." Current implementation requires manual `ExtractAndPersistAsync()` call.

**Recommendation:** Add optional auto-extraction hook in `Neo4jMicrosoftMemoryFacade` or document the pattern clearly in samples.

---

### G1–G5: Entity Resolution Persistence Decisions (Gaff, 2025-07-15)

**Implemented Decisions:**
- **D-G1:** MENTIONS as native Neo4j relationship (no properties, batch via UNWIND)
- **D-G2:** SAME_AS stores confidence + matchType as relationship properties (directional, queried bidirectionally)
- **D-G3:** MergeEntitiesAsync uses Neo4j 5 CALL subquery syntax (single transaction)
- **D-G4:** SearchByNameAsync uses toLower CONTAINS (not fulltext index, simpler for Phase 2)
- **D-G5:** entity_merged_into_idx added to SchemaBootstrapper (efficient dead entity queries)

**Status:** All implemented and verified working.

---

### H1: Integration Test Framework Ready (Holden, 2026-04-13)

**Finding:** 9 repository classes need integration test coverage via Testcontainers. Harness is fully implemented and ready:
- `IntegrationTestBase` abstract class with fixture and helpers
- `TestDataSeeders` factory methods for all domain types
- `Neo4jTestCollection` collection definition
- `Neo4jConnectivityTests` smoke tests passing

**Recommendation:** Scaffold integration test classes for each repository class when prioritized. ~50 test methods per repository class. Single-sprint effort.

---

### D16: Cross-Memory Relationships Implementation (Gaff, 2026-04-13)

**Status:** Implemented  
**Scope:** Phase 2 (Extraction & Resolution) — Relationship Completion

All 9 missing cross-memory relationship types from audit finding F1 are now implemented across Neo4j repositories.

#### D-G6: All 9 Missing Relationship Types

| Type | Auto-Wired | Method | Purpose |
|---|---|---|---|
| FIRST_MESSAGE | ✅ | - | Conversation → first message |
| EXTRACTED_FROM | ✅ | via UpsertAsync | Entity/fact/preference provenance |
| CALLS | ✅ | via AddAsync | Tool invocation tracking |
| ABOUT | ❌ | CreateAboutRelationshipAsync | Preference → entity association |
| INITIATED_BY | ❌ | CreateInitiatedByRelationshipAsync | Trace → initiating message |
| TRIGGERED_BY | ❌ | CreateTriggeredByRelationshipAsync | Tool call → source message |
| HAS_TRACE | ❌ | CreateHasTraceRelationshipAsync | Conversation → reasoning trace |
| IN_SESSION | ❌ | (paired with HAS_TRACE) | Trace ↔ conversation bidirectional |
| HAS_PREFERENCE | ❌ | CreateConversationPreferenceRelationshipAsync | Conversation → preference |
| HAS_FACT | ❌ | CreateConversationFactRelationshipAsync | Conversation → fact |

Auto-wired relationships are derived from node properties (SourceMessageIds, ToolName, ConversationId) and created during UpsertAsync/AddAsync. Explicit relationships require caller context and are invoked by the extraction pipeline/service layer.

**Rationale:** Relationships that can be inferred from node data are automatic. Relationships requiring external knowledge (entity association, triggering message, conversation linkage) are explicit methods, giving the pipeline full control over when they fire.

---

#### D-G7: CALLS Creates and Tracks Tool Nodes

`Neo4jToolCallRepository.AddAsync` auto-creates `:Tool {name}` nodes on first encounter and increments `tool.totalCalls` counter via:

```cypher
MERGE (tool:Tool {name: $toolName}) ON CREATE SET tool.createdAtUtc = $now
MERGE (tc)-[:CALLS]->(tool)
SET tool.totalCalls = COALESCE(tool.totalCalls, 0) + 1
```

Enables tool usage analytics without separate infrastructure.

---

#### D-G8: UpsertBatchAsync Uses UNWIND for Efficiency

Batch upserts for Entity and Fact use `UNWIND $items AS item` pattern for single round-trip per batch. Embeddings set individually (Neo4j vector property constraints). EXTRACTED_FROM relationships created per-entity within transaction.

---

#### D-G9: FIRST_MESSAGE Idempotent via WHERE NOT EXISTS

`(conv)-[:FIRST_MESSAGE]->()` created with:

```cypher
WHERE NOT EXISTS { MATCH (conv)-[:FIRST_MESSAGE]->() }
```

Safe to call multiple times; relationship created only for the actual first message.

---

#### D-G10: HAS_TRACE and IN_SESSION Atomic

Both directions created in single Cypher statement:

```cypher
MERGE (c)-[:HAS_TRACE]->(t)
MERGE (t)-[:IN_SESSION]->(c)
```

Ensures consistency — either both exist or neither.

---

### D17: Repository Interface Additions and Service-Layer Completion (Roy, 2026-04-13)

**Status:** Implemented  
**Scope:** Phase 2 (Extraction & Resolution) — Service Facade Completion

Added 9 interface methods across 4 repositories and 1 service to complete CRUD contract and enable extraction pipeline wiring.

#### D-R1: UpsertBatchAsync Added to Entity and Fact Repositories

```csharp
Task<IReadOnlyList<Entity>> UpsertBatchAsync(IReadOnlyList<Entity>, CancellationToken);
Task<IReadOnlyList<Fact>> UpsertBatchAsync(IReadOnlyList<Fact>, CancellationToken);
```

Needed for efficient extraction pipeline bulk writes. Neo4j implementations use UNWIND for single round-trip per batch.

**Rationale:** Batch operations critical for throughput at scale. Interface contract enables mocking and testing; Neo4j implementation can evolve independently.

---

#### D-R2: DeleteAsync Added to Preference Repository

```csharp
Task DeleteAsync(string preferenceId, CancellationToken = default);
```

Preferences must be deletable for user revocation and conflict resolution. `Neo4jPreferenceRepository` implements via `DETACH DELETE`.

**Rationale:** Without delete, preferences are write-only. Contradictory preferences accumulate and inject conflicting facts into context.

---

#### D-R3: Cross-Memory Relationship Methods

Added to entity, fact, and preference repositories:

- `CreateExtractedFromRelationshipAsync(entityId/factId/preferenceId, messageId)` — provenance edges
- `CreateAboutRelationshipAsync(preferenceId/factId, entityId)` — semantic linkage

Completes EXTRACTED_FROM and ABOUT relationship graph edges identified in finding F1. All Neo4j implementations pre-existed; interfaces now declare contracts.

---

#### D-R4: Conditional Re-Embedding After Entity Merge

In `CompositeEntityResolver`, after auto-merge (confidence ≥ threshold), re-embed **only if aliases list changed**:

```csharp
if (aliasesChanged) {
    var reEmbedded = entity with {
        Embedding = await _embeddingProvider.GenerateEmbeddingAsync(
            $"{entity.Name} {string.Join(" ", entity.Aliases)}", ...)
    };
    return reEmbedded;
}
```

Preserves existing test contract: exact matches (no new aliases) don't trigger embedding provider re-calls. Conditional re-embedding fixes bug where merged aliases weren't reflected in embedding vector.

**Rationale:** Keeps embedding semantics correct without breaking test invariants.

---

#### D-R5: EXTRACTED_FROM Wired in MemoryExtractionPipeline

After upserting each entity, fact, and preference, pipeline calls `CreateExtractedFromRelationshipAsync` for every source message ID. Failures logged as warnings (non-fatal) to preserve pipeline resilience.

Closes provenance gap where graph edges from memory nodes to source messages were never created.

---

#### D-R6: DeletePreferenceAsync Added to Service Facade

Added to `ILongTermMemoryService`:

```csharp
Task DeletePreferenceAsync(string preferenceId, CancellationToken);
```

Service-layer callers should not inject repository interfaces directly. Service facade exposes all CRUD operations. Preferences were the only long-term memory type without a delete path.

---

### D18: MAF Post-Run Integration and Advanced MCP Tools (Rachael, 2026-04-13)

**Status:** Implemented  
**Scope:** Phase 3 (MAF Adapter) + Phase 4 (MCP Tools)

Completed spec §4.4 auto-extraction compliance via `StoreAIContextAsync` hook and added 4 advanced memory operations to MCP tooling.

#### D-R1: StoreAIContextAsync Is the Canonical Post-Run Hook

`Neo4jMemoryContextProvider.StoreAIContextAsync(InvokedContext)` is the spec §4.4 extraction trigger (not middleware).

```csharp
public override async Task StoreAIContextAsync(InvokedContext context, CancellationToken cancellationToken = default)
{
    try
    {
        if (_options.AutoExtractOnPersist) {
            // Persist ResponseMessages
            // Trigger MemoryExtractionPipeline
        }
    }
    catch (Exception ex) {
        _logger.LogWarning(...); // Non-fatal
    }
}
```

MAF's designed post-run lifecycle hook. No custom middleware needed. Implementation entirely in AgentFramework adapter.

---

#### D-R2: Auto-Extraction Is Opt-Out (Default ON)

`AgentFrameworkOptions.AutoExtractOnPersist = true` controls post-run extraction (default enabled).

**Rationale:** Spec §4.4 says extraction SHALL happen automatically. Opt-out satisfies SHALL while allowing consumers to disable. Extraction failures never break agent response — logged as warnings only.

---

#### D-R3: StoreAIContextAsync Persists ResponseMessages Only

Hook persists only `context.ResponseMessages` (assistant outputs), not request messages.

**Rationale:** Request messages are either already in memory from prior turns or were injected by `ProvideAIContextAsync` and should not be double-stored. Only net-new assistant responses warrant persistence.

---

#### D-R4: AdvancedMemoryTools File Pattern

Four new MCP tools live in `AdvancedMemoryTools.cs`:

| Tool | Purpose |
|---|---|
| memory_record_tool_call | Persist tool invocation with result |
| memory_export_graph | Export entire Neo4j graph |
| memory_find_duplicates | Detect potential duplicate entities |
| extract_and_persist | Run extraction on arbitrary text |

Registered via `.WithTools<AdvancedMemoryTools>()` in `ServiceCollectionExtensions.AddAgentMemoryMcpTools()`.

**Rationale:** Grouping by concern (not service dependency) keeps files manageable. Extends existing 5-file pattern with 6th for advanced/cross-cutting ops. Total: 18 tools registered.

---

#### D-R5: Graph Query Tools Gate Behind EnableGraphQuery

Both `memory_export_graph` and `memory_find_duplicates` gate behind `McpServerOptions.EnableGraphQuery = true`. Use `IGraphQueryService` with raw Cypher, same security model as `graph_query` tool.

**Rationale:** Execute raw Cypher against Neo4j — same trust boundary as existing `graph_query`. Reusing gate avoids new option surface.

---

#### D-R6: memory_find_duplicates Uses Name Containment + Length Ratio

Duplicate detection uses Cypher:

```cypher
MATCH (e1:Entity), (e2:Entity) WHERE e1.id <> e2.id
AND toLower(e1.name) CONTAINS toLower(e2.name)
WITH e1, e2, min(len(e1.name), len(e2.name)) / max(len(e1.name), len(e2.name)) AS similarity
WHERE similarity >= $threshold
```

**Rationale:** Semantic similarity requires embeddings (not always available). FuzzySharp in Core, not available in McpServer (only references Abstractions). Name containment with length ratio in pure Cypher catches "John" vs "John Smith"-style duplicates reliably, transparent to operators.

---

### SC-1: Multi-Stage Extraction Pipeline — HIGH PRIORITY

**Finding:** Python's `ExtractionPipeline` chains spaCy → GLiNER → LLM with 5 configurable merge strategies. .NET runs exactly one extractor per type. For production cost control, combining a cheap fast extractor with LLM fallback is critical.

**Recommendation:** Add `IExtractionPipeline` composition to `MemoryExtractionPipeline`:
- Accept `IReadOnlyList<IEntityExtractor>` instead of a single `IEntityExtractor`.
- Implement at least `CONFIDENCE` and `FIRST_SUCCESS` merge strategies.
- Short-circuit on `FIRST_SUCCESS` to avoid unnecessary LLM calls.

**Status:** Proposed  
**Reference:** `docs/python-dotnet-comparison.md` (Sebastian, 2025-07-12)

---

### SC-2: Fact Deduplication — HIGH PRIORITY

**Finding:** Python has `fact_deduplication_enabled` config. .NET writes a new `Fact` node on every extraction without checking for semantic duplicates. Contradicting or stale facts silently accumulate.

**Recommendation:** Add deduplication in `IFactRepository.UpsertAsync`:
- Compute fact hash (subject + predicate + object, normalized).
- Check existing fact by hash before insert; update confidence if hash matches.
- Optional: vector-similarity fallback for semantically equivalent facts above threshold.

**Status:** Proposed  
**Reference:** `docs/python-dotnet-comparison.md` (Sebastian, 2025-07-12)

---

### SC-3: Background Enrichment Queue — MEDIUM PRIORITY

**Finding:** Python's `BackgroundEnrichmentQueue` is non-blocking. .NET's `WikimediaEnrichmentService` is synchronous and blocks the ingestion path.

**Recommendation:** Introduce `IBackgroundEnrichmentQueue` backed by `Channel<EnrichmentTask>`. Register as `IHostedService`. Extraction pipeline enqueues; hosted service dequeues and calls `IEnrichmentService.EnrichAsync()`. Enables non-blocking enrichment without changing the pipeline interface.

**Status:** Proposed  
**Reference:** `docs/python-dotnet-comparison.md` (Sebastian, 2025-07-12)

---

### SC-4: MCP Resources and Prompts — MEDIUM PRIORITY

**Finding:** Python exposes 4 MCP resources (`memory://context/{id}`, `memory://entities`, etc.) and 3 MCP prompts (memory-conversation, memory-reasoning, memory-review). These provide Claude Desktop with auto-injected context and slash-command workflows.

**Recommendation:** Add `[McpServerResource]` handlers in `McpServer` for at minimum `memory://context/{sessionId}`. Add `[McpServerPrompt]` for `memory-conversation`. Both are low-effort and improve end-user DX significantly.

**Status:** Proposed  
**Reference:** `docs/python-dotnet-comparison.md` (Sebastian, 2025-07-12)

---

### SC-5: Streaming Extraction — MEDIUM PRIORITY

**Finding:** Python's `StreamingExtractor` splits large texts into overlapping chunks and yields `ExtractionResult` via async generator. Needed for transcripts, long documents, RAG inputs.

**Recommendation:** Add `ExtractStreamingAsync(IAsyncEnumerable<string> chunks)` overload to `IMemoryExtractionPipeline`. Use `IAsyncEnumerable<ExtractionResult>` return type.

**Status:** Proposed  
**Reference:** `docs/python-dotnet-comparison.md` (Sebastian, 2025-07-12)

---

### SC-6: .NET Exclusive Features to Document

**Finding:** The following .NET features have no Python equivalent and should be highlighted in README:

- **GraphRAG adapter** (`Neo4jGraphRagContextSource`): reads external Neo4j KGs via vector/fulltext/hybrid.
- **Abstractions package**: enables clean test substitution and consumer packages.
- **Azure Language extraction**: enterprise cloud NLP without local ML models.
- **`DeletePreferenceAsync`**: preference retraction — Python API lacks this entirely.
- **`UpsertBatchAsync`** on entities and facts: UNWIND-based bulk persistence.
- **`extract_and_persist` MCP tool**: explicit extraction trigger; Python has no equivalent.

**Recommendation:** Update README.md to highlight these differentiators in the "Why .NET" section.

**Status:** Proposed  
**Reference:** `docs/python-dotnet-comparison.md` (Sebastian, 2025-07-12)

---

### SC-7: Geocoding + Geospatial Index — LOW PRIORITY

**Finding:** Python adds a `Neo4j Point` property to Location entities via Nominatim/Google geocoding and creates a point index (`entity_location_idx`). Enables radius queries.

**Recommendation:** Low priority unless location-based query use cases are confirmed. If needed: `IGeocodingService` → `HttpClient` → `IEntityRepository` update → one Cypher point index creation in `ISchemaRepository.SetupAsync()`.

**Status:** Proposed  
**Reference:** `docs/python-dotnet-comparison.md` (Sebastian, 2025-07-12)

---

### SC-8: Python Framework Integrations — NOT A PRIORITY

**Finding:** Python supports LangChain, OpenAI Agents, Pydantic AI, LlamaIndex, CrewAI, Google ADK, AWS AgentCore. These are Python-ecosystem frameworks.

**Recommendation:** No action required for .NET. The equivalent integration surface is MAF (already implemented). If .NET ecosystem equivalents are requested (e.g., Semantic Kernel), treat as separate integration packages.

**Status:** Acknowledged  
**Reference:** `docs/python-dotnet-comparison.md` (Sebastian, 2025-07-12)

---

### D-PKG1: Publish AgentFramework as Separate NuGet Package

**Status:** Approved  
**Scope:** NuGet Packaging Topology

**Decision:** `Neo4j.AgentMemory.AgentFramework` SHOULD be published as its own NuGet package.

**Rationale:** The MAF dependency (`Microsoft.Agents.AI.Abstractions 1.1.0`) must not pollute non-MAF consumers. The boundary is architecturally correct. Independent versioning lets the adapter track MAF releases while Core evolves separately.

**Reference:** `docs/package-strategy-and-features.md` (Deckard, July 2026)

---

### D-PKG2: GraphRAG Retrieval Separation Strategy

**Status:** Approved  
**Scope:** NuGet Packaging Topology

**Decision:** Keep `Neo4j.AgentMemory.GraphRagAdapter` as-is for the neo4j-maf-provider bridge. Future: create `Neo4j.AgentMemory.Retrieval` for standalone read-only search without full memory engine.

**Rationale:** The GraphRagAdapter serves a specific bridge role. A separate Retrieval package would serve users who want vector/fulltext/hybrid search without committing to the full memory engine. The current ProjectReference to neo4j-maf-provider source needs resolution before NuGet publishing.

**Reference:** `docs/package-strategy-and-features.md` (Deckard, July 2026)

---

### D-PKG3: Meta-Package Strategy

**Status:** Proposed  
**Scope:** NuGet Packaging Topology

**Decision:** Consider publishing `Neo4j.AgentMemory` meta-package (Abstractions + Core + Neo4j + Extraction.Llm) for convenience.

**Rationale:** Reduces onboarding friction. Users install one package for the common case. Power users can pick individual packages.

**Reference:** `docs/package-strategy-and-features.md` (Deckard, July 2026)

---

### D-PKG4: NuGet Publish Order

**Status:** Approved  
**Scope:** NuGet Packaging Topology

**Decision:** Publish in dependency order: Abstractions → Core → Neo4j → Extension packages → Adapter packages → Meta-package.

**Rationale:** Each tier depends only on previously published tiers. Prevents circular dependency issues.

**Reference:** `docs/package-strategy-and-features.md` (Deckard, July 2026)

---

### D-FEAT1: Tier 1 Feature Priorities

**Status:** Proposed  
**Scope:** Feature Roadmap (Next 6 months)

**Decision:** The next features to implement are (in order):
1. Batch Operations (P1)
2. Health Checks (O1)
3. Conversation Summarization (I1)
4. Fluent Configuration Builder (D1)
5. Semantic Kernel Adapter (F1)

**Rationale:** These address production readiness (P1, O1), user demand aligned with Python community (I1), developer experience (D1), and market reach (F1). All are High impact with Medium or smaller effort.

**Reference:** `docs/package-strategy-and-features.md` (Deckard, July 2026)

---

### D-FEAT2: Python Parity Targets

**Status:** Proposed  
**Scope:** Feature Roadmap (Python Alignment)

**Decision:** Prioritize features that address open Python agent-memory issues (#91, #44, #42, #13) to position .NET as the more mature implementation.

**Rationale:** 8 of 21 open Python issues map to our Tier 1–2 proposals. Implementing them first demonstrates .NET leadership in the agent memory space.

**Reference:** `docs/package-strategy-and-features.md` (Deckard, July 2026)

---

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
