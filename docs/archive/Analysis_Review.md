# Analysis & Review — Agent Memory for .NET

**Date:** 2026-05-31
**Scope:** Full-repository review of code vs. architecture/design documentation, code quality, and adherence to DRY / KISS / SOLID / CLEAN principles.
**Method:** Static review of `src/` (11 projects, ~18.7K LOC, 391 `.cs` files), `tests/` (180 files), and the canonical docs (`README.md`, `docs/architecture.md`, `docs/design.md`, `docs/schema.md`, `docs/getting-started.md`). Solution builds cleanly (`dotnet build AgentMemory.slnx` → 0 errors, 8 warnings). Findings below were verified against source with file:line citations.

> **How to read this document.** Every finding is tagged with a **severity** (`BUG` = incorrect runtime behavior; `GAP` = missing/incomplete vs. documented intent; `SMELL` = maintainability/design concern) and a **priority** (P1 highest → P3 lowest). Each finding has a concrete **Suggested fix**.

---

## 1. Executive Summary

The codebase is **mature, well-structured, and largely faithful to its layered architecture**. The dependency-direction rule (Adapters → Neo4j → Core → Abstractions) is respected, the Abstractions package keeps its single approved external dependency, package versions match the docs, and several "headline" doc claims (MCP = 21 tools / 6 resources / 3 prompts, Agent Framework = 6 tools, schema = 6 vector + 3 fulltext indexes) are **accurate**.

However, the review found:

- **6 functional bugs** (most notably: `CancellationToken` is silently ignored for *every* database operation; a fire-and-forget update in `RecallAsync`; a budget-truncation ordering bug; truncation strategies that don't actually differ).
- **Documentation drift**: `architecture.md §3.1` type counts and `design.md §5` interface method names no longer match the code; four real service interfaces are missing from the design catalog.
- **Cross-cutting maintainability issues**: repeated repository/extractor/retriever boilerplate (DRY), a heavy budget-truncation method and an 8-tuple signature repeated six times (KISS), adapters carrying business logic instead of being thin type-mappers (CLEAN/SOLID), and pervasive broad `catch (Exception)` → silent `null`/empty that hides failures.
- **Incompleteness vs. intent**: the `AgentMemory` "one-stop" bootstrap package wires only a subset of the stack; several `Options` fields (`MaxRetries`, `EntityTypes`, `WikipediaBaseUrl`) are declared but unused; Azure Language extraction is heuristic rather than the "real extraction" the docs imply.

None of these block the project, but the bugs in §3 and the documentation drift in §2 should be addressed first.

### Severity tally

| Severity | Count |
|---|---|
| BUG | 11 |
| GAP | 10 |
| SMELL | 20+ |

---

## 2. Documentation vs. Code Consistency

### 2.1 `architecture.md §3.1` type counts are stale — **GAP (P2)**

`docs/architecture.md:133` states the Abstractions package contains *"31 domain records, 15 service interfaces, 10 repository interfaces, 9 configuration types, 6 enums."* Actual counts in `src/AgentMemory.Abstractions`:

| Claimed | Actual | Notes |
|---|---|---|
| 31 domain records | **53** `public sealed record` | Materially higher |
| 15 service interfaces | **8** `IXxxService` (15 if counting all service-namespace contracts) | Ambiguous; see 2.3 |
| 10 repository interfaces | **11** | `IExtractorRepository` is undocumented |
| 9 configuration types | **7** option records | Lower |
| 6 enums | **9** | Higher |

**Suggested fix:** Regenerate these counts from code (a tiny `grep`-based check in CI would keep them honest), or replace hard numbers with "see the catalog tables in `design.md §5/§6`" so a single source of truth drives them. Add `IExtractorRepository` to the repository catalog.

### 2.2 `design.md §5` documents wrong method names for `IEmbeddingOrchestrator` — **BUG (P2, docs)**

`docs/design.md:336` lists the interface methods as `GenerateAsync, GenerateBatchAsync`. The actual interface (`src/AgentMemory.Abstractions/Services/IEmbeddingOrchestrator.cs:12-27`) exposes six methods: `EmbedEntityAsync`, `EmbedFactAsync`, `EmbedPreferenceAsync`, `EmbedMessageAsync`, `EmbedQueryAsync`, `EmbedTextAsync`. A developer following the docs would not compile.

**Suggested fix:** Update the catalog row to the actual six `Embed*Async` methods. (See also 4.3 — the interface itself is a candidate for slimming.)

### 2.3 `design.md §5` "Service Interface Catalog" omits real services — **GAP (P2)**

The catalog (`docs/design.md:325-340`) lists 15 interfaces but omits four that exist in code and ship behavior:
`IEnrichmentService` (`Services/IEnrichmentService.cs:8`), `IGeocodingService` (`IGeocodingService.cs:8`), `IGraphQueryService` (`IGraphQueryService.cs:6`), `IMemoryDecayService` (`IMemoryDecayService.cs:6`).

**Suggested fix:** Add the four interfaces to the catalog with their owning package/phase, or explicitly mark them as Phase-5 additions.

### 2.4 `StubExtractionPipeline` XML summary is misleading — **BUG (P3, docs)**

`src/AgentMemory.Core/Stubs/StubExtractionPipeline.cs:7-10` says it *"returns an empty but structurally correct ExtractionResult."* The implementation (lines 40-74) actually delegates to whatever `IEntityExtractor`/`IFactExtractor`/etc. are injected and returns their results — so with the real LLM extractors wired it returns real data. The summary describes obsolete Phase-1 behavior.

**Suggested fix:** Reword to "orchestrates the four registered extractors and aggregates their results" (it is no longer a stub), or rename the class to `DefaultExtractionPipeline`.

### 2.5 Claims verified as **correct** (no action)

- MCP Server **21 tools / 6 resources / 3 prompts** (`README.md:25,104,201`) — matches `src/AgentMemory.McpServer/Tools|Resources|Prompts`.
- Agent Framework **6 memory tools** (`MemoryToolFactory.cs:39-70`).
- Abstractions has exactly one external package ref: `Microsoft.Extensions.AI.Abstractions 10.4.1` (`AgentMemory.Abstractions.csproj`).
- Package versions: `Neo4j.Driver 6.0.0`, `Microsoft.Extensions.* 10.0.5`, `M.E.AI.Abstractions 10.4.1`, `Microsoft.Agents.AI.Abstractions 1.1.0`, `OpenTelemetry.Api 1.12.0` all match `architecture.md`.
- Schema: 6 vector indexes + 3 fulltext indexes (`SchemaQueries.cs:59-65,149-154`); 12 node labels.
- `getting-started.md` API references (`AddAgentMemoryCore`, `AddAgentMemoryFramework`, `MemoryToolFactory`, `AddGraphRagAdapter`) all resolve.

---

## 3. Functional Bugs

### 3.1 `CancellationToken` is silently dropped for all DB operations — **BUG (P1)**

`src/AgentMemory.Neo4j/Infrastructure/Neo4jTransactionRunner.cs:17-61` accepts a `CancellationToken` on every method but never passes it to `session.ExecuteReadAsync(work)` / `session.ExecuteWriteAsync(work)` nor to the `work` delegate. Cancellation requested by any caller (including the parallel fan-out in `MemoryContextAssembler`) is ignored — long/looping queries cannot be cancelled.

**Suggested fix:** Use the driver's cancellation-aware overloads / configuration and thread the token through, e.g. `session.ExecuteReadAsync(work, txConfig => { ... })` and ensure the `work` delegate receives the token (consider `Func<IAsyncQueryRunner, CancellationToken, Task<T>>`). At minimum, honor the token at entry: `cancellationToken.ThrowIfCancellationRequested()`.

### 3.2 `RecallAsync` fire-and-forget loses errors and cancellation — **BUG (P1)**

`src/AgentMemory.Core/Services/MemoryService.cs:63-67` runs `_ = UpdateAccessTimestampsAsync(context);` without awaiting, without a `CancellationToken`, and without observing exceptions. The public call returns before the decay-timestamp update completes; a thrown exception becomes an unobserved task (only logged if the method itself catches internally), and the update cannot be cancelled with the request.

**Suggested fix:** Either `await` it (it is cheap relative to the recall round-trips already performed), or make the side effect explicit and observable: pass `cancellationToken`, wrap in try/catch that logs, and document the fire-and-forget contract. Preferable: await it so callers get deterministic behavior.

### 3.3 Budget truncation: `graphRag` nulled before its size is subtracted — **BUG (P2)**

`src/AgentMemory.Core/Services/MemoryContextAssembler.cs:398`:

```csharp
else if (graphRag != null) { graphRag = null; totalChars -= graphRag?.Length ?? 0; removed = true; }
```

`graphRag` is set to `null` *before* `totalChars -= graphRag?.Length`, so the subtraction always removes `0`. `totalChars` is left overstated. It happens to be the last branch in the loop so the loop still exits, but the bookkeeping is wrong and would bite if the ordering changes.

**Suggested fix:** Capture the length first: `else if (graphRag != null) { totalChars -= graphRag.Length; graphRag = null; removed = true; }`.

### 3.4 Truncation strategies do not actually differ — **BUG (P2)**

In `MemoryContextAssembler`, `TruncateOldestFirst` (sorts then) and `TruncateLowestScoreFirst` (line 297-315, no sort) both funnel into `FitWithinBudget` (line 369-404), which always removes from a **fixed category order** (facts → entities → relevant → traces → preferences → recent → graphRag), trimming the *end* of each list. So `LowestScoreFirst` does not trim by score, and after `OldestFirst` sorts newest-first the "oldest" items it removes are the *lowest-scored-by-category*, not globally oldest across sections. The documented strategy semantics (`design.md:242`) are not honored.

**Suggested fix:** Implement the strategies distinctly: `LowestScoreFirst` should remove the globally lowest-scored item across all scored sections each iteration (requires carrying scores into the assembler — currently lost when sections are built); `OldestFirst` should remove the globally oldest item by timestamp. If scores aren't available post-assembly, thread them through `MemoryContextSection<T>` (which is the cleaner fix and also enables better telemetry).

### 3.5 Nominatim lat/long parsed with `double.Parse`, then swallowed — **BUG (P3)**

`src/AgentMemory.Enrichment/Geocoding/NominatimGeocodingService.cs:66-67` uses `double.Parse(first.Lat, …)`. Malformed/empty API data throws `FormatException`, which is caught by the broad handler at line 79-83 and returned as `null` — an outage and a data-format bug are indistinguishable.

**Suggested fix:** Use `double.TryParse(... , out var lat)` and return `null` (with a specific log) only on genuine parse failure, keeping the broad catch for transport errors. Consider surfacing a typed result so callers can distinguish "not found" from "service error".

### 3.6 Observability double-counts extraction metrics — **BUG (P2)**

`src/AgentMemory.Observability/InstrumentedMemoryService.cs:123-150` records extracted entity/fact/preference counts, while the per-extractor decorators registered in `ServiceCollectionExtensions.cs:25-31` increment the **same** `MemoryMetrics` counters for the same extraction. When both layers are active the extraction counters are inflated.

**Suggested fix:** Count at exactly one layer. Prefer the extractor decorators (closest to the unit of work) and have `InstrumentedMemoryService` only record the orchestration span, or use distinct counter names per layer.

### 3.7 Other confirmed correctness gaps (P2/P3)

- **`MetadataFilterBuilder` does not escape backticks in keys** (`src/AgentMemory.Neo4j/Queries/MetadataFilterBuilder.cs:44-45`): a metadata key containing a backtick breaks/!injects the Cypher fragment. **Fix:** reject or double-escape backticks in identifiers.
- **`AzureExtractionContext` cache keyed by content only** (`…AzureLanguage/Internal/AzureExtractionContext.cs:18-24`): the same text analyzed under a different language returns cached results for the wrong language. **Fix:** include language in the cache key.
- **`DiffbotEnrichmentService` takes a raw options object, not `IOptions<>`** (`…Enrichment/DiffbotEnrichmentService.cs:51-59`), bypassing options validation; and the API key is sent in the query string (`:85-93`) where it can be logged by proxies. **Fix:** inject `IOptions<DiffbotEnrichmentOptions>`; move the key to a header.

---

## 4. SOLID

### 4.1 `IMemoryService` / `MemoryService` is a god facade — **SMELL (P2)**

`src/AgentMemory.Abstractions/Services/IMemoryService.cs:8-85` and `src/AgentMemory.Core/Services/MemoryService.cs:13-298` bundle recall, message append/CRUD, extract-and-persist, retroactive extraction, embedding backfill, decay updates, and session clearing into one interface/class (ISP + SRP). Callers that only need recall must depend on the entire surface.

**Suggested fix:** Keep `IMemoryService` as a thin facade that *delegates* to the already-existing focused services (`IShortTermMemoryService`, `ILongTermMemoryService`, `IReasoningMemoryService`, `IMemoryExtractionPipeline`, `IMemoryDecayService`). Split the fat interface into role interfaces (e.g. `IMemoryRecall`, `IMemoryIngestion`, `IMemoryMaintenance`) if consumers need narrower contracts.

### 4.2 Adapters carry business logic (should be thin type-mappers) — **SMELL (P2)**

The architecture states adapters "MUST NOT reference business logic — act only as a type mapper" (`architecture.md:169`). In practice:
- `src/AgentMemory.AgentFramework/Tools/MemoryToolFactory.cs:280-447` performs searching, result composition, persistence, and formatting inside the tool bodies.
- `src/AgentMemory.SemanticKernel/Neo4jMemoryPlugin.cs:91-149` assembles/formats context in the adapter.

**Suggested fix:** Move the search/compose/format logic into a Core-level service (e.g. a `MemoryQueryFacade` returning ready-to-render DTOs) and have both adapters call it. This also removes the duplication noted in 5.1.

### 4.3 `IEmbeddingOrchestrator` is a fat interface — **SMELL (P3)**

`IEmbeddingOrchestrator` (six `Embed*Async` methods, one per domain concept) mostly differ only in which string they embed; `EmbedTextAsync` already generalizes them. This couples the abstraction to every domain type (OCP/ISP).

**Suggested fix:** Reduce to `EmbedAsync(string text, …)` + `EmbedBatchAsync(IReadOnlyList<string>, …)`; let callers pass the composed text. Keep typed helpers as extension methods if convenient. (Update `design.md §5` accordingly — see 2.2.)

### 4.4 Hidden async side effect in `RecallAsync` (Liskov/least-surprise) — see 3.2.

---

## 5. DRY (Don't Repeat Yourself)

### 5.1 Duplicated tool logic across the two factory methods — **SMELL (P2)**

`MemoryToolFactory` implements each of the six tools twice — once in `CreateAIFunctions()` and once in `CreateTools()` (`MemoryToolFactory.cs:43-70` and `280-447`). The two paths can drift.

**Suggested fix:** Extract one private method per capability (`SearchMemoryCoreAsync`, `RememberPreferenceCoreAsync`, …) and have both `AIFunction` and legacy `MemoryTool` wrappers call it.

### 5.2 Repeated "embed-if-null then upsert" in `LongTermMemoryService` — **SMELL (P3)**

`AddEntityAsync`, `AddPreferenceAsync`, `AddFactAsync` (`LongTermMemoryService.cs:41-117`) repeat the same shape: if embedding is null, generate it; then upsert. Same idea recurs in the embedding-backfill paths.

**Suggested fix:** A small generic helper `EnsureEmbeddingThenUpsert<T>(item, embedSelector, upsert)` or a per-type private method removes the repetition.

### 5.3 Repository / retriever boilerplate — **SMELL (P2)**

- JSON metadata serialize/deserialize is duplicated across repositories (`Neo4jConversationRepository.cs:123-129`, `Neo4jMessageRepository.cs:297-303`, and others).
- Node→content/score mapping is duplicated in `VectorRetriever.cs:76-85` and `FulltextRetriever.cs:78-85`.

**Suggested fix:** Introduce a shared `Neo4jRecordMapper`/base-repository for metadata (de)serialization and a common `MapToRetrieverResult(IRecord)` helper.

### 5.4 LLM extractor prompt/text composition duplicated 4× — **SMELL (P3)**

`LlmEntityExtractor`/`LlmFactExtractor`/`LlmPreferenceExtractor`/`LlmRelationshipExtractor` each build the message text and call/parse the LLM with near-identical scaffolding (`LlmEntityExtractor.cs:61-86` et al.).

**Suggested fix:** A shared `LlmExtractionRunner` that takes a prompt template + a JSON-shape parser delegate; each extractor supplies only its prompt and result projection. This also centralizes the robustness fix in 6.2.

---

## 6. KISS (Keep It Simple)

### 6.1 The 8-tuple budget signature is repeated six times — **SMELL (P2)**

`MemoryContextAssembler` passes an 8-element tuple `(recent, relevant, entities, preferences, facts, traces, graphRag, truncated)` through `ApplyBudget`, `TruncateOldestFirst`, `TruncateLowestScoreFirst`, `TruncateProportional`, `FitWithinBudget` (lines 220-404). The signature is hard to read and easy to mis-order (which is exactly how bug 3.3 hides).

**Suggested fix:** Introduce a small mutable `AssembledSections` holder (or a record with `with` semantics) that the truncation methods accept and return. Strategies become methods on/over that type. This simplifies signatures and makes the truncation logic testable per-strategy.

### 6.2 Brittle LLM JSON parsing — **BUG/SMELL (P2)**

`LlmEntityExtractor.cs:71-86` (and the sibling extractors) assume a clean top-level JSON object, returning empty on `null` but throwing on invalid JSON, code-fenced output, or surrounding prose — common LLM behaviors. There is no retry despite `LlmExtractionOptions.MaxRetries` existing.

**Suggested fix:** Add tolerant parsing (strip ```` ```json ```` fences, locate the first `{`/`[`), wrap parse in try/catch that logs and returns empty, and honor `MaxRetries` with a re-prompt on parse failure. Centralize in the shared runner from 5.4.

### 6.3 `CypherBuilder.AndRawFragment` invites invalid queries — **SMELL (P3)**

`src/AgentMemory.Neo4j/Infrastructure/CypherBuilder.cs:11-161` is a heavy fluent builder for a small, mostly-static query set, and `AndRawFragment` makes it easy to compose malformed/unsafe Cypher (used by the "graph" retrieval mode — see 7.2).

**Suggested fix:** Prefer parameterized constant queries in `Queries/*.cs` for the fixed cases; constrain or remove `AndRawFragment`.

---

## 7. CLEAN Architecture & Error Handling

### 7.1 Pervasive broad `catch (Exception)` → silent `null`/empty — **SMELL/BUG (P2)**

Failures are widely swallowed and converted to "no result", hiding outages and bugs:
- `Neo4jGraphRagContextSource.cs:69-73` (returns empty `GraphRagContextResult` on any retrieval failure — intentional resilience, but undifferentiated).
- `Neo4jMemoryPlugin.cs:38-41` (returns `string.Empty` — a failure looks like success).
- `MemoryToolFactory.cs` (~11 broad catches).
- `NominatimGeocodingService`, `WikimediaEnrichmentService`, `DiffbotEnrichmentService`, `TextAnalyticsClientWrapper` — all swallow into `null`/`Error`.

**Suggested fix:** Catch specific exception types; always log with context; for "resilient" paths (GraphRAG, enrichment) keep the empty-result behavior but emit an error metric/log so failures are observable (ties into 3.6 / Observability). Avoid returning `string.Empty`/`null` where the caller cannot distinguish "no data" from "error".

### 7.2 GraphRAG "Graph" mode is not a real graph retriever — **GAP (P2)**

`src/AgentMemory.Neo4j/Services/Neo4jGraphRagContextSource.cs:100-137` implements `GraphRagSearchMode.Graph` by reusing `VectorRetriever` with an injected raw Cypher tail (`RetrievalQuery`) rather than a dedicated multi-hop traversal retriever. `architecture.md:209` advertises `Graph` as "vector + multi-hop traversal." The current implementation is closer to a vector query with an appended fragment, and `CreateRetriever` mixes DI selection, fallback, and graph-mode behavior in one method.

**Suggested fix:** Add a first-class `GraphRetriever : IRetriever` that performs the documented vector-seed + traversal, and have `CreateRetriever` purely *select* a retriever (move fallback/graph logic into the retrievers themselves).

### 7.3 Missing input/options validation — **GAP (P2)**

- No `ValidateOnStart`/range checks for `Neo4jOptions`, `GraphRagOptions` (`IndexName`, `EmbeddingDimensions`), `AzureLanguageOptions` (endpoint/key), `LlmExtractionOptions`, geocoding/enrichment options. Invalid config fails late at runtime (e.g. `SchemaQueries.cs:147-155` builds vector-index DDL straight from `dimensions` with no guard).
- Public service methods in `MemoryService`/`LongTermMemoryService` don't null/empty-check arguments (`MemoryService.cs:56-199`); `Neo4jMemoryPlugin` doesn't null-check its injected service (`Neo4jMemoryPlugin.cs:18-21`).

**Suggested fix:** Add `builder.Services.AddOptions<T>().Validate(...).ValidateOnStart()` (or `IValidateOptions<T>`) for each options type; add guard clauses (`ArgumentException.ThrowIfNullOrWhiteSpace`, `ArgumentNullException.ThrowIfNull`) at public entry points.

### 7.4 `StubEntityResolver` performs no deduplication — **GAP (P2)**

`src/AgentMemory.Core/Stubs/StubEntityResolver.cs:26-58` returns entities unchanged. Entity resolution/dedup is a core advertised capability ("entity resolution and deduplication", `README.md:66`). If this stub is the default registration, duplicate entities accumulate.

**Suggested fix:** Provide a real resolver (the repo already depends on `FuzzySharp` in Core — wire a name/alias fuzzy-match + embedding-similarity resolver) and make it the default; keep the stub only for tests.

### 7.5 `AgentMemory` "one-stop" bootstrap is incomplete — **GAP (P2)**

`src/AgentMemory/ServiceCollectionExtensions.cs:21-35` (`AddNeo4jAgentMemory`) registers Core + Neo4j + LLM extraction only; it does **not** include `AgentMemory.Extraction.AzureLanguage`, `AgentMemory.Enrichment`, or `AgentMemory.Observability`, and `AgentMemory.csproj:9-14` only references the LLM extraction package. The name implies a full stack.

**Suggested fix:** Either add opt-in builder methods (`.WithObservability()`, `.WithEnrichment()`, `.WithAzureLanguageExtraction()`) and reference those packages, or rename to reflect the actual scope and document what is/isn't included.

---

## 8. Tests & Build Quality

- **8 `xUnit1013` warnings** — public `InitializeAsync`/`DisposeAsync` on integration test classes are flagged as accidental test methods (`FactRepositoryIntegrationTests.cs:28-29`, plus Preference/Entity/ReasoningTrace). **Fix:** implement `IAsyncLifetime` explicitly or reduce visibility so the analyzer doesn't treat them as `[Fact]`s. **SMELL (P3)**
- **Unused/dead code:** `Neo4jMessageRepository.BuildMessageParameters` (`:285-295`) is unused; `SchemaBootstrapper.BuildVectorIndexes` (`:61-65`) duplicates `SchemaQueries.BuildVectorIndexes` "for compatibility"; `LlmExtractionOptions.MaxRetries`/`EntityTypes`, `GeocodingOptions.MaxRetries`, `EnrichmentOptions.WikipediaBaseUrl`/`MaxRetries` are declared but never read. **Fix:** remove dead code or wire the options through (the `MaxRetries` fields tie into 6.2). **SMELL (P3)**
- **Disposable lifetimes:** `MemoryMetrics.Meter` (`MemoryMetrics.cs:20-23`) and `DiffbotEnrichmentService`'s `SemaphoreSlim` (`:164-181`) are never disposed — acceptable for app lifetime but not for library composition. **SMELL (P3)**

---

## 9. Strengths (worth preserving)

- **Clean layering & dependency direction** are genuinely respected; Abstractions stays dependency-light (verified).
- **Centralized Cypher** in `Queries/*.cs` and **snapshot testing** of queries is a strong pattern.
- **Provider-neutral abstractions** (`IChatClient`, `IClock`, `IIdGenerator`, `IEmbeddingOrchestrator`) make the core testable.
- **Resilient-by-default retrieval/enrichment** (catch → empty) is the right *intent* — it just needs observability (3.6/7.1).
- **Documentation is unusually thorough** and mostly accurate; the drift found is in details (counts, method names), not in architecture.

---

## 10. Prioritized Remediation Roadmap

### P1 — correctness, do first
1. Thread `CancellationToken` through `Neo4jTransactionRunner` (3.1).
2. Await or properly observe the `RecallAsync` decay update (3.2).

### P2 — behavior, observability, and design
3. Fix budget bug 3.3 and implement distinct truncation strategies 3.4 (introduce `AssembledSections`, 6.1).
4. Resolve metric double-counting 3.6; add error metrics to swallowed-failure paths 7.1.
5. Add options validation + guard clauses 7.3; escape Cypher identifiers 3.7.
6. Implement a real entity resolver 7.4 and a real `GraphRetriever` 7.2.
7. De-duplicate adapter/tool logic into a Core facade (4.2, 5.1); slim `IMemoryService`/`IEmbeddingOrchestrator` (4.1, 4.3).
8. Make LLM JSON parsing tolerant + honor `MaxRetries` (6.2, 5.4).
9. Correct the docs: arch §3.1 counts (2.1), design §5 method names (2.2) and service catalog (2.3), `StubExtractionPipeline` summary (2.4).

### P3 — hygiene
10. Complete/clarify the `AgentMemory` bootstrap package (7.5).
11. Remove dead code & unused options; fix `xUnit1013` warnings; review disposable lifetimes (§8).
12. Diffbot: use `IOptions<>` and a header for the API key (3.7).

---

*Generated as a point-in-time review. Line numbers refer to the repository state at the date above; re-verify before applying fixes.*
