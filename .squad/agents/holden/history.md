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
