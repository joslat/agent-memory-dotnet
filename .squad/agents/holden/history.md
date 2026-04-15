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
