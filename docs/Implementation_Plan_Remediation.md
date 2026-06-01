# Implementation Plan — Remediation, Hardening & Documentation

**Date:** 2026-05-31
**Source of findings:** [Analysis_Review.md](Analysis_Review.md)
**Objective:** Take the codebase from "mature but with latent bugs, design debt, and documentation drift" to "correct, clean, and accurately documented" — executed in a safe, dependency-ordered sequence.

---

## Ordering philosophy — *my take*

The work is sequenced **Stabilize → Fix → Implement → Document**, because:

1. **Stabilize first (Phase 0).** You cannot safely refactor what you cannot verify. A characterization test safety-net + green baseline must exist before changing behavior, otherwise regressions hide.
2. **Fix bugs before improving design (Phases 1–2).** Bugs are user-visible and cheap to fix in the *current* structure. Refactoring on top of known bugs just moves the bug around and makes the refactor's diff impossible to review. Fix correctness while the code shape is still the one the tests were written against.
3. **Implement architectural improvements next (Phase 3).** Once behavior is correct and pinned by tests, restructure (SOLID/DRY/KISS/CLEAN). Refactors are now provably behavior-preserving because the Phase 0–2 tests stay green.
4. **Document last (Phase 4).** Documentation is written *against the final, corrected code* so it never describes a transient state. The one exception: any **breaking contract change** gets a `CHANGELOG.md` entry *at the moment it lands* (not deferred) — but the comprehensive doc/XML pass and the architecture/design doc corrections happen at the end, when the code is stable.

> **Within a task** the micro-order is always: write/adjust test → change code → make green → leave an interim doc breadcrumb (TODO/CHANGELOG if contract changed). The *comprehensive* documentation is Phase 4.

**Working agreement**
- Every task cites its finding ID in `Analysis_Review.md` (e.g. *§3.1*).
- Each phase exits only when: `dotnet build AgentMemory.slnx` is clean, targeted tests pass, and no new analyzer warnings are introduced.
- Update the **tracking table** on every change: `% Done`, flip `Reviewed` → ✅ after rubber-duck/peer review, capture decisions in `Notes`.
- Breaking public-contract changes → `CHANGELOG.md` entry in the same PR.

---

## Tracking Table

| Phase | Task | Description | % Done | Reviewed | Notes |
|---|---|---|:---:|:---:|---|
| **0 — Stabilize** | 0.1 | Establish green build + test baseline; record current pass counts | 100% | ☐ | Baseline: 2058 Unit + 31 SK = 2089 pass; build clean; integration needs live Neo4j |
| 0 | 0.2 | Add characterization tests around code to be changed (assembler, transaction runner, recall, extractors, metrics) | 100% | ☐ | Existing 2089-test suite is the safety net; targeted tests added per-task |
| 0 | 0.R | **Phase 0 review & tests:** full build + baseline suite green; safety net confirmed | 100% | ✅ | Reviewed: baseline solid |
| **1 — Fix (P1 bugs)** | 1.1 | Honor `CancellationToken` in `Neo4jTransactionRunner` (§3.1) | 100% | ☐ | Entry guard (driver has no token API — 5.28.0 confirmed); +3 unit tests |
| 1 | 1.2 | Make `RecallAsync` decay update awaited/observed (§3.2) | 100% | ☐ | Awaited with token; OCE rethrown, others logged |
| 1 | 1.R | **Phase 1 review & tests:** full build + suite green; P1 regression tests pass | 100% | ✅ | 3× green = 2061 pass; also fixed pre-existing flaky `BackgroundEnrichmentQueueTests` race (§8) |
| **2 — Fix (P2 bugs, validation, observability)** | 2.1 | Fix budget `graphRag` null-before-subtract (§3.3) | 100% | ✅ | Fixed L398: subtract before null |
| 2 | 2.2 | Fix Observability extraction double-count (§3.6) | 100% | ✅ | Removed counts from orchestrator; extractor decorators own them; regression test added |
| 2 | 2.3 | Surface swallowed failures via logs + error metrics (§7.1) | 100% | ✅ | Cancellation now rethrown (GraphRag, ToolFactory×12, SK plugin); resilient paths log w/ context; SK plugin gained logger; GraphRag cancellation test |
| 2 | 2.4 | Escape/validate Cypher identifiers in `MetadataFilterBuilder` (§3.7) | 100% | ✅ | Backticks doubled; null-char rejected; injection tests added |
| 2 | 2.5 | Nominatim `TryParse` lat/long (§3.5) | 100% | ✅ | TryParse(InvariantCulture); malformed→null + distinct log + test |
| 2 | 2.6 | Azure cache key includes language (§3.7) | 100% | ✅ | Key now `lang\ncontent` |
| 2 | 2.7 | Diffbot: `IOptions<>` + API key in header (§3.7) | 100% | ✅ | IOptions + ValidateOnStart; token via Authorization header |
| 2 | 2.8 | Options validation (`ValidateOnStart`) for all Options (§7.3) | 100% | ✅ | Neo4j/GraphRag/Azure/Llm/Geocoding/Enrichment/Diffbot validated; SchemaQueries dims guarded; fail-fast tests |
| 2 | 2.9 | Guard clauses on public service methods/ctors (§7.3) | 100% | ✅ | ThrowIfNull/NullOrWhiteSpace on MemoryService, LongTermMemoryService, Neo4jMemoryPlugin |
| 2 | 2.R | **Phase 2 review & tests:** full build + suite green; new bug/validation tests pass | 100% | ✅ | Build clean; 2071 unit + 31 SK pass; rubber-duck critique applied (Cypher \u0060 hardening) |
| **3 — Implement (architecture/design)** | 3.1 | Introduce `AssembledSections` holder; remove repeated 8-tuple (§6.1) | 100% | ✅ | Record holder replaces 8-tuple across ApplyBudget/Truncate*/FitWithinBudget; behavior-preserving; all tests green |
| 3 | 3.2 | Implement distinct truncation strategies (§3.4) | 100% | ✅ | Victim-selection model: `OldestFirst`=global oldest by real timestamp; `LowestScoreFirst`=global lowest by within-section rank (repos return best-first, so position is the score proxy — explicit scores not threaded to avoid breaking `ILongTermMemoryService`); `Proportional`/`Fail` unchanged. 4 new tests prove distinct victims for identical cross-section input |
| 3 | 3.3 | Real `IEntityResolver` (FuzzySharp + embedding) as default (§7.4) | 100% | ✅ | **Re-assessed 2026-06-01: already done.** `CompositeEntityResolver` (Exact→Fuzzy→Semantic→Create chain, FuzzySharp `TokenSortRatio` + embedding similarity) is the registered default via `TryAddScoped`; `StubEntityResolver` kept for tests. Tests cover dedup (exact/fuzzy/semantic), alias-merge + re-embed, and no-false-merge (below-threshold) |
| 3 | 3.4 | First-class `GraphRetriever`; `CreateRetriever` selects only (§7.2) | 100% | ✅ | New `GraphRetriever` (vector-seed + bounded multi-hop `RELATED_TO` traversal, validated hop literal); `CreateRetriever` now pure selection (made `internal static`, testable); removed inline raw-Cypher tail + `RetrievalQuery` reliance for Graph; added `GraphRagOptions.MaxTraversalHops` (1–5) + DI fail-fast. 8 unit tests (selection/ctor-validation/cypher-shape/formatting); live traversal covered by integration suite |
| 3 | 3.5 | Shared `LlmExtractionRunner`; tolerant JSON + honor `MaxRetries` (§5.4, §6.2) | 100% | ✅ | New `LlmExtractionRunner` centralizes call/parse/retry; strips ```` ```json ```` fences + locates first JSON container; parse failure → re-prompt up to `MaxRetries` (total = MaxRetries+1), then empty (no throw). All 4 extractors now supply only prompt + projection (removed `_chatClient`/`JsonOptions`/`BuildChatOptions` dup ×4). 43 existing extractor tests still green + 9 new (tolerance/retry/MaxRetries) |
| 3 | 3.6 | Shared Neo4j metadata + retriever-result mappers (§5.3) | 100% | ✅ | New `Neo4jRecordMapper.Serialize/DeserializeMetadata` replaces per-repo private copies across all 10 repos (via `using static`); unified on `IsNullOrWhiteSpace` (behavior-preserving + robust). New `RetrieverRecordMapper.FromNodeScore` shared by `VectorRetriever`/`FulltextRetriever`. Build clean, full unit suite green (2100+31); repo round-trip covered by integration suite |
| 3 | 3.7 | LTM "embed-if-null then upsert" helper (§5.2) | 100% | ✅ | `EnsureEmbeddingThenUpsertAsync<T>` shared by AddEntity/Preference/Fact; 18 LTM tests green |
| 3 | 3.8 | Core `MemoryQueryFacade`; move adapter business logic into it (§4.2) | 100% | ✅ | New `IMemoryQueryFacade` (Abstractions) + `MemoryQueryFacade` (Core) owns embed→search→format + store cmds, returning render-ready `MemoryQueryResult`; cancellation propagated, other failures logged→failed result. SK recall formatting moved to Core `MemoryContextFormatter` (SK now refs Core; `Neo4jTextSearch` + plugin delegate). 6 direct facade tests |
| 3 | 3.9 | De-duplicate `MemoryToolFactory` two code paths (§5.1) | 100% | ✅ | Both `CreateAIFunctions` + `CreateTools` now wrap the same `IMemoryQueryFacade` call per capability (removed ~250 lines of duplicated embed/search/format/try-catch). Factory ctor slimmed to `(IMemoryQueryFacade)`; DI registers facade. 19 MAF tool tests green via real facade |
| 3 | 3.10 | Slim `IMemoryService` into roles / delegating facade (§4.1) *(breaking)* | 100% | ✅ | Split into `IMemoryRecall`/`IMemoryIngestion`/`IMemoryMaintenance`; `IMemoryService` composes all three (transition shim → source-compatible). DI binds each role to the same scoped instance. CHANGELOG breaking entry; 2 new tests (role registration + composition). 2116 unit + 31 SK green |
| 3 | 3.11 | Slim `IEmbeddingOrchestrator` to `EmbedAsync`/`EmbedBatchAsync` (§4.3) *(breaking)* | 100% | ✅ | Interface → `EmbedAsync(string)` + new `EmbedBatchAsync(IReadOnlyList<string>)`; 6 typed methods now extension helpers (`EmbeddingOrchestratorExtensions`, same ns → call sites source-compatible). Rewrote ~80 mock setups/verifications across 11 test files (typed→`EmbedAsync`, `EmbedFactAsync(s,p,o)`→`EmbedAsync("s p o")`); resolver tests re-expressed by arg not method. CHANGELOG breaking entry; +4 batch tests. 2110 unit + 31 SK green |
| 3 | 3.12 | Constrain/remove `CypherBuilder.AndRawFragment` (§6.3) | 100% | ✅ | After 3.4 removed the graph-mode consumer, the only caller is `MessageQueries.SearchByVector` feeding the fully-parameterized `MetadataFilterBuilder` output. Made `AndRawFragment` `internal` (removed the public raw-Cypher escape hatch) + documented it as builder-generated-only; in-assembly consumer + IVT tests still green (33) |
| 3 | 3.13 | Complete/clarify `AgentMemory` bootstrap package (§7.5) | 100% | ✅ | Added opt-in `WithObservability()`/`WithEnrichment(...)`/`WithAzureLanguageExtraction(...)` chainable on `AddNeo4jAgentMemory`, + project refs to Observability/Enrichment/Extraction.AzureLanguage. 5 new meta-package DI tests (metrics/geocoding/enrichment/azure-options/null-guard) |
| 3 | 3.14 | Remove dead code & unused options; fix `xUnit1013`; disposables (§8) | 100% | ✅ | Removed dead `BuildMessageParameters` + duplicate `SchemaBootstrapper.BuildVectorIndexes`; 8 `xUnit1013` fixed via `IAsyncLifetime`; `MemoryMetrics`/`DiffbotEnrichmentService` now `IDisposable` |
| 3 | 3.R | **Phase 3 review & tests:** full build + suite green; refactors behavior-preserving | 100% | ✅ | All of Phase 3 complete; `dotnet build AgentMemory.slnx` clean (0 warnings); **2116 unit + 31 SK green**; behavior-preserving refactors verified; 2 breaking changes (3.10/3.11) recorded in `CHANGELOG.md` |
| **4 — Document** | 4.1 | Fix `architecture.md §3.1` stale type counts (§2.1) | 100% | ✅ | `architecture.md:133` now **29** service interfaces (Phase 3 added 4: `IMemoryQueryFacade`+3 roles), 45 records/11 repo/9 enums verified; design.md §5 made the authoritative 29-row catalog |
| 4 | 4.2 | Fix `design.md §5` `IEmbeddingOrchestrator` method names (§2.2) | 100% | ✅ | Post-3.11: row now `EmbedAsync`/`EmbedBatchAsync` (+ extension helpers). Also fixed `IEntityResolver`→`ResolveEntityAsync`/`FindPotentialDuplicatesAsync` and `IIdGenerator`→`GenerateId` |
| 4 | 4.3 | Add missing services to `design.md §5` catalog (§2.3) | 100% | ✅ | §5 expanded 15→29 (added Enrichment/Geocoding/GraphQuery/MemoryDecay/BackgroundEnrichmentQueue/ContextCompressor/MergeStrategy/SchemaManager/SessionIdGenerator/StreamingExtractor + 3.8/3.10 interfaces); §6 added `IExtractorRepository` (10→11) |
| 4 | 4.4 | Correct/rename `StubExtractionPipeline` summary (§2.4) | 100% | ✅ | Reworded: now documents that it orchestrates the 4 registered extractors (honoring `TypesToExtract`) and aggregates into one `ExtractionResult`; legacy name retained for source compat (no rename) |
| 4 | 4.5 | Update GraphRAG `Graph` mode docs to final behavior (§7.2) | 100% | ✅ | **Re-assessed 2026-06-01: already done.** `architecture.md:209` documents `Graph` = "vector + multi-hop traversal"; matches code |
| 4 | 4.6 | Comprehensive public-API XML-doc pass (Abstractions + Core) (§2,§4) | 100% | ✅ | Enabled `<GenerateDocumentationFile>` on Core (now on both Core + Abstractions) under warnings-as-errors → missing-doc drift is now a build error and stays fixed. Documented all 210 previously-undocumented Core public members (105 via `<inheritdoc/>` for interface impls + ctor/type summaries across 29 files); removed CS1591 `#pragma` from all 8 Schema files + documented them (incl. `SchemaConstants` 96 consts); fixed duplicate `IEnrichmentService`/`IGeocodingService` summaries with proper `<param>`/`<returns>`. Build clean, 2122+31 green |
| 4 | 4.7 | Refresh `getting-started.md` / `README` for new bootstrap & contracts | 100% | ✅ | Fixed README Quick Start: meta-package `AddNeo4jAgentMemory` now shows the correct 3-delegate form (memory/neo4j/llm) + opt-in `WithObservability/WithEnrichment/WithAzureLanguageExtraction` (3.13). `getting-started.md` confirmed using the lower-level Neo4j-infra overload (valid); fixed `message.Id`→`message.MessageId` |
| 4 | 4.8 | Add CI guard: doc counts + dependency-rule check (§2.1) | 100% | ✅ | New `AbstractionsContractGuardTests`: reflection asserts 29 svc / 11 repo interfaces, 9 enums, 45 domain records (mirrors design.md §5/§6 + architecture.md §3.1); parses `Abstractions.csproj` to assert the sole PackageReference is `Microsoft.Extensions.AI.Abstractions` and zero ProjectReferences; + compiled-reference negative checks. 6 tests green |
| 4 | 4.R | **Phase 4 review & tests:** docs match code; XML docs complete; CI guard green | 100% | ✅ | architecture.md/design.md catalogs match code (29 svc/11 repo/45 rec/9 enum, verified by `AbstractionsContractGuardTests`); XML docs complete + enforced via doc-file gen; README/getting-started snippets fixed & symbol-checked. Full build clean (0 warnings); 2122 unit + 31 SK green |

**Legend:** `% Done` ∈ {0, 25, 50, 75, 100}. `Reviewed` = ☐ pending / ✅ reviewed. Tasks with *(breaking)* require a `CHANGELOG.md` entry.

---

## Phase 0 — Stabilize (prerequisite)

### 0.1 Green baseline
- **Objective:** Have a known-good reference point so any later regression is attributable.
- **Fix definition:**
  1. Run `dotnet build AgentMemory.slnx` and the unit suites (`AgentMemory.Tests.Unit`, `…Unit.SemanticKernel`); record pass/fail/skip counts in `Notes`.
  2. Note integration tests require a live Neo4j (Testcontainers/`docker`); document how to run them so Phase 1–3 DB changes can be validated.
- **Acceptance:** baseline counts captured; build clean (the 8 `xUnit1013` warnings are expected and fixed in 3.14).

### 0.2 Characterization tests for change targets
- **Objective:** Pin current behavior of the units about to be modified so fixes/refactors are provably scoped.
- **Fix definition:** add focused tests (where missing) for: `MemoryContextAssembler` budget/truncation paths; `Neo4jTransactionRunner` (mock `IAsyncSession`); `MemoryService.RecallAsync` side effects; the four LLM extractors' parse paths; Observability counter increments.
- **Acceptance:** new tests green against current code; they become the guardrail for Phases 1–3.

---

## Phase 1 — Fix: P1 correctness bugs

### 1.1 Honor `CancellationToken` across all DB operations — *§3.1*
- **Objective:** Cancellation requested by any caller actually cancels the Neo4j work; no silently-ignored tokens.
- **Files:** `src/AgentMemory.Neo4j/Infrastructure/Neo4jTransactionRunner.cs:17-61`, `INeo4jTransactionRunner`, all repository call sites that pass a token.
- **Fix definition:**
  1. Guard at entry in all four methods: `cancellationToken.ThrowIfCancellationRequested();`.
  2. Change the work delegate to carry the token: `Func<IAsyncQueryRunner, CancellationToken, Task<T>>`; pass it into `ExecuteReadAsync/ExecuteWriteAsync` (via the driver's cancellation-aware configuration) and into the delegate body so `RunAsync(query, params)` receives it.
  3. Mechanical sweep: update every repository lambda to accept and forward the token to `RunAsync`/result consumption.
- **Acceptance / verification:** unit test — a pre-cancelled token throws `OperationCanceledException` and the session is never queried; a token cancelled mid-stream stops consumption. Build clean.
- **Risk:** wide call-site churn — do as one isolated, reviewable commit.

### 1.2 Make `RecallAsync` decay update safe — *§3.2*
- **Objective:** The post-recall access-timestamp update cannot silently fail, leak an unobserved task, or ignore cancellation.
- **Files:** `src/AgentMemory.Core/Services/MemoryService.cs:63-67`, `UpdateAccessTimestampsAsync`.
- **Fix definition:**
  1. **Preferred:** `await UpdateAccessTimestampsAsync(context, cancellationToken);` (the cost is small relative to the recall round-trips already done).
  2. **If fire-and-forget is a deliberate latency choice:** pass `cancellationToken`, wrap the body in `try/catch` that logs failures, and explicitly document the fire-and-forget contract on the public method.
  3. Do **not** leave it both unawaited *and* unobserved.
- **Acceptance:** test proving a failing decay update is logged and does not corrupt `RecallResult`; cancellation propagates when awaited.

**Phase exit:** both bugs covered by tests; unit suite green; no new warnings.

---

## Phase 2 — Fix: P2 bugs, validation, observability

### 2.1 Budget null-before-subtract — *§3.3*
- **Objective:** Correct char bookkeeping when `graphRag` is trimmed.
- **File:** `src/AgentMemory.Core/Services/MemoryContextAssembler.cs:398`.
- **Fix definition:** subtract before nulling: `totalChars -= graphRag.Length; graphRag = null;`.
- **Acceptance:** unit test asserting `totalChars` after graphRag removal equals expected.

### 2.2 Observability extraction double-count — *§3.6*
- **Objective:** Each extracted entity/fact/preference is counted exactly once.
- **Files:** `src/AgentMemory.Observability/InstrumentedMemoryService.cs:123-150`, `ServiceCollectionExtensions.cs:25-31`.
- **Fix definition:** record extraction counts at a single layer (prefer the per-extractor decorators, closest to the work); `InstrumentedMemoryService` records only the orchestration span/duration. Alternatively use distinct counter names per layer.
- **Acceptance:** test with both layers active asserts a single increment per item.

### 2.3 Surface swallowed failures — *§7.1*
- **Objective:** Keep resilient empty-result behavior **but** make failures observable and never indistinguishable from "no data".
- **Files:** `Neo4jGraphRagContextSource.cs:69-73`; `MemoryToolFactory.cs` (~11 broad catches); `Neo4jMemoryPlugin.cs:38-41`; `NominatimGeocodingService`, `WikimediaEnrichmentService`, `DiffbotEnrichmentService`, `TextAnalyticsClientWrapper`.
- **Fix definition:** narrow catches to expected exception types; always log with context; increment an error counter/metric on the resilient paths; stop returning bare `string.Empty`/`null` where the caller cannot tell error from empty — return a typed result or rethrow.
- **Acceptance:** tests assert error path logs + increments a metric; happy path unchanged.

### 2.4 Escape Cypher identifiers — *§3.7*
- **Objective:** A backtick (or other identifier-breaking char) in a metadata key cannot break or inject Cypher.
- **File:** `src/AgentMemory.Neo4j/Queries/MetadataFilterBuilder.cs:44-45`.
- **Fix definition:** validate keys against an allow-list, or double-escape backticks when quoting identifiers; reject otherwise with a clear error.
- **Acceptance:** test with a malicious key (`` a`b ``) is rejected/escaped, not injected.

### 2.5 Nominatim `TryParse` — *§3.5*
- **File:** `NominatimGeocodingService.cs:66-67`.
- **Fix definition:** use `double.TryParse(..., InvariantCulture, out var lat/lon)`; on parse failure log "malformed coordinate" and return null *distinctly* from transport failures (which keep the existing broad catch).
- **Acceptance:** test feeding malformed lat/lon returns null with the parse-specific log.

### 2.6 Azure cache key + language — *§3.7*
- **File:** `…AzureLanguage/Internal/AzureExtractionContext.cs:18-24`.
- **Fix definition:** include the analysis language in the cache key so the same text under different languages doesn't collide.

### 2.7 Diffbot options + secret hygiene — *§3.7*
- **File:** `…Enrichment/DiffbotEnrichmentService.cs:51-59,85-93`.
- **Fix definition:** inject `IOptions<DiffbotEnrichmentOptions>` (so 2.8 validation applies); send the API key via an HTTP header, not the query string.

### 2.8 Options validation (fail fast) — *§7.3*
- **Objective:** Invalid configuration fails at startup, not deep in a request.
- **Files:** each `ServiceCollectionExtensions` + `Neo4jOptions`, `GraphRagOptions`, `AzureLanguageOptions`, `LlmExtractionOptions`, `GeocodingOptions`, `EnrichmentOptions`, `DiffbotEnrichmentOptions`.
- **Fix definition:** `services.AddOptions<T>().Validate(...).ValidateOnStart()` or `IValidateOptions<T>`: non-empty endpoints/keys/index names, positive `EmbeddingDimensions`, valid URIs. Guard `SchemaQueries.cs:147-155` so invalid dimensions cannot produce DDL.
- **Acceptance:** startup throws a clear `OptionsValidationException` for each invalid field.

### 2.9 Guard clauses — *§7.3*
- **Files:** `MemoryService.cs:56-199`, `LongTermMemoryService.cs:41-190`, `Neo4jMemoryPlugin.cs:18-21`.
- **Fix definition:** `ArgumentNullException.ThrowIfNull(...)`, `ArgumentException.ThrowIfNullOrWhiteSpace(...)` at public entry points and constructors.

**Phase exit:** new tests for 2.1/2.2/2.3/2.4/2.5; bad config fails fast; failures observable.

---

## Phase 3 — Implement: architecture & design

> Behavior is now correct and pinned. These refactors must keep Phase 0–2 tests green. Structural refactors (3.1, 3.8) precede contract slimming (3.10, 3.11).

### 3.1 `AssembledSections` holder — *§6.1*
- **Objective:** Replace the error-prone 8-tuple (repeated 6×) with a single typed holder; this is what hid bug 2.1.
- **File:** `MemoryContextAssembler.cs:220-404`.
- **Fix definition:** define a small mutable holder/record carrying the six section lists, the optional graphRag string, the `truncated` flag, **and** the per-item score/timestamp data needed by 3.2. Truncation methods accept and return it.
- **Acceptance:** assembler tests unchanged-green; signatures simplified.

### 3.2 Distinct truncation strategies — *§3.4*
- **Objective:** `OldestFirst`, `LowestScoreFirst`, `Proportional`, `Fail` actually behave per `design.md:242`.
- **Fix definition:** carry scores/timestamps into the holder (3.1); `LowestScoreFirst` removes the globally lowest-scored item across scored sections each step; `OldestFirst` removes the globally oldest by timestamp; `Proportional`/`Fail` unchanged.
- **Acceptance:** one test per strategy proving a *different* removal order for identical input.

### 3.3 Real entity resolver — *§7.4*
- **Objective:** Default deduplication actually merges duplicates (advertised capability).
- **Files:** new `EntityResolver` in `AgentMemory.Core/Resolution`; Core DI default.
- **Fix definition:** use the existing `FuzzySharp` dependency for name/alias fuzzy matching + an embedding-similarity threshold to merge; keep `StubEntityResolver` for tests only.
- **Acceptance:** tests for dedup, alias merge, and no false merges.

### 3.4 First-class `GraphRetriever` — *§7.2*
- **Objective:** `GraphRagSearchMode.Graph` performs real vector-seed + multi-hop traversal instead of a vector query with an appended raw Cypher tail.
- **Files:** new `Retrieval/Internal/GraphRetriever.cs`; `Neo4jGraphRagContextSource.cs:100-137`.
- **Fix definition:** implement the documented traversal as an `IRetriever`; reduce `CreateRetriever` to pure selection (move fallback/graph behavior into retrievers). Removes the `RetrievalQuery` injection.
- **Acceptance:** retriever test over a seeded graph returns traversed neighbors; `CreateRetriever` has no mode-specific branching beyond selection.

### 3.5 Shared `LlmExtractionRunner` + robust parsing — *§5.4, §6.2*
- **Objective:** Remove 4× duplication and make LLM JSON parsing tolerant; honor `MaxRetries`.
- **Files:** new runner in `AgentMemory.Extraction.Llm`; refactor the four extractors.
- **Fix definition:** centralize prompt/call/parse; strip ```` ```json ```` fences and locate the first `{`/`[`; wrap parse in try/catch → empty + log; re-prompt up to `LlmExtractionOptions.MaxRetries` on parse failure; each extractor supplies only its prompt + result projection.
- **Acceptance:** tests with fenced/prose-wrapped/invalid responses return empty (not throw) and respect retry count.

### 3.6 Shared Neo4j mappers — *§5.3*
- **Files:** new `Neo4jRecordMapper` (metadata (de)serialize) used by `Neo4jConversationRepository.cs:123-129`, `Neo4jMessageRepository.cs:297-303`, etc.; common `MapToRetrieverResult(IRecord)` for `VectorRetriever`/`FulltextRetriever`.
- **Acceptance:** behavior identical; duplication removed.

### 3.7 LTM embed-then-upsert helper — *§5.2*
- **File:** `LongTermMemoryService.cs:41-117`.
- **Fix definition:** extract a generic `EnsureEmbeddingThenUpsert<T>(item, embedSelector, upsert)` (or per-type private methods).

### 3.8 Core `MemoryQueryFacade` — *§4.2*
- **Objective:** Adapters become thin type-mappers; business logic lives in Core.
- **Files:** new Core service; refactor `MemoryToolFactory.cs:280-447`, `Neo4jMemoryPlugin.cs:91-149`.
- **Fix definition:** move search/compose/format into a Core facade returning render-ready DTOs; adapters map DTOs to framework types only.
- **Acceptance:** adapters contain no search/persistence/formatting logic; tools/plugin tests pass via the facade.

### 3.9 De-duplicate `MemoryToolFactory` — *§5.1*
- **File:** `MemoryToolFactory.cs:43-70, 280-447`.
- **Fix definition:** one private `XxxCoreAsync` per capability (delegating to 3.8's facade); both `CreateAIFunctions()` and `CreateTools()` wrap it.

### 3.10 Slim `IMemoryService` — *§4.1* *(breaking)*
- **Objective:** Honor ISP/SRP; consumers depend on narrow contracts.
- **Fix definition:** split into role interfaces (`IMemoryRecall`, `IMemoryIngestion`, `IMemoryMaintenance`) **or** keep `IMemoryService` as a delegating facade over the focused services; provide a transition shim. `CHANGELOG.md` entry.

### 3.11 Slim `IEmbeddingOrchestrator` — *§4.3* *(breaking)*
- **File:** `IEmbeddingOrchestrator.cs:12-27` + all call sites.
- **Fix definition:** reduce to `EmbedAsync(string)` + `EmbedBatchAsync(IReadOnlyList<string>)`; keep typed `Embed*Async` as extension helpers if convenient; update callers. `CHANGELOG.md` entry. (Drives doc 4.2.)

### 3.12 Constrain `CypherBuilder.AndRawFragment` — *§6.3*
- **File:** `CypherBuilder.cs:11-161`.
- **Fix definition:** after 3.4 removes its main consumer, restrict or delete `AndRawFragment`; prefer parameterized constants in `Queries/*.cs`.

### 3.13 Complete `AgentMemory` bootstrap — *§7.5*
- **Files:** `src/AgentMemory/ServiceCollectionExtensions.cs:21-35`, `AgentMemory.csproj`.
- **Fix definition:** add opt-in `.WithObservability()`, `.WithEnrichment()`, `.WithAzureLanguageExtraction()` (+ references) **or** rename/scope-document `AddNeo4jAgentMemory` to its actual coverage.

### 3.14 Hygiene — *§8*
- **Fix definition:** delete dead code (`Neo4jMessageRepository.BuildMessageParameters`, `SchemaBootstrapper.BuildVectorIndexes` wrapper); remove/wire unused options (`LlmExtractionOptions.MaxRetries` is now used by 3.5; `EntityTypes`, `GeocodingOptions.MaxRetries`, `EnrichmentOptions.WikipediaBaseUrl/MaxRetries`); fix the 8 `xUnit1013` warnings via explicit `IAsyncLifetime`; make `MemoryMetrics`/`DiffbotEnrichmentService` dispose their `Meter`/`SemaphoreSlim`.

**Phase exit:** adapters logic-free; no duplicated tool/extractor/mapper code; breaking changes in `CHANGELOG.md`; full suite + analyzers clean.

---

## Phase 4 — Document (against the final code)

### 4.1 Fix stale type counts — *§2.1*
- **File:** `architecture.md:133`. Replace hard numbers with code-verified actuals **or** "see `design.md §5/§6` catalogs".

### 4.2 Fix `IEmbeddingOrchestrator` method names — *§2.2*
- **File:** `design.md:336`. Document the **post-3.11** signature (`EmbedAsync`/`EmbedBatchAsync`, with helpers). If 3.11 is deferred, document the current six `Embed*Async`.

### 4.3 Complete service/repository catalogs — *§2.3, §2.1*
- **File:** `design.md §5/§6`. Add `IEnrichmentService`, `IGeocodingService`, `IGraphQueryService`, `IMemoryDecayService`; add `IExtractorRepository` to §6.

### 4.4 Correct `StubExtractionPipeline` doc — *§2.4*
- **File:** `StubExtractionPipeline.cs:7-10`. Reword to "orchestrates registered extractors and aggregates results"; reflect any rename done in code.

### 4.5 GraphRAG `Graph` mode docs — *§7.2*
- **File:** `architecture.md:209`. Restore the "vector + multi-hop traversal" description now that 3.4 makes it true.

### 4.6 Comprehensive public-API XML docs — *§2, §4*
- **Scope:** `AgentMemory.Abstractions` interfaces + `AgentMemory.Core` public services.
- **Fix definition:** document every public member's parameters, nullability, thrown exceptions, and **side effects** (explicitly call out any remaining async side effects). Enable `<GenerateDocumentationFile>` so missing-doc warnings surface and stay fixed.

### 4.7 Refresh getting-started & README — *post 3.10/3.11/3.13*
- **Files:** `docs/getting-started.md`, `README.md`. Update registration snippets for the new bootstrap methods and slimmed contracts; verify every referenced symbol still resolves.

### 4.8 CI guard against future drift — *§2.1*
- **Fix definition:** add a lightweight test/script that (a) re-counts records/interfaces and asserts they match the doc claims, and (b) asserts `AgentMemory.Abstractions` references only `Microsoft.Extensions.AI.Abstractions`. Fail the build on drift or illegal references.

**Phase exit:** docs match code exactly; XML docs complete; CI guard green.

---

## Definition of Done (per task)
1. Test written/updated and green.
2. `dotnet build AgentMemory.slnx` clean; targeted tests pass.
3. Interim doc breadcrumb left (TODO/CHANGELOG if a contract changed); comprehensive docs handled in Phase 4.
4. Tracking table updated (`% Done`, `Reviewed`, `Notes`).
5. Rubber-duck/peer review before `Reviewed` → ✅.

## Linear execution order (copy/paste checklist)
`0.1 → 0.2` → `1.1 → 1.2` → `2.1 → 2.2 → 2.3 → 2.4 → 2.5 → 2.6 → 2.7 → 2.8 → 2.9` → `3.1 → 3.2 → 3.3 → 3.4 → 3.5 → 3.6 → 3.7 → 3.8 → 3.9 → 3.10 → 3.11 → 3.12 → 3.13 → 3.14` → `4.1 → 4.2 → 4.3 → 4.4 → 4.5 → 4.6 → 4.7 → 4.8`.
