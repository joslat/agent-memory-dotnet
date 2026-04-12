# Roy — History

## Project Context
- **Project:** Agent Memory for .NET
- **User:** Jose Luis Latorre Millas
- **Stack:** .NET 9, C#, Neo4j, Microsoft Agent Framework, GraphRAG
- **Role focus:** Core memory domain — Abstractions + Core packages
- **Architecture:** Framework-agnostic core, ports-and-adapters

## Learnings

### 2025-07-14: Epics 4–7 — Core Service Implementations

**Task:** Implemented all Core service classes for Epics 4, 5, 6, and 7 in `src/Neo4j.AgentMemory.Core/Services/`.

**Files Created:**
1. `ShortTermMemoryService` — implements `IShortTermMemoryService`; embedding generation, capped limits via `ShortTermMemoryOptions`, conversation/message lifecycle
2. `LongTermMemoryService` — implements `ILongTermMemoryService`; conditional embedding for entities/facts/preferences, delegates to 4 repos
3. `ReasoningMemoryService` — implements `IReasoningMemoryService`; trace start/step/tool-call/complete lifecycle with `IClock` + `IIdGenerator`
4. `MemoryContextAssembler` — implements `IMemoryContextAssembler`; full parallel recall, optional GraphRAG, 4-strategy context budget enforcement
5. `MemoryService` — facade implementing `IMemoryService`; delegates to shortTerm + assembler + extraction pipeline
6. `ServiceCollectionExtensions` — `AddAgentMemoryCore(Action<MemoryOptions>)` with sub-option bridging via factory `IOptions<T>`

**Also:** Added `Category` property (`string?`) to `Fact` domain model in Abstractions (needed for SchemaBootstrapper index on `f.category`).

**Key Design Decisions:**
1. **`IEmbeddingProvider` method name** — Interface uses `GenerateEmbeddingAsync` (not `GenerateAsync`); verified from source
2. **Sub-option bridging** — `MemoryOptions` sub-records are `init`-only, so DI `Configure<T>(Action<T>)` cannot mutate them at call sites. Used factory-based `IOptions<T>` registration that reads from parent `IOptions<MemoryOptions>` to bridge sub-options to services
3. **Budget enforcement** — 4 strategies: `OldestFirst` (sort by timestamp desc), `LowestScoreFirst` (trim from end), `Proportional` (ratio-based), `Fail` (throw)
4. **GraphRAG is optional** — `IGraphRagContextSource?`; only called when `!= null && options.EnableGraphRag`. Errors are non-fatal (logged as warnings)
5. **Pre-existing test failures** — `SchemaBootstrapperTests` had 7 failing tests before this work; confirmed via `git stash` + test run

**Build Outcome:** `dotnet build` — 0 errors, 0 warnings
**Test Outcome:** 27 passing, 7 pre-existing failures in `SchemaBootstrapperTests`



### 2025-01-27: Phase 1 Domain Model Design

**Task:** Designed complete domain model and interfaces for Neo4j.AgentMemory.Abstractions package.

**Key Design Decisions:**

1. **Domain Model Patterns**
   - Used C# records for all domain models (immutability, value semantics, concise syntax)
   - Applied `required` keyword for spec-mandated fields, nullable types for optional
   - Default empty collections instead of null (better API ergonomics)
   - IReadOnlyList/IReadOnlyDictionary for all collection properties

2. **Service Interface Architecture**
   - IMemoryService as main facade for all operations
   - Separate interfaces per memory layer (IShortTermMemoryService, ILongTermMemoryService, IReasoningMemoryService)
   - IMemoryContextAssembler for orchestrating recall from multiple sources
   - Individual extractor interfaces (IEntityExtractor, IFactExtractor, IPreferenceExtractor, IRelationshipExtractor)
   - IEmbeddingProvider abstraction for vector generation
   - IGraphRagContextSource for GraphRAG interop (defined in Abstractions for dependency inversion)

3. **Repository Pattern Consistency**
   - All repositories follow consistent naming: UpsertAsync, GetByXAsync, SearchByVectorAsync
   - Scored results return tuples: `(Entity, double Score)` for semantic searches
   - Batch operations where appropriate (AddBatchAsync for messages)
   - Separate repositories for each aggregate root per DDD principles

4. **Specification Compliance**
   - All required fields from spec section 3 mapped to domain models
   - Message: MessageId, SessionId, ConversationId, Role, Content, TimestampUtc, Metadata, Embedding
   - Entity: EntityId, Name, CanonicalName, Type, Subtype, Description, Confidence, Attributes
   - Fact: Subject, Predicate, Object, Confidence, ValidFrom/Until
   - Preference: Category, PreferenceText, Context, Confidence
   - Relationship: SourceEntityId, TargetEntityId, RelationshipType, Confidence
   - ReasoningTrace: TraceId, SessionId, Task, TaskEmbedding, Outcome, Success, StartedAt, CompletedAt
   - ReasoningStep: StepId, TraceId, StepNumber, Thought, Action, Observation
   - ToolCall: ToolCallId, StepId, ToolName, ArgumentsJson, ResultJson, Status, DurationMs, Error

5. **Context and Recall Design**
   - MemoryContext as assembled context container with typed sections
   - MemoryContextSection<T> for generic section handling with metadata
   - RecallRequest/RecallResult for recall operations
   - RecallOptions with configurable limits per memory type
   - ContextBudget for token/character limits with truncation strategies

6. **Extraction Pipeline**
   - ExtractionRequest with configurable ExtractionTypes flags enum
   - Separate "Extracted*" types (ExtractedEntity, ExtractedFact, etc.) before persistence
   - ExtractionResult with collections per type
   - Provenance via SourceMessageIds throughout

7. **Configuration Model**
   - MemoryOptions as root configuration with nested options
   - ShortTermMemoryOptions, LongTermMemoryOptions, ReasoningMemoryOptions
   - RecallOptions with Default singleton
   - ContextBudget with TruncationStrategy enum
   - All options use records with init-only properties

8. **Zero Framework Dependencies**
   - No Neo4j.Driver types in Abstractions
   - No Microsoft.Agents.* references
   - No GraphRAG SDK types
   - Pure .NET 9 with nullable reference types enabled

9. **Async and Cancellation**
   - All async methods accept CancellationToken (default parameter)
   - Consistent Task<T> return types
   - Batch operations return IReadOnlyList<T>

10. **Utility Abstractions**
    - IClock for testable time operations
    - IIdGenerator for testable ID generation
    - IEntityResolver for deduplication logic
    - ISchemaRepository for schema versioning and migrations

**Artifacts:**
- Created `.squad/agents/roy/domain-design-v1.md` — 70KB complete design document

**Next Steps:**
- Await Deckard's architectural review
- Address any feedback on interface boundaries or patterns
- Scaffold Neo4j.AgentMemory.Abstractions package
- Begin Core layer design (orchestration services)

---

### 2025-01-27: Epic 2 — Abstractions Package Scaffolded

**Task:** Created all C# source files for `Neo4j.AgentMemory.Abstractions`.

**Final Type Counts:**
- **Domain types:** 26 types across 6 subdirectories (ShortTerm: 3, LongTerm: 4, Reasoning: 4, Context: 4, Extraction: 7, GraphRag: 4)
- **Service interfaces:** 15 interfaces (IMemoryService, IShortTermMemoryService, ILongTermMemoryService, IReasoningMemoryService, IMemoryContextAssembler, IMemoryExtractionPipeline, IEntityExtractor, IRelationshipExtractor, IPreferenceExtractor, IFactExtractor, IEmbeddingProvider, IEntityResolver, IGraphRagContextSource, IClock, IIdGenerator)
- **Repository interfaces:** 10 interfaces (Conversation, Message, Entity, Preference, Fact, Relationship, ReasoningTrace, ReasoningStep, ToolCall, Schema)
- **Options types:** 9 types (MemoryOptions, ShortTermMemoryOptions, LongTermMemoryOptions, ReasoningMemoryOptions, RecallOptions, ContextBudget, SessionStrategy, RetrievalBlendMode, TruncationStrategy)
- **Total source files:** 56 `.cs` files

**Design Refinements During Implementation:**
1. Discovered Gaff had pre-created some Options files with combined types (enums co-located with records). Separated enums into dedicated files per the directory structure spec while keeping the records clean — removed duplicates from pre-existing combined files.
2. All types build cleanly under `net9.0` with zero warnings or errors.
3. `RecallRequest.cs` required explicit `using Neo4j.AgentMemory.Abstractions.Options;` since `RecallOptions` is in a different namespace — the only cross-namespace using in Domain types.

**Build Outcome:** `dotnet build` — `Build succeeded in 0.8s`, zero errors, zero warnings.

**Artifacts:**
- `src/Neo4j.AgentMemory.Abstractions/` — 56 source files, fully compiled

---

### 2025-01-28: Epic 8 — Stub Implementations

**Task:** Created Phase 1 stub/default implementations in `src/Neo4j.AgentMemory.Core/Stubs/` and unit tests in `tests/Neo4j.AgentMemory.Tests.Unit/Stubs/`.

**Stubs Created (9 files):**
1. `SystemClock` — implements IClock, returns DateTimeOffset.UtcNow
2. `GuidIdGenerator` — implements IIdGenerator, returns Guid.NewGuid().ToString("N")
3. `StubEmbeddingProvider` — implements IEmbeddingProvider, deterministic random vectors via text hash seed (configurable dimension, default 1536)
4. `StubEntityExtractor` — implements IEntityExtractor, returns empty list
5. `StubFactExtractor` — implements IFactExtractor, returns empty list
6. `StubPreferenceExtractor` — implements IPreferenceExtractor, returns empty list
7. `StubRelationshipExtractor` — implements IRelationshipExtractor, returns empty list
8. `StubExtractionPipeline` — implements IMemoryExtractionPipeline, orchestrates all four extractors, respects ExtractionTypes flags, populates SourceMessageIds
9. `StubEntityResolver` — implements IEntityResolver, returns entity unchanged (no dedup), uses IClock + IIdGenerator for new Entity creation

**Unit Tests Created (4 files, 21 tests total):**
- `SystemClockTests` — 3 tests: UTC time, UTC offset, advances
- `GuidIdGeneratorTests` — 4 tests: non-empty, uniqueness, no hyphens, 32-char length
- `StubEmbeddingProviderTests` — 7 tests: dimension, default 1536, determinism, different inputs → different vectors, batch count, batch dimensions, configurable dimension
- `StubExtractionPipelineTests` — 7 tests: empty entities/facts/preferences/relationships, source message IDs populated, extraction type flags respected, metadata stub flag

**Build Outcome:** `dotnet build` — succeeded, 0 errors, 0 warnings
**Test Outcome:** `dotnet test` — 21/21 passed ✅

**Key Learnings:**
- FluentAssertions v8 `NotBeEquivalentTo` on large (1536-element) float arrays hangs the test host — use `SequenceEqual(...).Should().BeFalse(...)` instead for large collection inequality checks.
- `StubEmbeddingProvider` uses `string.GetHashCode()` as seed for `Random` — deterministic within a process run, which is sufficient for Phase 1 testing.
- `StubEntityResolver` depends on IClock and IIdGenerator to construct a proper `Entity` record with all required fields satisfied.

**Artifacts:**
- `src/Neo4j.AgentMemory.Core/Stubs/` — 9 stub implementation files
- `tests/Neo4j.AgentMemory.Tests.Unit/Stubs/` — 4 test files, 21 passing tests


