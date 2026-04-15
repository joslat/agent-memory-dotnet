# Holden — Test Coverage Audit Report

**Date:** 2026-07-xx  
**Requested by:** Jose Luis Latorre Millas  
**Auditor:** Holden (Testing & Harness Engineer)  
**Baseline:** 1058 unit tests (103 files) + 56 integration tests (7 files). All passing.

---

## Summary Table

| Project | Public Types | Tested (unit) | Coverage % | Test Files | Critical Gaps |
|---------|-------------|---------------|------------|------------|---------------|
| **Abstractions** | 110 | ~35 (options+exceptions+domain models) | ~32% | 20+ (shared across areas) | Interfaces not directly tested (by design); some domain model records untested |
| **AgentFramework** | 11 | 9 | ~82% | 6 | `ContextFormatOptions`, `AgentFrameworkOptions`, `ServiceCollectionExtensions` untested |
| **Core** | 31 | 28 | ~90% | 18 | `ServiceCollectionExtensions` untested |
| **Enrichment** | 10 | 9 | ~90% | 8 | `ServiceCollectionExtensions` untested |
| **Extraction.AzureLanguage** | 6 | 5 | ~83% | 4 | `ServiceCollectionExtensions`, `TextAnalyticsClientWrapper` untested |
| **Extraction.Llm** | 6 | 5 | ~83% | 4 | `ServiceCollectionExtensions` untested |
| **GraphRagAdapter** | 3 pub + 3 internal | 2 public, 1 internal | ~50% | 2 | `AdapterVectorRetriever`, `AdapterFulltextRetriever`, `AdapterHybridRetriever` — **ZERO tests** |
| **McpServer** | 18 | 17 | ~94% | 11 | `ServiceCollectionExtensions` untested |
| **Neo4j** | 24 | 18 unit + 6 integration | ~75% | 15 unit + 7 integration | `Neo4jRelationshipRepository` (NO unit/integration), 4 repos missing integration tests |
| **Observability** | 3 | 3 | ~100% | 3 | `ServiceCollectionExtensions` untested |

---

## Prioritized Gap List

### P1 — Critical (core functionality without tests)

#### P1-A: `Neo4jRelationshipRepository` — No tests at all (unit or integration)
- **5 public methods:** `UpsertAsync`, `GetByIdAsync`, `GetByEntityAsync`, `GetBySourceEntityAsync`, `GetByTargetEntityAsync`
- **Why critical:** Relationships are first-class knowledge graph edges. This is persistence-layer code for cross-entity connections — queried heavily by GraphRAG context assembly.
- **Recommended unit tests:** ~8 (cypher/parameter validation per method)
- **Recommended integration tests:** ~6 (round-trip upsert+get, by-entity queries, missing returns null)

#### P1-B: `AdapterVectorRetriever`, `AdapterFulltextRetriever`, `AdapterHybridRetriever` — No tests at all
- Classes are `internal` in `Neo4j.AgentMemory.GraphRagAdapter.Internal` and **exposed via InternalsVisibleTo** to the unit test project.
- These classes ARE unit-testable with mock `IDriver`/`IAsyncSession` via NSubstitute.
- **Why critical:** These are the retrieval pipeline — the entire GraphRAG context source depends on them. Currently `Neo4jGraphRagContextSourceTests` tests the orchestration layer but not the retriever implementations themselves.
- **Recommended unit tests:** ~6 per retriever = ~18 total (correct Cypher construction, parameter passing, empty result handling)

#### P1-C: `Neo4jReasoningStepRepository` — No integration tests
- 3 public methods: `AddAsync`, `GetByTraceAsync`, `GetByIdAsync`
- Unit tests exist for `CreateTriggeredByRelationshipAsync` (via Neo4jToolCallRepositoryTests) but the step repo itself has no integration coverage.
- **Recommended integration tests:** ~5 (add, get-by-trace ordering, get-by-id round-trip, null miss, filtering by trace)

#### P1-D: `Neo4jToolCallRepository` — No integration tests
- 5 public methods: `AddAsync`, `UpdateAsync`, `GetByStepAsync`, `GetByIdAsync`, `CreateTriggeredByRelationshipAsync`
- Unit test covers only `CreateTriggeredByRelationshipAsync` (2 tests).
- **Recommended integration tests:** ~6 (add, update status, get-by-step, get-by-id, null miss, relationship creation)

### P2 — Important (significant features missing tests)

#### P2-A: `Neo4jExtractorRepository` — No integration tests
- 4 public methods: `UpsertAsync`, `GetByNameAsync`, `ListAsync`, `CreateExtractedByRelationshipAsync`
- Unit tests cover all 4 methods (11 tests in `Neo4jExtractorRepositoryTests.cs`).
- Missing: live database round-trip validation.
- **Recommended integration tests:** ~5 (upsert+get by name round-trip, list all, upsert idempotency, EXTRACTED_BY relationship creation)

#### P2-B: `Neo4jEntityRepository.GetByIdAsync` and `GetByNameAsync` — No unit tests
- Both have integration test coverage but no Cypher-level unit validation.
- Risk: Cypher regression not caught until integration run.
- **Recommended unit tests:** 4 (correct Cypher, parameter passing for each; null miss behavior)

#### P2-C: `Neo4jTransactionRunner`, `Neo4jSessionFactory`, `Neo4jDriverFactory` — No tests at all
- Core infrastructure wiring. Currently untested entirely (noted in previous audits).
- Unit testing is difficult due to Neo4j driver dependency — integration test is the right approach.
- **Recommended integration tests:** ~4 (session open/close lifecycle, transaction read/write, factory creates valid driver from options, connection failure throws typed exception)

#### P2-D: `TextAnalyticsClientWrapper` (`Extraction.AzureLanguage`) — No tests
- The concrete wrapper around Azure Cognitive Services client is `internal` but untested.
- `ITextAnalyticsClientWrapper` interface is mocked in extractor tests — the real implementation is dark.
- **Recommended:** Integration/contract test against Azure test endpoint, or characterization unit test with HttpMessageHandler mock.
- **Estimated:** ~3 tests (happy path delegating to real client, handles API error, handles empty response)

#### P2-E: Missing error-path tests for Core services
- `LongTermMemoryService`, `ShortTermMemoryService`, `ReasoningMemoryService`, `MemoryExtractionPipeline` — exception/error coverage exists but is thin.
- Currently **39 exception assertions** across 21 files for 1058 tests = **3.7% error-path ratio** — low for critical services.
- Missing: null argument guards, invalid state transitions (e.g., completing a trace that doesn't exist), embedding generation failures propagating correctly as `EmbeddingGenerationException`.
- **Recommended unit tests:** ~15 across the core service layer

### P3 — Nice-to-have (edge cases, error paths, configuration)

#### P3-A: `ContextFormatOptions` and `AgentFrameworkOptions` — No dedicated tests
- Both are options POCOs used by the AgentFramework. They appear in other tests but have no direct validation of defaults or validation logic.
- **Recommended unit tests:** ~4 combined (default values, override)

#### P3-B: `ServiceCollectionExtensions` across all projects — Not tested
- Present in: Abstractions (schema), AgentFramework, Core, Enrichment, Extraction.AzureLanguage, Extraction.Llm, GraphRagAdapter, McpServer, Observability (9 projects).
- DI registration smoke tests are low-effort and catch misconfiguration bugs early.
- **Recommended:** ~2 DI smoke tests per project = ~18 tests total (can use `IServiceCollection.BuildServiceProvider()` to verify service resolution)

#### P3-C: Weak assertion quality in enrichment and observability tests
- **DiffbotEnrichmentServiceTests**: 9 tests that only assert `.Should().NotBeNull()` after HTTP calls — no field-level validation of the mapped `EnrichmentResult`.
- **MemoryMetricsTests**: 12 assertions that only check metrics instruments are not null — no behavior test (e.g., counter increments on service call).
- **InstrumentedMemoryServiceTests** and **InstrumentedGraphRagContextSourceTests**: bare `.NotBeNull()` on returned results without asserting field values.
- **Recommended improvement:** Strengthen ~20 of these with value-asserting follow-up assertions.

#### P3-D: `SessionIdGeneratorTests` — Theoretically flaky
- Tests for `PerDay` strategy embed `DateTime.UtcNow.ToString("yyyy-MM-dd")` inline, creating a 1-in-86400 failure window if the test runs exactly at UTC midnight.
- **Fix:** Inject `IClock` into `SessionIdGenerator` and mock it in tests (the infrastructure for `IClock` already exists in the project).
- **Estimated effort:** 1 source change + 2 test updates

#### P3-E: `ExactMatchEntityMatcher`, `FuzzyMatchEntityMatcher`, `SemanticMatchEntityMatcher` — Thin assertions
- Several tests only assert `result.Should().NotBeNull()` without checking `IsMatch`, `Score`, or `ResolvedEntity` fields.
- **Recommended:** Add field-level assertions to ~12 existing tests.

#### P3-F: `SchemaBootstrapper` — Only happy-path tested
- `SchemaBootstrapperTests` (11 tests, unit) validates Cypher strings. The integration tests (18 tests) validate database state. 
- Missing: test for schema already-exists idempotency path at the unit level, and a test for initialization failure with `SchemaInitializationException`.
- **Recommended unit tests:** ~3

---

## Coverage by Domain

### Repositories (Neo4j project) — 70% covered

| Repository | Unit Tests | Integration Tests | Notable Gaps |
|-----------|-----------|------------------|--------------|
| `Neo4jConversationRepository` | ✅ 9 | ✅ 8 | Good coverage |
| `Neo4jMessageRepository` | ⚠️ 0 direct | ✅ 7 | Unit: no Cypher validation |
| `Neo4jEntityRepository` | ✅ 49 | ✅ 10 | `GetByIdAsync`, `GetByNameAsync` lack unit test |
| `Neo4jFactRepository` | ✅ 25 | ✅ 10 | Good coverage |
| `Neo4jPreferenceRepository` | ✅ 6 | ✅ 8 | Good coverage |
| `Neo4jRelationshipRepository` | ❌ 0 | ❌ 0 | **CRITICAL: no tests** |
| `Neo4jReasoningTraceRepository` | ✅ 5 | ✅ 8 | Good coverage |
| `Neo4jReasoningStepRepository` | ⚠️ 0 direct | ❌ 0 | Unit: indirect only; no integration |
| `Neo4jToolCallRepository` | ⚠️ 2 | ❌ 0 | Only relationship wiring tested |
| `Neo4jExtractorRepository` | ✅ 11 | ❌ 0 | No integration tests |

### Services (Core project) — 88% covered

All 8 core service classes have test files. Error paths and null-guard tests are the main gap.

### Extraction pipeline — 90% covered

LLM extractors, AzureLanguage extractors, merge strategies, PatternBased detector, StreamingExtractor, MultiExtractorPipeline all well-tested.

### MCP Server — 94% covered

All 8 tool classes, 6 resource classes, 3 prompt classes tested. `ServiceCollectionExtensions` is the only untested public type.

### AgentFramework adapter — 82% covered

`AgentTraceRecorder`, `MafTypeMapper`, `MemoryToolFactory`, `Neo4jChatMessageStore`, `Neo4jMemoryContextProvider`, `Neo4jMicrosoftMemoryFacade` all tested. `ContextFormatOptions` and `AgentFrameworkOptions` have no dedicated tests.

### GraphRAG adapter — 50% covered

`Neo4jGraphRagContextSource` and `StopWordFilter` tested. **All three retriever implementations untested.**

### Enrichment — 90% covered

All 7 enrichment service classes have tests. Assertion quality in Diffbot tests is weak.

### Observability — 95% covered

Three source classes all tested. `ServiceCollectionExtensions` (decorator registration logic) not tested.

---

## Test Quality Assessment

### Strengths
- **1058 unit tests** is a solid foundation. Test naming is descriptive (`Method_Condition_ExpectedResult` convention).
- Repository Cypher-string tests provide strong regression protection against query rewrites.
- Options tests cover all non-trivial defaults and enum variants thoroughly.
- Exception hierarchy tested for all 9 exception types including polymorphic catch patterns.
- Theory + InlineData used effectively for parameterized scenarios.

### Issues Found

| Issue | Severity | Files Affected | Count |
|-------|----------|----------------|-------|
| `.Should().NotBeNull()` as the sole assertion | Medium | 14 files | ~63 occurrences |
| `DateTime.UtcNow` used directly in tests | Low | `SessionIdGeneratorTests.cs` | 2 tests |
| Very thin tool call repository coverage (2 tests for 5 methods) | High | `Neo4jToolCallRepositoryTests.cs` | — |
| No behavior tests for observability metrics (only existence checks) | Medium | `MemoryMetricsTests.cs` | 12 assertions |
| Error propagation paths undertested in Core services | Medium | Multiple service tests | — |

### Flaky Pattern Risk
- **Low overall.** Only 1 time-dependent test (`PerDay_*` in `SessionIdGeneratorTests.cs`). No test ordering dependencies detected. No `Thread.Sleep` patterns found.

---

## Recommended Test Additions (Ordered by Priority)

| # | Area | Test Type | New Tests | Effort |
|---|------|-----------|-----------|--------|
| 1 | `Neo4jRelationshipRepository` | Unit (Cypher validation) | ~8 | Low |
| 2 | `Neo4jRelationshipRepository` | Integration (round-trip) | ~6 | Medium |
| 3 | `AdapterVectorRetriever` | Unit | ~6 | Medium |
| 4 | `AdapterFulltextRetriever` | Unit | ~5 | Medium |
| 5 | `AdapterHybridRetriever` | Unit | ~5 | Medium |
| 6 | `Neo4jReasoningStepRepository` | Integration | ~5 | Medium |
| 7 | `Neo4jToolCallRepository` | Integration | ~6 | Medium |
| 8 | `Neo4jExtractorRepository` | Integration | ~5 | Medium |
| 9 | Core service error paths | Unit | ~15 | Medium |
| 10 | `Neo4jEntityRepository` (`GetByIdAsync`, `GetByNameAsync`) | Unit | ~4 | Low |
| 11 | `Neo4jTransactionRunner`/`Factory` | Integration | ~4 | High |
| 12 | `SessionIdGenerator` — inject IClock | Unit refactor | ~2 | Low |
| 13 | DI smoke tests (all 9 projects) | Unit | ~18 | Low |
| 14 | Strengthen enrichment assertions | Unit (strengthen) | ~0 new, refactor | Low |
| 15 | Observability metrics behavior | Unit | ~4 | Low |
| **TOTAL** | | | **~99 new tests** | |

---

## Estimated Coverage Targets

| Milestone | New Tests | Cumulative Total | Notes |
|-----------|-----------|-----------------|-------|
| **Current** | — | 1058 unit + 56 integration | Baseline |
| After P1 gaps | +50 | ~1108 unit + ~73 integration | RelationshipRepo + retrievers |
| After P2 gaps | +35 | ~1143 unit + ~85 integration | Infra + error paths |
| After P3 gaps | +30 | ~1173 unit + ~85 integration | Quality + DI smoke tests |
| **Good coverage** | **+99 total** | **~1157 unit + ~100 integration** | All critical paths covered |

---

## Conclusion

Test coverage is **good to strong** across most of the solution. The 1058 unit tests and 56 integration tests represent serious investment. The main structural gaps are:

1. **`Neo4jRelationshipRepository` is completely untested** — this is the clearest P1 deficiency.
2. **GraphRAG retriever internals** (`AdapterVector/Fulltext/HybridRetriever`) are dark — they are unit-testable today via `InternalsVisibleTo`.
3. **4 repository classes** lack integration test coverage (ReasoningStep, ToolCall, Extractor, Relationship).
4. **Test assertion quality** in enrichment and observability tests is weaker than elsewhere — mostly `.NotBeNull()` without value checking.

The integration test framework is production-ready (Testcontainers, shared fixture, CleanDatabaseAsync isolation). Executing the P1 integration tests for the 4 missing repositories is the highest-ROI next step — the framework requires no changes, only new test classes.
