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

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
