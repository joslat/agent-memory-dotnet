# Holden — History

## Project Context
- **Project:** Agent Memory for .NET
- **User:** Jose Luis Latorre Millas
- **Stack:** xUnit, FluentAssertions, Testcontainers, NSubstitute, Docker
- **Role focus:** Test harness, unit/integration/E2E tests, quality gates
- **Test strategy:** Tests alongside implementation, golden datasets, Testcontainers for Neo4j

## Learnings

- `Testcontainers.Neo4j` 4.11.0 `Neo4jBuilder` accepts image in the constructor (`new Neo4jBuilder("neo4j:5.26")`); there is no `WithPassword` method — set password via `WithEnvironment("NEO4J_AUTH", "neo4j/<password>")`.
- `Neo4j.Driver` and `FluentAssertions` both expose `.As<T>()` extension methods causing CS0121 ambiguity — use `global::Neo4j.Driver.ValueExtensions.As<T>(value)` to be explicit.
- Domain models live in `Neo4j.AgentMemory.Abstractions.Domain` namespace. Avoid using fully-qualified names starting with `Neo4j.` at the call site to prevent conflicts with the `Neo4j.Driver` package namespace.
- The `Neo4j.AgentMemory.Neo4j` project (infrastructure) had no `.cs` files when Epic 9 was run — the test harness is built ahead of that implementation.
- `IIdGenerator` uses `GenerateId()` (not `NewId()`); `IEmbeddingProvider` uses `GenerateEmbeddingAsync(string, CancellationToken)` (not `GenerateAsync()`). Always verify abstract method names against the interface before mocking.
- `ReasoningMemoryService` does NOT take `IOptions<ReasoningMemoryOptions>` in its constructor (Roy's Wave 4 implementation). Budget enforcement logic lives in `MemoryContextAssembler`, not in individual services.
- `MemoryService` constructor order: `(shortTerm, assembler, extraction, IOptions<MemoryOptions>, clock, idGenerator, logger)` — options come before clock/idGenerator.
- `MemoryContextAssembler` uses character-based estimation (`EstimateItemChars`) for budget enforcement: messages use `Content.Length`, facts use `Subject+Predicate+Object+4`, entities use `Name+Description+10`, traces use `Task+Outcome+10`.
- For `TruncationStrategy.OldestFirst`, items are sorted descending by timestamp THEN `FitWithinBudget` removes from the end of each list in round-robin (facts first, then entities, relevant messages, traces, preferences, recent messages).
- `StopWordFilter` in `Neo4j.AgentMemory.GraphRagAdapter.Internal` is `internal` but exposed to tests via `<InternalsVisibleTo Include="Neo4j.AgentMemory.Tests.Unit" />` in the csproj (not via AssemblyInfo.cs).
- `EnrichmentResult` record has `EntityName`, `Summary`, `Description`, `WikipediaUrl`, `ImageUrl`, `Properties`, `Provider`, `RetrievedAtUtc` — no `EntityType` field.
- `Neo4jGraphQueryService.ReadAsync<T>` uses the full generic type `IReadOnlyList<IReadOnlyDictionary<string, object?>>` in the NSubstitute mock setup — Arg matching requires exact type match.
- When `dotnet test` fails with MSB3492 cache file error, run with `--no-build` after a successful `dotnet build` to work around the stale cache check.

- `WriteAsync<T>` vs `WriteAsync` (void): NSubstitute must match the exact generic overload. Void-returning repository methods (`CreateXxxRelationshipAsync`, `DeleteAsync`) use `WriteAsync(Func<IAsyncQueryRunner, Task>)`. Data-returning batch methods (`UpsertBatchAsync`) use `WriteAsync<List<T>>(...)` — T is inferred as `List<T>` (not `IReadOnlyList<T>`) because the lambda ends with `.ToList()`.
- Repository `UpsertBatchAsync` methods return early (skip WriteAsync entirely) when the input collection is empty — test this case without any mock setup.
- Testing `protected override` methods on abstract base class adapters (e.g., `StoreAIContextAsync` on `AIContextProvider`): extract the core logic into an `internal` method (e.g., `PerformStoreAsync`) and call it from the override. This mirrors the `BuildContextAsync` pattern already used for `ProvideAIContextAsync`.
- `CompositeEntityResolver` re-embedding logic: when aliases change after merge, `GenerateEmbeddingAsync` is called a second time with `"{name} {alias1} {alias2}"` combined text. The total call count for `GenerateEmbeddingAsync` in a SemanticMatch path is 2 (one for the semantic query, one for re-embedding) when aliases change; 1 when they don't.
- `MemoryExtractionPipeline.ExtractAsync` wraps each `CreateExtractedFromRelationshipAsync` call in try/catch — failures are logged and don't abort extraction. Tests for fault tolerance should verify the extraction result is still returned after repository exceptions.
- For `AdvancedMemoryTools.MemoryExportGraph` and `MemoryFindDuplicates`: when `EnableGraphQuery = false`, they throw `McpException` before calling any service. Match `.WithMessage("*EnableGraphQuery*")`.
- `CypherQueryRegistry.GetAll()` uses `IsLiteral` reflection to find `const string` fields — `static` method fields (e.g., `DecayQueries.UpdateAccessTimestamp`) and `static readonly string[]` arrays (e.g., `SchemaQueries.Constraints`) are NOT included. The current catalog has exactly 139 const string entries.
- `[CallerFilePath]` in a static property initializer works by calling a helper method (`private static string Foo([CallerFilePath] string? src = null) => ...`) — the compiler substitutes the source file path at the call site at compile time, enabling snapshot files to live alongside test source files and be committed to git.
- Stale incremental build cache (`error CS1591`) can appear on the first `dotnet build` after switching branches. Run `dotnet clean` then `dotnet build` to get accurate errors.

## Work Log

### 2025-01-28 — Epic 9: Test Harness Bootstrap

**Completed:**
- Created `tests/Neo4j.AgentMemory.Tests.Integration/Neo4jTestFixture.cs` — shared `IAsyncLifetime` fixture wrapping a `Neo4jContainer` (Testcontainers)
- Created `tests/Neo4j.AgentMemory.Tests.Integration/Neo4jTestCollection.cs` — xUnit `[CollectionDefinition("Neo4j")]` so all integration tests share one container
- Created `tests/Neo4j.AgentMemory.Tests.Integration/TestDataSeeders.cs` — factory methods for all domain types: `Conversation`, `Message`, `Entity`, `Fact`, `Preference`, `Relationship`, `ReasoningTrace`, `ReasoningStep`, `ToolCall`
- Created `tests/Neo4j.AgentMemory.Tests.Integration/IntegrationTestBase.cs` — abstract base with `[Collection("Neo4j")]`, `Fixture` property, `CreateDriver()` and `RunCypherAsync()` helpers
- Created `tests/Neo4j.AgentMemory.Tests.Integration/Neo4jConnectivityTests.cs` — smoke tests: `CanConnectToNeo4j`, `CanCreateAndQueryNode`
- Created `tests/Neo4j.AgentMemory.Tests.Unit/TestHelpers/MockFactory.cs` — `CreateFixedClock`, `CreateSequentialIdGenerator`, `CreateStubEmbeddingProvider` using NSubstitute
- Added explicit `Neo4j.Driver Version="6.0.0"` reference to integration test project
- `dotnet build` — **Build succeeded** (0 errors)

### 2025-01-28 — Wave 4: Core Service Unit Tests

**Completed:**
- Created `tests/Neo4j.AgentMemory.Tests.Unit/Services/` directory
- Created `ShortTermMemoryServiceTests.cs` — 12 tests covering conversation creation, embedding generation/skipping, message persistence, limit capping, score stripping, session clearing
- Created `LongTermMemoryServiceTests.cs` — 14 tests covering entity/fact/preference/relationship add+search, embedding conditional generation, score stripping for all search methods
- Created `ReasoningMemoryServiceTests.cs` — 10 tests covering trace start, step add, tool call record, trace completion, parallel GetTraceWithSteps, list and search with score stripping
- Created `MemoryContextAssemblerTests.cs` — 10 tests covering embedding generation, all-layer retrieval, GraphRAG enable/disable/null, assembled timestamp, budget enforcement (OldestFirst + LowestScoreFirst), token count estimation
- Created `MemoryServiceTests.cs` — 5 tests covering recall wrapping, message creation via IIdGenerator+IClock, batch delegate, extraction pipeline delegate, session clear delegate
- **Total unit tests: 85 passing (0 failures)**

### 2025-07-16 — Test Gap Analysis & New Unit Tests

**Baseline:** 398 unit tests across 48 test classes.

**Gap analysis completed** — inventoried all 120+ source files against test coverage. Unit-testable gaps found and filled:

- Created `GraphRagAdapter/StopWordFilterTests.cs` — 8 tests for the static keyword extractor (stop word removal, case insensitivity, single-char filtering, empty input)
- Created `Enrichment/CachedEnrichmentServiceTests.cs` — 5 tests (cache miss delegates, cache hit short-circuits, null not cached, separate keys per entity type, case-insensitive key normalisation)
- Created `Infrastructure/MigrationRunnerTests.cs` — 3 tests (no-folder path: no DB calls, no exception, pre-cancelled token)
- Created `Services/Neo4jGraphQueryServiceTests.cs` — 5 tests (cypher forwarding, null params → empty dict, param forwarding, null param values, empty result set)

**Result: 419 unit tests, 0 failures** (+21 new tests)

### 2025-07-16 — Test Gap Analysis & New Unit Tests

**Baseline:** 398 unit tests across 48 test classes.

**Gap analysis completed** — inventoried all 120+ source files against test coverage. Unit-testable gaps found and filled:

- Created `GraphRagAdapter/StopWordFilterTests.cs` — 8 tests for the static keyword extractor (stop word removal, case insensitivity, single-char filtering, empty input)
- Created `Enrichment/CachedEnrichmentServiceTests.cs` — 5 tests (cache miss delegates, cache hit short-circuits, null not cached, separate keys per entity type, case-insensitive key normalisation)
- Created `Infrastructure/MigrationRunnerTests.cs` — 3 tests (no-folder path: no DB calls, no exception, pre-cancelled token)
- Created `Services/Neo4jGraphQueryServiceTests.cs` — 5 tests (cypher forwarding, null params → empty dict, param forwarding, null param values, empty result set)

**Result: 419 unit tests, 0 failures** (+21 new tests)

**Remaining gaps requiring integration tests (need live Neo4j):**
- All 9 `Neo4j.AgentMemory.Neo4j` repositories (Conversation, Message, Entity, Fact, Preference, Relationship, ReasoningTrace, ReasoningStep, ToolCall)
- `Neo4jGraphQueryService` full record-mapping (INode/IRelationship/IPath ConvertValue logic)
- `AdapterVectorRetriever`, `AdapterFulltextRetriever`, `AdapterHybridRetriever` (all hit IDriver directly)
- `Neo4jTransactionRunner`, `Neo4jSessionFactory`, `Neo4jDriverFactory` (infrastructure wiring)

### 2026-04-13 — Test Gap Analysis Consolidation & Integration Test Framework Assessment

**Trigger:** Multi-agent review session (Deckard, Holden, Sebastian) requested assessment of test coverage gaps.

**Test Count Update:** Previous 398 unit tests → confirmed 419 unit tests (+21 from 2025-07-16 session). All passing.

**Integration Test Gap Analysis Results:**

Critical gaps requiring Testcontainers (live Neo4j):

| Category | Classes | Impact |
|----------|---------|--------|
| **Repositories (9)** | ConversationRepository, MessageRepository, EntityRepository, FactRepository, PreferenceRepository, RelationshipRepository, ReasoningTraceRepository, ReasoningStepRepository, ToolCallRepository | HIGH — persistence layer core functionality |
| **Infrastructure (3)** | Neo4jTransactionRunner, Neo4jSessionFactory, Neo4jDriverFactory | MEDIUM — configuration and lifecycle |
| **Services (1 partial)** | Neo4jGraphQueryService (ConvertValue branches: INode/IRelationship/IPath) | MEDIUM — record mapping with real graph data |
| **Retrievers (3)** | AdapterVectorRetriever, AdapterFulltextRetriever, AdapterHybridRetriever | HIGH — GraphRAG integration verification |

**Framework Readiness:** ✅ Complete
- `IntegrationTestBase` abstract class with Fixture property, CreateDriver(), RunCypherAsync()
- `TestDataSeeders` factory methods for all 9 domain types
- `Neo4jTestCollection` collection definition ensuring single container per test run
- `Neo4jConnectivityTests` smoke tests all passing

**Recommendation:** Prioritize repository integration tests (9 classes × ~5 tests each ≈ 45 tests). Framework ready; ~2-week sprint to complete. Highest-confidence test coverage of persistence layer.

**Standing Decision:** Integration test coverage is non-negotiable per Jose's TDD directive ("Tests first before/during implementation"). Current gap is documented and tracked.

### 2026-07-xx — Wave 5 New Feature Tests (Cross-Memory Relationships, Batch, MCP Tools)

**Baseline:** 421 unit tests (from Wave 5 implementation, 0 failures).

**Scope:** Wrote 52 new unit tests covering all new functionality added in the Wave 5 feature wave.

**Test breakdown:**

| File | New Tests | Coverage Focus |
|------|-----------|----------------|
| `Repositories/Neo4jPreferenceRepositoryTests.cs` | 6 | DeleteAsync, CreateAboutRelationshipAsync, CreateExtractedFromRelationshipAsync |
| `Repositories/Neo4jFactRepositoryTests.cs` | 8 | CreateAboutRelationshipAsync, CreateExtractedFromRelationshipAsync, UpsertBatchAsync |
| `Repositories/Neo4jEntityRepositoryBatchTests.cs` | 5 | CreateExtractedFromRelationshipAsync, UpsertBatchAsync |
| `Repositories/Neo4jReasoningTraceRepositoryTests.cs` | 5 | CreateInitiatedByRelationshipAsync, CreateConversationTraceRelationshipsAsync |
| `Repositories/Neo4jToolCallRepositoryTests.cs` | 2 | CreateTriggeredByRelationshipAsync |
| `McpServer/AdvancedMemoryToolsTests.cs` | 12 | memory_record_tool_call, memory_export_graph, memory_find_duplicates, extract_and_persist |
| `AgentFramework/Neo4jMemoryContextProviderTests.cs` | +6 | PerformStoreAsync (AutoExtract behavior) |
| `Resolution/CompositeEntityResolverTests.cs` | +3 | Re-embedding after merge, combined text format |
| `Services/MemoryExtractionPipelineTests.cs` | +4 | EXTRACTED_FROM wiring, fault tolerance |
| `Services/LongTermMemoryServiceTests.cs` | +2 | DeletePreferenceAsync delegation |

**Source change:** `Neo4jMemoryContextProvider.cs` — extracted `PerformStoreAsync` internal method from `StoreAIContextAsync`. Identical behavior, improved testability (mirrors `BuildContextAsync` pattern).

**Final result: 473 unit tests, 0 failures (+52 new tests)**

### 2026-07-xx — G8: Typed Exception Hierarchy + Options Validation Tests

**Baseline:** 623 unit tests (from prior sessions, 0 failures).

**Part 1: Typed Exception Hierarchy**

Created `src/Neo4j.AgentMemory.Abstractions/Exceptions/` with 9 exception classes:

| Exception | Base | Context Property |
|-----------|------|-----------------|
| `MemoryException` | `Exception` | — (base class) |
| `EntityNotFoundException` | `MemoryException` | `EntityId` |
| `FactNotFoundException` | `MemoryException` | `FactId` |
| `SchemaInitializationException` | `MemoryException` | `SchemaOperation` |
| `ExtractionException` | `MemoryException` | `ExtractionStep` |
| `EmbeddingGenerationException` | `MemoryException` | `InputText` |
| `EntityResolutionException` | `MemoryException` | `EntityName` |
| `MemoryConfigurationException` | `MemoryException` | `OptionName` |
| `GraphQueryException` | `MemoryException` | `CypherQuery` |

All exceptions have 3 constructors (message-only, message+context, message+innerException) and full XML documentation (Abstractions project enforces CS1591).

**Part 2: Options Validation Tests**

Created `tests/Neo4j.AgentMemory.Tests.Unit/Options/` with 8 test classes:

| File | Tests | Coverage |
|------|-------|----------|
| `MemoryOptionsTests.cs` | 10 | Nested non-null defaults, EnableAutoExtraction, EnableGraphRag, init overrides |
| `ShortTermMemoryOptionsTests.cs` | 7 | SessionStrategy, GenerateEmbeddings, MessageLimits, all SessionStrategy enum values |
| `LongTermMemoryOptionsTests.cs` | 7 | All embedding flags, EntityResolution, MinConfidenceThreshold range |
| `ExtractionOptionsTests.cs` | 13 | EntityResolution sub-options, EntityValidation sub-options, thresholds, auto-merge ordering |
| `RecallOptionsTests.cs` | 11 | All Max* values, BlendMode, MinSimilarityScore, static Default singleton, positivity |
| `ContextBudgetTests.cs` | 7+4 | Nullable defaults, TruncationStrategy default, static Default, all 4 TruncationStrategy enum values via Theory |
| `ReasoningMemoryOptionsTests.cs` | 5 | GenerateTaskEmbeddings, StoreToolCalls, MaxTracesPerSession nullable |
| `ContextCompressionOptionsTests.cs` | 6 | TokenThreshold, RecentMessageCount, MaxObservations, EnableReflections, full override |

**Part 3: Exception Hierarchy Tests**

Created `tests/Neo4j.AgentMemory.Tests.Unit/Exceptions/MemoryExceptionTests.cs` — 28 tests covering:
- All 9 exception types: construction, message, inner exception, inheritance chain
- Context properties (EntityId, FactId, SchemaOperation, etc.)
- Polymorphic catch-as-MemoryException verification for all 8 derived types

**Namespace note:** Test namespace is `Neo4j.AgentMemory.Tests.Unit.OptionsTests` (not `.Options`) to avoid collision with `Microsoft.Extensions.Options.Options` used via unqualified `Options.Create()` in existing test code (e.g., CompositeEntityResolverTests).

**Final result: 717 unit tests, 0 failures (+94 new tests)**

### 2026-04-14 — G1: Repository Integration Tests

**Baseline:** 717 unit tests (all passing), 2 integration smoke tests (Neo4jConnectivityTests).

**Task:** Create comprehensive Neo4j integration tests for all repositories using Testcontainers.

**New files created:**
- `tests/Neo4j.AgentMemory.Tests.Integration/Fixtures/Neo4jIntegrationFixture.cs` — IAsyncLifetime shared fixture: starts Neo4j 5.26 container, wires DirectSessionFactory → Neo4jTransactionRunner, runs SchemaBootstrapper with 4-dim embeddings, exposes CleanDatabaseAsync() and WaitForVectorIndexesAsync()
- `tests/Neo4j.AgentMemory.Tests.Integration/Fixtures/Neo4jIntegrationCollection.cs` — `[CollectionDefinition("Neo4j Integration")]` for all new tests
- `Repositories/ConversationRepositoryIntegrationTests.cs` — 8 tests: upsert, title persistence, get-by-id round-trip, null miss, session filtering, ordering, delete, upsert-update
- `Repositories/MessageRepositoryIntegrationTests.cs` — 7 tests: add single, embedding persistence, add batch, recent-by-session limit+order, get-by-conversation, vector search, null miss
- `Repositories/EntityRepositoryIntegrationTests.cs` — 10 tests: upsert all props, get-by-id round-trip, null miss, get-by-name, embedding persistence, vector search, batch upsert, delete, CreateExtractedFrom relationship, get-by-type
- `Repositories/FactRepositoryIntegrationTests.cs` — 10 tests: upsert, get-by-id round-trip, null miss, get-by-subject, FindByTriple (case-insensitive), FindByTriple null, vector search, delete, CreateAbout relationship, CreateExtractedFrom relationship
- `Repositories/PreferenceRepositoryIntegrationTests.cs` — 8 tests: upsert, get-by-id round-trip, null miss, delete, get-by-category, CreateAbout relationship, CreateExtractedFrom relationship, vector search
- `Repositories/ReasoningTraceRepositoryIntegrationTests.cs` — 8 tests: add, get-by-id round-trip, null miss, list-by-session with ordering, update outcome/success, vector search, CreateConversationTraceRelationships (HAS_TRACE+IN_SESSION), CreateInitiatedBy
- `Repositories/SchemaBootstrapperIntegrationTests.cs` — 18 tests: Theory for 8 unique constraints, Theory for 3 fulltext indexes, Theory for 5 vector indexes, idempotency (run twice no error), all vector indexes ONLINE

**Total new integration tests: 69**

**Key implementation decisions:**
- `Neo4jIntegrationFixture` uses `DirectSessionFactory` (private nested class) to wire the test container driver to `Neo4jTransactionRunner` without DI
- `EmbeddingDimensions = 4` in fixture options so test embeddings `[float, float, float, float]` match index dimension
- `WaitForVectorIndexesAsync` polls `SHOW INDEXES WHERE type = 'VECTOR' AND state <> 'ONLINE'` until all indexes are ONLINE (up to 60s)
- Each test class implements `IAsyncLifetime.InitializeAsync()` calling `fixture.CleanDatabaseAsync()` for per-test isolation
- All tests tagged `[Trait("Category", "Integration")]` so `dotnet test --filter "Category!=Integration"` skips them (no Docker needed)
- Unit tests remain at 847 passing (0 failures) — filter verified after creation

**Learnings:**
- When using `INeo4jTransactionRunner.ReadAsync<T>` in test helpers, the `runner` callback uses `IAsyncQueryRunner.RunAsync()` which returns `IResultCursor`. `SingleAsync()` is an extension method from `Neo4j.Driver` namespace — the `using Neo4j.Driver;` directive is required in test files that call it directly.
- The `edit` tool replaces the first exact match; including class header in `old_str` with mismatched whitespace will corrupt the file. Use minimal unique context for `old_str`.
- `Neo4jIntegrationFixture` should be a separate xUnit collection from the existing `Neo4jTestFixture` to avoid container sharing with smoke tests.
- Vector indexes in Neo4j start in POPULATING state and must transition to ONLINE before `db.index.vector.queryNodes` works. Always call `WaitForVectorIndexesAsync` in fixture init.

### 2026-07-xx — Comprehensive Test Coverage Audit

**Baseline:** 1058 unit tests (103 files, all passing) + 56 integration tests.

**Audit scope:** All 10 src projects, 222 public types, 103 unit test files, 7 integration test files.

**Key findings:**

- **`Neo4jRelationshipRepository`** — ZERO unit or integration tests. 5 public methods. Critical P1 gap.
- **GraphRAG retrievers** (`AdapterVectorRetriever`, `AdapterFulltextRetriever`, `AdapterHybridRetriever`) — ZERO tests. All `internal` but exposed via `InternalsVisibleTo`. Unit-testable today.
- **4 repos missing integration tests:** `Neo4jReasoningStepRepository`, `Neo4jToolCallRepository`, `Neo4jExtractorRepository`, `Neo4jRelationshipRepository`.
- **`Neo4jEntityRepository.GetByIdAsync` and `GetByNameAsync`** — no unit test (has integration coverage only).
- **~63 weak `.Should().NotBeNull()` sole assertions** in enrichment + observability tests — value-level assertions missing.
- **`SessionIdGeneratorTests`** embeds `DateTime.UtcNow` inline — theoretically flaky at UTC midnight. Fix: inject `IClock`.
- **Coverage estimate:** ~75-94% depending on project; overall solution ~82% by public type.
- **Error path ratio:** 39 exception assertions in 21 files = ~3.7% — low; Core services could use more null-guard / invalid-state tests.

**Deliverable:** `.squad/decisions/inbox/holden-test-audit.md` — full gap report with P1/P2/P3 priorities, summary table, and estimate of ~99 new tests to reach good coverage.

### L9: Test Coverage Audit Across 222 Public Types (2026-04-15)

**Session:** arch-review-session (parallel with Deckard, Joi)

**Scope:** Complete assessment of test coverage across all 10 projects (222 public types).

**Baseline Metrics:**
- 1058 unit tests (103 files)
- 56 integration tests (7 files)
- All tests passing
- Overall coverage: 70–95% across projects

**Coverage Summary by Project:**

| Project | Public Types | Coverage | Tests | Gap |
|---------|---|---|---|---|
| Abstractions | 110 | ~32% | Interfaces not directly tested (by design) | — |
| AgentFramework | 11 | ~82% | ServiceCollectionExtensions untested | 1 |
| Core | 31 | ~90% | ServiceCollectionExtensions untested | 1 |
| Enrichment | 10 | ~90% | ServiceCollectionExtensions untested | 1 |
| Extraction.AzureLanguage | 6 | ~83% | ServiceCollectionExtensions, wrapper untested | 2 |
| Extraction.Llm | 6 | ~83% | ServiceCollectionExtensions untested | 1 |
| GraphRagAdapter | 6 | ~50% | **ALL 3 retrievers untested** | 3 |
| McpServer | 18 | ~94% | ServiceCollectionExtensions untested | 1 |
| Neo4j | 24 | ~75% | **RelationshipRepository (ZERO tests)**, 4 repos missing integration | 5 |
| Observability | 3 | ~100% | ServiceCollectionExtensions untested | 1 |

**Critical Gaps (P1 — Core Functionality Without Tests):**

1. **Neo4jRelationshipRepository — ZERO tests (unit or integration)**
   - 5 public methods: UpsertAsync, GetByIdAsync, GetByEntityAsync, GetBySourceEntityAsync, GetByTargetEntityAsync
   - Relationships are first-class knowledge graph edges
   - Recommended: ~8 unit (Cypher validation) + ~6 integration (round-trip)

2. **GraphRAG Retrievers — ZERO tests**
   - AdapterVectorRetriever, AdapterFulltextRetriever, AdapterHybridRetriever
   - Internal classes exposed via InternalsVisibleTo
   - Core retrieval pipeline for GraphRAG context
   - Recommended: ~6 unit tests per retriever (~18 total)

3. **Neo4jReasoningStepRepository — Missing integration tests**
   - Unit tests exist, integration missing
   - Recommended: ~5 integration tests

4. **Neo4jToolCallRepository — Only 2 relationship tests**
   - 5 public methods, only relationship creation tested
   - Recommended: ~6 integration tests

**Important Gaps (P2 — Significant Features):**

- Neo4jExtractorRepository: 11 unit tests but no integration tests (~5 needed)
- Neo4jEntityRepository: GetByIdAsync/GetByNameAsync lack unit tests (~4 needed)
- Neo4jTransactionRunner/SessionFactory/DriverFactory: No tests (~4 integration needed)
- TextAnalyticsClientWrapper: No tests (~3 integration/contract tests needed)
- Core service error paths: Thin coverage (~15 unit tests needed)

**Test Quality Issues Found:**

| Issue | Severity | Files | Count |
|---|---|---|---|
| .Should().NotBeNull() as sole assertion | Medium | 14 | ~63 |
| DateTime.UtcNow direct in tests | Low | SessionIdGeneratorTests | 2 |
| Bare .NotBeNull() assertions | Medium | Enrichment/Observability | ~20 |
| No behavior tests for metrics (only existence) | Medium | MemoryMetricsTests | 12 |
| Error paths undertested in Core services | Medium | Multiple | — |

**Recommended Additions (Priority Order):**

Total: **~99 new tests** across 15 areas

| # | Area | Tests | Effort |
|---|------|-------|--------|
| 1-2 | Neo4jRelationshipRepository (unit + integration) | ~14 | Low-Medium |
| 3-5 | GraphRAG retrievers (unit) | ~18 | Medium |
| 6-8 | Repo integration tests (Step, ToolCall, Extractor) | ~16 | Medium |
| 9 | Core service error paths | ~15 | Medium |
| 10-15 | DI smoke tests, infra, quality improvements | ~20 | Low |

**Assessment:**

Coverage is good to strong overall. Testcontainers framework is production-ready and fully implemented. Integration test harness requires no changes — only new test classes.

**Highest ROI:** Execute P1 integration tests for the 4 missing Neo4j repositories. These are straightforward round-trip scenarios using existing framework patterns.

**Status:** Audit report ready for Jose review. Test implementation can begin immediately.

### 2026-07-xx — Implementation Status Verification + Doc-vs-Code Consistency Audit

**Build result:** Succeeded, 0 errors, 0 src/ warnings.

**Actual test counts:** 1,407 unit (main project) + 31 unit (SemanticKernel project) = **1,438 total unit tests**, 0 failures.

#### Task 1: Feature Implementation Status

| Feature | Status | Note |
|---------|--------|------|
| Meta-package | ✅ EXISTS | `src/Neo4j.AgentMemory/Neo4j.AgentMemory.csproj` — references Abstractions, Core, Neo4j, Extraction.Llm + MEDI.Abstractions |
| Semantic Kernel adapter | ✅ EXISTS | `Neo4jMemoryPlugin.cs`, `Neo4jTextSearch.cs`, `KernelMemoryExtensions.cs` all present |
| Externalized prompts | ✅ EXISTS | `LlmExtractionOptions` has all 4 `*Prompt` properties; each extractor uses `_options.*Prompt ?? DefaultSystemPrompt` |
| Observability decorators | ✅ EXISTS (exceeds docs) | 7 decorators registered (not 5): Entity/Fact/Preference/Relationship extractors + EnrichmentService + **MemoryService + GraphRagContextSource** |
| Config validation tests | ✅ EXISTS (exceeds docs) | 82 `[Fact]` tests in `ConfigurationValidationTests.cs` (docs claim 60) |
| Temporal retrieval | ✅ EXISTS | `RecallAsOfAsync` in `IMemoryService`, `MemoryService`, and `InstrumentedMemoryService` |
| Memory decay | ✅ EXISTS | `IMemoryDecayService`, `MemoryDecayService`, `DecayQueries`, `TemporalQueries` all confirmed |
| MemoryMetrics per-extractor | ✅ EXISTS | 4 per-extractor duration histograms + 4 per-extractor counters in `MemoryMetrics` |

All 8 features are fully implemented.

#### Task 2: Doc-vs-Code Discrepancies

| Doc | Claim | Reality | Verdict |
|-----|-------|---------|---------|
| `refactoring-plan.md` | "1,438 unit tests (1,407 + 31 SK)" | 1,438 actual | ✅ Accurate |
| `refactoring-plan.md` | All ✅ items complete | Verified by code search | ✅ Accurate |
| `improvement-suggestions.md` | "5 instrumented decorators" | **7 in code** (added InstrumentedMemoryService + InstrumentedGraphRagContextSource) | ⚠️ Understated |
| `improvement-suggestions.md` | "60 tests covering 20 Options classes" | 82 `[Fact]` tests in file | ⚠️ Understated (tests grew) |
| `architecture-review-assessment.md` | "9 packages" | **11 packages** (original 9 + `Neo4j.AgentMemory` meta + `Neo4j.AgentMemory.SemanticKernel`) | ❌ Outdated — predates meta+SK additions |
| `architecture-review-assessment.md` | "1,211 unit tests" | **1,438 actual** | ❌ Outdated by 227 tests |

**Summary:** `refactoring-plan.md` is accurate. `improvement-suggestions.md` understates decorator and test counts (harmless — reality is better). `architecture-review-assessment.md` has stale package count (9→11) and test count (1,211→1,438) — last updated before the meta-package, SemanticKernel, config validation, and Wave 5+ test additions.

### 2026-07-xx — Cypher Snapshot Tests

**Baseline:** 1,438 unit tests (0 failures).

**Task:** Create HotChocolate-inspired snapshot and structural regression tests for all centralized Cypher query constants.

**New files created:**
- `tests/Neo4j.AgentMemory.Tests.Unit/Queries/CypherQuerySnapshotTests.cs` — 558 tests across 6 test methods:
  - `CypherCatalog_MatchesSnapshot` — file-based snapshot (auto-generates `CypherQuerySnapshot.snap` on first run using `[CallerFilePath]`; set `UPDATE_CYPHER_SNAPSHOTS=1` to regenerate intentionally)
  - `CypherQueryInventory_CountMatchesExpected` — asserts exactly 139 const string fields (catches accidental deletions)
  - `CypherQuery_StartsWithValidKeyword` × 139 — regex check for leading Cypher keyword
  - `CypherQuery_UsesParameterizedValues_WhenWhereOrSetPresent` × 139 — asserts `$param` syntax in WHERE/SET clauses (DDL exempt)
  - `CypherQuery_OnlyReferencesKnownNodeLabels` × 139 — validates labels against domain schema allowlist
  - `CypherQuery_HasBalancedParentheses` × 139 — structural parenthesis balance check
- `tests/Neo4j.AgentMemory.Tests.Unit/Queries/CypherQuerySnapshot.snap` — committed baseline of all 139 query strings (sorted alphabetically)

**Final result: 2,009 unit tests, 0 failures (+571 new tests)**

**Update workflow:** If a Cypher query is intentionally changed, run `UPDATE_CYPHER_SNAPSHOTS=1 dotnet test --filter "CypherCatalog_MatchesSnapshot"` and commit the updated `.snap` file together with the query change.

