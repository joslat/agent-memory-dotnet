# Roy — History

## Project Context
- **Project:** Agent Memory for .NET
- **User:** Jose Luis Latorre Millas
- **Stack:** .NET 9, C#, Neo4j, Microsoft Agent Framework, GraphRAG
- **Role focus:** Core memory domain — Abstractions + Core packages
- **Architecture:** Framework-agnostic core, ports-and-adapters

## Recent Work (Wave 1, 2026-07-18)

**Sprint:** MEAI Migration & Architecture Review Finalization

1. **ToolCallStatus Enum Expansion** — Added `Failure` and `Timeout` values. Fixed related Cypher bug. (1,059 tests green ✅)
2. **Extraction Package Merge Analysis** — Deep code audit of Extraction.Llm and Extraction.AzureLanguage. Finding: 9.7% actual duplication (100 LOC / 1,031 LOC) — below 10% threshold. **Decision:** Keep separate; remove unnecessary Core dependency instead (pragmatic override of D-AR2-2).
3. **Team Synchronization** — Validated D-KP-1 meta-package scope with Deckard's architect team. Extraction decision supports lean meta-bundle design.

---

## Learnings

### Wave 2 Findings 3+4, 2026-07-18 (Pipeline SRP Split + Multi-extractor Merge)

**Task:** Split `MemoryExtractionPipeline` (14 deps, 4 responsibilities) into `ExtractionStage` + `PersistenceStage`. Merge `MultiExtractorPipeline` into `ExtractionStage`.

**New files created:**
- `src/Neo4j.AgentMemory.Core/Extraction/ExtractionStageResult.cs` — internal `record` DTO between stages
- `src/Neo4j.AgentMemory.Core/Extraction/IExtractionStage.cs` + `ExtractionStage.cs` — runs multi-extractor, merges, filters, validates, resolves
- `src/Neo4j.AgentMemory.Core/Extraction/IPersistenceStage.cs` + `PersistenceStage.cs` — embeds, upserts, wires EXTRACTED_FROM provenance

**Files deleted:**
- `src/Neo4j.AgentMemory.Core/Services/MultiExtractorPipeline.cs` (logic absorbed into ExtractionStage)
- `tests/.../Extraction/MultiExtractorPipelineTests.cs` (tests migrated to ExtractionStageTests)

**Key decisions:**
1. `IExtractionStage` and `IPersistenceStage` are `internal` (not public) — they're Core implementation details. `MemoryExtractionPipeline` constructor is also `internal` (DI uses reflection). Public contract `IMemoryExtractionPipeline` unchanged.
2. `ExtractionStageResult` must be a `record` (not `class`) for test `with` expressions to work.
3. NSubstitute mocking of internal interfaces requires `[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]` in Core's AssemblyInfo.cs — added alongside existing Tests.Unit entry.
4. `ExtractionStage` accepts `IEnumerable<T>` extractor collections from DI; builds internal `IReadOnlyList<T>`. Single extractor bypasses merge. Multiple extractors apply configured `MergeStrategyType`.
5. DI registration: `TryAddScoped<IExtractionStage, ExtractionStage>` + `TryAddScoped<IPersistenceStage, PersistenceStage>` added to `ServiceCollectionExtensions.cs`. Pipeline still registered as `TryAddScoped<IMemoryExtractionPipeline, MemoryExtractionPipeline>`.
6. All stage interfaces kept `internal` — avoids polluting the public Core API surface.

**Test strategy:**
- `MemoryExtractionPipelineTests.cs` rewritten to test orchestration only (5 focused tests with mocked stages).
- New `ExtractionStageTests.cs` covers single + multi-extractor, all merge strategies, confidence filter, validation, relationship endpoint check.
- New `PersistenceStageTests.cs` covers embedding, upsert, provenance wiring, fault tolerance.

**Result:** 1,066 tests, 0 failures. `MemoryExtractionPipeline` now has 3 constructor deps instead of 14.


- **IEmbeddingOrchestrator** created in `src/Neo4j.AgentMemory.Abstractions/Services/IEmbeddingOrchestrator.cs`; implementation `EmbeddingOrchestrator` in `src/Neo4j.AgentMemory.Core/Services/EmbeddingOrchestrator.cs`
- All 7 Core service/resolver files now use `IEmbeddingOrchestrator` instead of `IEmbeddingGenerator<string, Embedding<float>>` directly
- **ExtractorBase<T>** created in `src/Neo4j.AgentMemory.Core/Extraction/ExtractorBase.cs` — centralizes empty-check and try/catch for all 8 extractors
- **ConversationTextBuilder** created in `src/Neo4j.AgentMemory.Core/Extraction/ConversationTextBuilder.cs` — centralizes `"{role}: {content}"` pattern
- Added `Core` project reference to `Extraction.Llm` and `Extraction.AzureLanguage` csproj files
- All 4 LLM extractors and 4 Azure extractors now extend `ExtractorBase<T>`, implement `ExtractCoreAsync`
- Test files updated: 8 test files now mock `IEmbeddingOrchestrator` instead of `IEmbeddingGenerator`
- `LongTermMemoryService.AddEntityAsync` still composes `text` (name + optional description) and calls `EmbedTextAsync` — preserves exact text composition
- `CompositeEntityResolver` re-embed with aliases still composes `combinedText` and calls `EmbedTextAsync` — preserves exact composition
- DI: `services.TryAddScoped<IEmbeddingOrchestrator, EmbeddingOrchestrator>()` added to `Core/ServiceCollectionExtensions.cs`

### 2025-01-XX: Extraction Package Merge Analysis

**Task:** Evaluate merging Extraction.Llm and Extraction.AzureLanguage packages using IExtractionEngine strategy pattern (per architecture-review-2.md Section 1.3 Change 1).

**Analysis:**
- Thoroughly reviewed both extraction packages (~1,031 LOC total)
- LLM approach: Chat-based with JSON deserialization
- Azure approach: Direct Azure API calls per extractor type
- Measured actual duplication: ~100 LOC / 1,031 LOC = 9.7%
- Architecture review's "95% structural duplication" referred to external structure, not internal logic

**Decision:** DO NOT merge. Actual code duplication is <10%, insufficient to justify new abstraction layer.

**Action Taken:**
- Removed unnecessary Core dependency from Extraction.Llm
- All 1,059 unit tests pass
- Documented decision in .squad/decisions/inbox/roy-extraction-analysis.md

**Key Learning:** "Structural similarity" ≠ "code duplication". Two implementations of the same interface may follow the same pattern without sharing logic. Creating abstractions should be driven by actual code reuse opportunity, not just pattern recognition. The task instructions explicitly allowed for this pragmatic assessment (step 9).

**Impact:** Maintained architectural clarity, avoided unnecessary complexity. If we add more extraction implementations in the future (3+ total), we can revisit consolidation when true patterns emerge.
### 2025-07-15: Gap G3 — Multi-Extractor Pipeline with Merge Strategies

**Task:** Implemented 5 merge strategies (Union, Intersection, Confidence, Cascade, FirstSuccess) and a MultiExtractorPipeline that runs N extractors per type in parallel, merges results via configurable strategy.

**Files Created:**

*Abstractions:*
1. `src/Neo4j.AgentMemory.Abstractions/Domain/Extraction/MergeStrategyType.cs` — enum with 5 strategy types
2. `src/Neo4j.AgentMemory.Abstractions/Services/IMergeStrategy.cs` — generic merge strategy interface

*Core:*
3. `src/Neo4j.AgentMemory.Core/Extraction/MergeStrategies/UnionMergeStrategy.cs` — combine all, dedup by key, highest confidence wins
4. `src/Neo4j.AgentMemory.Core/Extraction/MergeStrategies/IntersectionMergeStrategy.cs` — only keep items found by 2+ extractors
5. `src/Neo4j.AgentMemory.Core/Extraction/MergeStrategies/ConfidenceMergeStrategy.cs` — highest confidence per key
6. `src/Neo4j.AgentMemory.Core/Extraction/MergeStrategies/CascadeMergeStrategy.cs` — first non-empty result list
7. `src/Neo4j.AgentMemory.Core/Extraction/MergeStrategies/FirstSuccessMergeStrategy.cs` — error-tolerant cascade
8. `src/Neo4j.AgentMemory.Core/Extraction/MergeStrategies/MergeStrategyFactory.cs` — static factory for all 4 extraction types × 5 strategies
9. `src/Neo4j.AgentMemory.Core/Services/MultiExtractorPipeline.cs` — IMemoryExtractionPipeline that accepts IEnumerable<IXxxExtractor>, runs in parallel, merges

*Modified:*
10. `src/Neo4j.AgentMemory.Abstractions/Options/ExtractionOptions.cs` — added `MergeStrategy` property (default: Union)

*Tests (72 new tests):*
11. `tests/.../Extraction/MergeStrategies/UnionMergeStrategyTests.cs` — 8 tests
12. `tests/.../Extraction/MergeStrategies/IntersectionMergeStrategyTests.cs` — 8 tests
13. `tests/.../Extraction/MergeStrategies/ConfidenceMergeStrategyTests.cs` — 7 tests
14. `tests/.../Extraction/MergeStrategies/CascadeMergeStrategyTests.cs` — 6 tests
15. `tests/.../Extraction/MergeStrategies/FirstSuccessMergeStrategyTests.cs` — 6 tests
16. `tests/.../Extraction/MergeStrategies/MergeStrategyFactoryTests.cs` — 23 tests (Theory × 4 factory methods + edge cases)
17. `tests/.../Extraction/MultiExtractorPipelineTests.cs` — 14 tests (all strategies, error handling, type filtering, metadata)

**Key Design Decisions:**
1. **Generic merge strategies with key+confidence selectors** — `UnionMergeStrategy<T>` accepts `Func<T, string>` key and `Func<T, double>` confidence selectors rather than type-specific implementations. Factory wires correct selectors per extraction type.
2. **Existing MemoryExtractionPipeline unchanged** — MultiExtractorPipeline is a new class alongside the existing single-extractor pipeline. Both implement `IMemoryExtractionPipeline`; DI registration determines which is used.
3. **Confidence property already existed** — All 4 extraction models (ExtractedEntity, ExtractedFact, ExtractedPreference, ExtractedRelationship) already had `double Confidence { get; init; } = 1.0`, so no model changes needed.
4. **Case-insensitive dedup keys** — Entity by Name, Fact by SPO triple, Preference by PreferenceText, Relationship by (source, type, target). All use `StringComparer.OrdinalIgnoreCase`.

**Build Outcome:** `dotnet build` — 0 new errors (32 pre-existing CS1591 in Exceptions)
**Test Outcome:** 72 new tests pass, 0 failures

---

### 2025-07-14: Phase 2 Sprint 1 — Entity Resolution Chain + Entity Validation

**Task:** Implemented the full entity resolution pipeline for Phase 2 as specified in the charter. All tasks 1–7 completed.

**Files Created:**

*Abstractions:*
1. `src/Neo4j.AgentMemory.Abstractions/Domain/Extraction/EntityResolutionResult.cs` — sealed record with ResolvedEntity, MatchType ("exact"/"fuzzy"/"semantic"/"new"), Confidence, MergedFromEntityId, SourceMessageIds
2. `src/Neo4j.AgentMemory.Abstractions/Options/ExtractionOptions.cs` — ExtractionOptions, EntityResolutionOptions, EntityValidationOptions
3. `src/Neo4j.AgentMemory.Abstractions/Options/MemoryOptions.cs` — added `ExtractionOptions Extraction { get; init; }` property

*Core:*
4. `src/Neo4j.AgentMemory.Core/Validation/EntityValidator.cs` — static utility class; ~221 stopwords; IsValid() and ValidateEntities() methods; rejects empty/short/numeric-only/punctuation-only/stopword names
5. `src/Neo4j.AgentMemory.Core/Resolution/IEntityMatcher.cs` — internal interface; MatchType + TryMatchAsync
6. `src/Neo4j.AgentMemory.Core/Resolution/ExactMatchEntityMatcher.cs` — case-insensitive equality on Name, CanonicalName, Aliases; confidence = 1.0
7. `src/Neo4j.AgentMemory.Core/Resolution/FuzzyMatchEntityMatcher.cs` — FuzzySharp.Fuzz.TokenSortRatio; threshold configurable; compares Name + CanonicalName + Aliases
8. `src/Neo4j.AgentMemory.Core/Resolution/SemanticMatchEntityMatcher.cs` — IEmbeddingProvider; cosine similarity helper; skips entities without embeddings
9. `src/Neo4j.AgentMemory.Core/Resolution/CompositeEntityResolver.cs` — IEntityResolver implementation; Exact→Fuzzy→Semantic→New chain; auto-merge >= AutoMergeThreshold; SAME_AS range detection; type-strict filtering via GetByTypeAsync
10. `src/Neo4j.AgentMemory.Core/Properties/AssemblyInfo.cs` — InternalsVisibleTo("Neo4j.AgentMemory.Tests.Unit") for unit testing internal classes
11. `src/Neo4j.AgentMemory.Core/ServiceCollectionExtensions.cs` — updated to register CompositeEntityResolver as IEntityResolver; bridge ExtractionOptions from MemoryOptions; keep StubEntityResolver available for explicit fallback

*Tests (47 new tests):*
12. `tests/.../Validation/EntityValidatorTests.cs` — 32 tests covering stopwords, length, numeric, punctuation, bulk validation
13. `tests/.../Resolution/ExactMatchEntityMatcherTests.cs` — 7 tests
14. `tests/.../Resolution/FuzzyMatchEntityMatcherTests.cs` — 7 tests
15. `tests/.../Resolution/SemanticMatchEntityMatcherTests.cs` — 8 tests including CosineSimilarity helpers
16. `tests/.../Resolution/CompositeEntityResolverTests.cs` — 10 tests covering chain, type filtering, auto-merge, SAME_AS range, FindPotentialDuplicates

**NuGet added:** `FuzzySharp 2.0.2` to Core csproj

**Key Design Decisions:**
1. **EntityResolutionResult is internal to resolution chain** — IEntityResolver still returns `Entity` per the existing contract; EntityResolutionResult is used internally to carry metadata through the Exact→Fuzzy→Semantic chain.
2. **IsNumericOnly requires at least one digit** — Prevents "..." (all dots) from being classified as numeric. Dots/commas/dashes are allowed alongside digits (e.g., "3.14", "1,000", "-5").
3. **GetByTypeAsync used for all candidate fetching** — IEntityRepository already has GetByTypeAsync. Without TypeStrictFiltering, the implementation still uses GetByTypeAsync with the same type as best-effort (a future GetAllAsync would be ideal).
4. **CosineSimilarity is internal static** — Accessed from tests via InternalsVisibleTo rather than making it public. Keeps it testable without polluting the public API.
5. **FuzzySharp threshold is a double (0–1)** but FuzzySharp returns int (0–100); division by 100 converts. The threshold comparison uses `(int)(threshold * 100)` as integer cutoff.

**Build Outcome:** `dotnet build` — 0 errors, 0 warnings
**Test Outcome:** 156 passing (all new tests pass). 7 pre-existing failures in `Neo4jEntityRepositoryExtensionsTests` added by Deckard (concurrent Neo4j adapter work), unrelated to entity resolution.

---



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



---

### 2025-07-15: Write-Layer Gap Audit — Interface Additions + Re-Embedding Fix

**Tasks:** Six tasks from the audit findings. All in Abstractions (contracts) and Core (business logic).

**Task 1 — UpsertBatchAsync on IEntityRepository and IFactRepository:**
- Added Task<IReadOnlyList<Entity>> UpsertBatchAsync(IReadOnlyList<Entity>, CancellationToken) to IEntityRepository
- Added Task<IReadOnlyList<Fact>> UpsertBatchAsync(IReadOnlyList<Fact>, CancellationToken) to IFactRepository
- Gaff's Neo4j repos already had real implementations; interface contracts were the gap.

**Task 2 — DeleteAsync on IPreferenceRepository:**
- Added Task DeleteAsync(string preferenceId, CancellationToken) to IPreferenceRepository
- Neo4jPreferenceRepository already had a real Cypher implementation.

**Task 3 — Cross-memory relationship methods:**
- Added CreateExtractedFromRelationshipAsync to IEntityRepository, IFactRepository, IPreferenceRepository
- Added CreateAboutRelationshipAsync to IFactRepository and IPreferenceRepository
- All Neo4j repos already had real Cypher implementations from Gaff; again only the interface contracts were missing.

**Task 4 — Fix re-embedding after entity merge in CompositeEntityResolver:**
- Added conditional re-embed: only regenerates embedding when liasesChanged (new aliases were genuinely added)
- Combined text = Name + Aliases (space-joined, trimmed)
- Unconditional re-embed broke the exact-match test (which asserts embedding provider is NOT called on same-name match)

**Task 5 — Wire EXTRACTED_FROM in MemoryExtractionPipeline:**
- After upserting each entity, fact, and preference, now calls CreateExtractedFromRelationshipAsync for every source message ID
- Failures caught and logged as warnings (non-fatal) to maintain pipeline resilience

**Task 6 — DeletePreferenceAsync on ILongTermMemoryService:**
- Added Task DeletePreferenceAsync(string preferenceId, CancellationToken) to ILongTermMemoryService
- Implemented in LongTermMemoryService as delegation to _prefRepo.DeleteAsync

**Build Outcome:** dotnet build — succeeded, 0 errors, 0 warnings  
**Test Outcome:** dotnet test — 419/419 passed ✅

**Key Learnings:**
- Always run a full dotnet build (not --no-restore) after adding interface members. Cached compiled assemblies can hide CS0535 errors during incremental builds.
- Gaff had pre-implemented many Neo4j-side methods before the interface contracts were formally declared. The write-layer "gaps" were mostly interface contract gaps, not missing Neo4j Cypher.
- Conditional re-embedding (only when aliases change) is the correct semantic: if an exact-name match merges with no new alias added, the embedding remains valid and regenerating it is wasteful and breaks existing test expectations.
- MemoryExtractionPipeline already held both repository references and sourceMessageIds, so wiring EXTRACTED_FROM was purely additive — no new dependencies required.

**Artifacts:**
- .squad/decisions/inbox/roy-interface-additions.md — decision record for all 6 tasks

---

### 2025-07-16: Exhaustive Gap Hunt — Python agent-memory vs .NET agent-memory-dotnet

**Task:** Performed a full method-by-method, file-by-file comparison between the Python `neo4j-agent-memory` and our .NET implementation. Read every Python source file in `Neo4j/agent-memory/src/neo4j_agent_memory/` and every .NET source file in `src/`.

**Findings Summary:** 42 total gaps — 5 Critical, 16 Major, 21 Minor. 14 .NET advantages identified.

**Critical Gaps (must address):**
1. **No observational memory / context compression** — Python has a 3-tier `MemoryObserver` (reflections → observations → recent messages) that compresses context at 30,000 tokens. .NET has no equivalent; long sessions will hit LLM context limits.
2. **No MCP prompts or agent instructions** — Python provides 3 MCP prompt templates (`memory-conversation`, `memory-reasoning`, `memory-review`) and detailed system instruction sets. .NET MCP server has none.
3. **No geospatial storage/search** — .NET `NominatimGeocodingService` exists but never persists coordinates on Entity nodes. Python stores `location = point(...)`, creates `entity_location_idx`, and has spatial Cypher queries (`SEARCH_LOCATIONS_NEAR`, `SEARCH_LOCATIONS_IN_BOUNDING_BOX`).
4. **No pattern-based preference detection** — Python `PreferenceDetector` uses regex across 7 categories (food, music, tech, entertainment, travel, communication, work). .NET relies entirely on LLM extraction.
5. **No retroactive extraction** — Python: `extract_entities_from_session()`, `generate_embeddings_batch()`. .NET cannot process historical data.

**Major Gaps:**
- No batch progress callbacks (`on_progress`, `on_batch_complete`) in message/entity batch operations
- No metadata filter operators ($eq, $ne, $gt, $in, etc.) on message search
- No single-message delete (only `ClearSessionAsync`)
- No conversation summarization
- No entity/fact deletion (IEntityRepository, IFactRepository have no DeleteAsync)
- No spaCy extractor, no GLiNER extractor — .NET only has LLM + Azure Language
- No extraction merge strategies (UNION/INTERSECTION/CONFIDENCE/CASCADE)
- Google Maps geocoding support (Python) vs Nominatim-only (.NET)

**Key Cypher Differences Found:**
- Message creation: Python is single atomic query; .NET uses 3 separate queries (less atomic)
- Entity upsert: Python MERGE on `{name, type}` (natural dedup); .NET MERGE on `{id}` (requires pre-resolved ID). Python uses COALESCE on MATCH; .NET overwrites all
- Entity relationships: Python `RELATED_TO` / .NET `RELATES_TO` — schema mismatch
- ToolCall creation: Python single atomic query with success/failure/duration stats; .NET uses 2 queries, only totalCalls tracked
- Tool relationships: Python `USES_TOOL`/`INSTANCE_OF` vs .NET `USED_TOOL`/`CALLS` — schema mismatch

**Configuration Differences:**
- Default message limit: Python 50 vs .NET 10 (5x difference)
- Max connection pool: Python 50 vs .NET 100
- .NET has hardcoded default password `"password"` — security concern
- Python `gpt-4o-mini` as default LLM; .NET leaves ModelId empty

**Schema Incompatibilities (same Neo4j instance won't work for both):**
- Relationship type names differ: `RELATED_TO` vs `RELATES_TO`, `USES_TOOL` vs `USED_TOOL`, `INSTANCE_OF` vs `CALLS`
- Property naming: snake_case (Python) vs camelCase (.NET) throughout

**.NET Advantages over Python:**
- OpenTelemetry metrics (7 counters + 5 histograms) and activity tracing
- GraphRAG adapter with Vector/Fulltext/Hybrid/Graph search modes
- Azure Language Services extraction
- Agent Framework integration (Microsoft.Extensions.AI)
- 3 fulltext indexes (Python has none)
- ReasoningStep vector index (Python only has task-level)
- ContextBudget with 4 truncation strategies
- `memory_find_duplicates` and `extract_and_persist` MCP tools
- Conversation-level HAS_FACT and HAS_PREFERENCE relationships
- Fact `Category` property

**Artifacts:**
- `.squad/decisions/inbox/roy-gap-hunt-results.md` — 400-line detailed report with per-category tables, Cypher comparisons, configuration diff table

---

### 2026-07: Architecture Assessment & Ecosystem Strategy

**Task:** Full architecture audit of all 10 src projects + ecosystem strategy for .NET AI frameworks.

**Key Findings:**

1. **Architecture is clean** — Zero circular dependencies, zero boundary violations, zero framework leakage. Abstractions has zero external dependencies. Core has zero Neo4j/MAF imports. All dependency arrows flow strictly downward through the 5-layer stack.

2. **Cross-reference matrix verified** — All 10 src projects examined. No project references anything it shouldn't. The ports-and-adapters pattern is consistently applied across all layers.

3. **Issue A1: neo4j-maf-provider ProjectReference** — GraphRagAdapter references neo4j-maf-provider via source checkout (ProjectReference), blocking NuGet publishing. Must become PackageReference when upstream publishes to NuGet.

4. **Issue A2: net8.0/net9.0 asymmetry** — neo4j-maf-provider targets net8.0 while all our projects target net9.0. Works via backward compat but worth tracking.

5. **McpServer is properly thin** — References only Abstractions (not Core or Neo4j). Tools resolve services at runtime via DI. Host applications wire the full stack.

6. **Ecosystem strategy: Semantic Kernel adapter is highest-impact new work** — SK has the largest .NET AI user base. Our architecture already supports building a thin SK adapter identical in pattern to AgentFramework.

7. **M.E.AI bridge is lowest-effort, highest-value integration** — Single adapter class bridges IEmbeddingGenerator to IEmbeddingProvider. Benefits all consumers regardless of framework.

8. **AutoGen and LangChain.NET are defer** — Too experimental (AutoGen) or too niche (LangChain.NET) to justify investment.

**Artifacts:**
- `docs/architecture-assessment.md` — Full architecture diagram, cross-reference matrix, boundary analysis, ecosystem strategy with 7 prioritized recommendations
- `.squad/decisions/inbox/roy-architecture-assessment.md` — Decision proposals for team review


---

### 2026-07: Gap G5 — Background Enrichment Queue

**Task:** Implemented async, non-blocking background enrichment queue to decouple enrichment from the extraction pipeline.

**What was built:**
1. IBackgroundEnrichmentQueue interface in Abstractions/Services/ — EnqueueAsync, EnqueueBatchAsync, QueueDepth, IsProcessing
2. EnrichmentQueueOptions record in Abstractions/Options/ — MaxConcurrency=3, MaxRetries=2, RetryDelay=5s, MaxQueueCapacity=1000, Enabled=true
3. BackgroundEnrichmentQueue class in Core/Enrichment/ — full implementation
4. EnrichmentItem internal record in same file — (string EntityId, int RetryCount = 0)
5. 20 unit tests in Tests.Unit/Enrichment/BackgroundEnrichmentQueueTests.cs

**Architecture decisions:**
- Used System.Threading.Channels.Channel<T> (BCL, no extra NuGet) with BoundedChannelFullMode.DropOldest
- Used a fixed pool of MaxConcurrency worker Tasks (Task.Run) instead of IHostedService to keep the implementation framework-agnostic
- Workers independently compete to read from the channel — items stay in the channel until a worker is free, making QueueDepth = channel.Reader.Count accurate
- Retry: failed items (all providers return null or throw) are re-queued with incremented RetryCount; dropped when RetryCount >= MaxRetries
- Supports multiple IEnrichmentService providers: all are called per entity, any success triggers UpsertAsync
- Implements both IDisposable and IAsyncDisposable; DisposeAsync awaits the worker pool with a 5s timeout

**Key Learnings:**
- With DropOldest BoundedChannel, TryWrite always succeeds (drops oldest when full). This keeps EnqueueAsync truly non-blocking.
- Worker-per-concurrency pattern (vs single reader loop + semaphore) keeps items in the channel until processing starts, making QueueDepth a reliable observable.
- When NSubstitute Returns lambda throws without returning a value, the compiler can't infer T. Use Task.FromException<T>() to make return type explicit and avoid CS0121 ambiguity.
- FluentAssertions 8.x uses BeLessThanOrEqualTo / BeGreaterThanOrEqualTo (not BeLessOrEqualTo / BeGreaterOrEqualTo).
- Tests for IsProcessing_FalseAfterProcessingCompletes must use MaxRetries=0 or a successful service; a null result triggers retry and the test will timeout waiting for IsProcessing=false with default RetryDelay=5s.

**Build Outcome:** dotnet build — 0 errors, 0 warnings
**Test Outcome:** dotnet test — 867/867 passed ✅ (20 new tests added)

---

### 2026-04-14: Gap G7 — Streaming Extraction Pipeline

**Task:** Ported the Python streaming.py module to .NET, implementing a full streaming extraction pipeline for processing long documents efficiently with chunking, overlap, and deduplication.

**Files Created:**

*Abstractions:*
1. `src/Neo4j.AgentMemory.Abstractions/Domain/Extraction/Streaming/ChunkInfo.cs` — sealed record with Index, StartChar, EndChar, Text, IsFirst, IsLast; computed CharCount and ApproxTokenCount (\S+ pattern)
2. `src/Neo4j.AgentMemory.Abstractions/Domain/Extraction/Streaming/StreamingChunkResult.cs` — sealed record with Chunk, Result, Success, Error, DurationMs; computed EntityCount, RelationCount
3. `src/Neo4j.AgentMemory.Abstractions/Domain/Extraction/Streaming/StreamingExtractionStats.cs` — sealed record with all aggregate counters
4. `src/Neo4j.AgentMemory.Abstractions/Domain/Extraction/Streaming/StreamingExtractionResult.cs` — sealed record with Entities, Relationships, ChunkResults, Stats; ToExtractionResult() method
5. `src/Neo4j.AgentMemory.Abstractions/Options/StreamingExtractionOptions.cs` — char/token defaults, ChunkByTokens flag, SplitOnSentences, ForTokens() factory
6. `src/Neo4j.AgentMemory.Abstractions/Services/IStreamingExtractor.cs` — ChunkDocument, ExtractStreamingAsync (IAsyncEnumerable), ExtractAsync

*Core:*
7. `src/Neo4j.AgentMemory.Core/Extraction/Streaming/TextChunker.cs` — internal static class; ChunkByChars (sentence-boundary aware) and ChunkByTokens (\S+ tokeniser) matching Python logic exactly
8. `src/Neo4j.AgentMemory.Core/Extraction/Streaming/EntityDeduplicator.cs` — internal static class; DeduplicateEntities (name+type key, highest confidence wins) and DeduplicateRelationships (source+type+target, case-insensitive)
9. `src/Neo4j.AgentMemory.Core/Extraction/Streaming/StreamingExtractor.cs` — IStreamingExtractor implementation; IAsyncEnumerable with [EnumeratorCancellation]; per-chunk error isolation; Stopwatch timing; wraps chunk text in a synthetic Message for IEntityExtractor

*Tests (54 new tests, all passing):*
10. `tests/.../Extraction/Streaming/TextChunkerTests.cs` — 16 tests: empty, short, multi-chunk, sentence boundary, overlap, token-based, unicode, 1M chars, no-spaces
11. `tests/.../Extraction/Streaming/EntityDeduplicatorTests.cs` — 10 tests: empty, no-dupes, with-dupes, same-name-different-types, case-insensitive, relationship dedup
12. `tests/.../Extraction/Streaming/StreamingExtractorTests.cs` — 12 tests: single chunk, multi-chunk streaming, error isolation, sequential indices, dedup, stats accuracy, ToExtractionResult
13. `tests/.../Extraction/Streaming/StreamingChunkResultTests.cs` — 4 tests: property accessors, defaults
14. `tests/.../Extraction/Streaming/StreamingExtractionResultTests.cs` — 4 tests: ToExtractionResult, empty collections, stats defaults
15. `tests/.../Extraction/Streaming/StreamingExtractionOptionsTests.cs` — 6 tests: default values, constant values, ForTokens factory

**Key Design Decisions:**
1. **IEntityExtractor wrapping** — IEntityExtractor takes IReadOnlyList<Message>, not raw text. StreamingExtractor synthesises a Message with a stable MessageId (GUID), ConversationId="streaming", SessionId="streaming" per chunk. This is clean and doesn't require interface changes.
2. **IAsyncEnumerable with [EnumeratorCancellation]** — Proper .NET async streaming pattern. Errors on individual chunks are caught, logged, and emitted as failed StreamingChunkResult rather than propagating — matching Python's per-chunk error isolation.
3. **Internal static helpers** — TextChunker and EntityDeduplicator are internal static classes accessible to tests via InternalsVisibleTo. Keeps the public surface minimal.
4. **No IRelationshipExtractor integration** — IStreamingExtractor only wraps IEntityExtractor by design. Relationship extraction is separate concern; the ExtractionResult.Relationships collection remains empty unless the extractor populates it.
5. **StreamingExtractionResult.Stats uses DeduplicatedEntities for post-dedup count** — TotalEntities = raw across all chunks, DeduplicatedEntities = count after dedup, matching Python's stats structure exactly.

**Build Outcome:** `dotnet build` — 0 new errors
**Test Outcome:** 1003/1003 tests pass (54 new tests for streaming pipeline) ✅

### 2025-07-15: ToolCallStatus Enum Fix — Added Failure and Timeout

**Task:** Added missing ToolCallStatus.Failure and ToolCallStatus.Timeout enum values to achieve Python parity (6 status values total).

**Problem:** Python agent-memory has 6 ToolCallStatus values (pending, success, error, cancelled, failure, timeout), but .NET had only 4 (Pending, Success, Error, Cancelled). This created a dead Cypher branch when tool calls had failure/timeout statuses.

**Files Modified:**
1. `src/Neo4j.AgentMemory.Abstractions/Domain/Reasoning/ToolCallStatus.cs` — Added Failure and Timeout enum values with XML docs
2. `src/Neo4j.AgentMemory.Neo4j/Repositories/Neo4jToolCallRepository.cs` — Updated Cypher query to count 'failure' in failed_calls: `CASE WHEN $status IN ['error', 'failure', 'timeout']`
3. `src/Neo4j.AgentMemory.McpServer/Tools/AdvancedMemoryTools.cs` — Updated description parameter to include Failure and Timeout
4. `tests/Neo4j.AgentMemory.Tests.Unit/Repositories/SchemaParityP1Tests.cs` — Updated test assertion to verify new Cypher includes 'failure'

**Key Findings:**
1. **No switch statements on ToolCallStatus** — No dead branches found; all code uses pattern matching or string comparisons after `.ToString().ToLowerInvariant()`
2. **Cypher bug caught** — Line 61 in Neo4jToolCallRepository only counted `['error', 'timeout']` as failed calls. Now includes 'failure'.
3. `docs/schema.md` already documented all 6 status values — Documentation was ahead of implementation.

**Build Outcome:** `dotnet build` — Success, 0 errors
**Test Outcome:** 1,058 unit tests passed (0 failures)
