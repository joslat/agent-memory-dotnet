# Squad Decisions

## Active Decisions


### 2026-05-08T09:26:42.925+02:00: User directive
**By:** Jose Luis Latorre Millas (via Copilot)
**What:** Do not use `claude-opus-4.7` or `gpt-5.5` at all; avoid both models for squad work.
**Why:** User request — captured for team memory


---

### 2026-04-30: Round 4 — DELETE_SESSION_DATA Gap review

**By:** Deckard (Lead)
**What:** Reviewed implementation of ClearSessionAsync fix. Tests passing (2057). PR opened. Architecture compliant — new methods in Abstractions, implementations in Neo4j, Core uses injection only.
**PR:** https://github.com/joslat/agent-memory-dotnet/pull/1
**Status:** Approved — advancing to 90%


---

### 2026-04-30: PR #1 merged — DELETE_SESSION_DATA Gap

**By:** Deckard (Lead, top-tier review with claude-opus-4.7)
**PR:** https://github.com/joslat/agent-memory-dotnet/pull/1
**Issues found:** One apparent test failure (`BackgroundEnrichmentQueueTests.EnqueueAsync_ProviderThrows_OtherProvidersStillCalled`) that reproduced equally on `main` — confirmed pre-existing flaky test (NSubstitute ordering sensitivity in full suite run; passes in isolation). Not a regression from this PR.
**Fixes applied:** None required — all checklist items passed on first inspection.
**Final test count:** 2058 total (2057 passing; 1 pre-existing flaky). All 11 PR-specific tests (DeleteBySessionAsync ×4, ClearSessionAsync ×1, CypherQueryInventory ×1, CypherCatalog ×1, structural query tests ×4) passed green.
**Architecture verdict:** Clean — boundaries maintained, DI correct, no layer violations. `IReasoningTraceRepository` correctly registered in `AgentMemory.Neo4j` DI extension. `ShortTermMemoryService` in Core references only Abstractions interfaces. Cypher queries use correct node labels (`Conversation`, `ReasoningTrace`, `ReasoningStep`), relationship type (`HAS_STEP`), and `$sessionId` parameter. N+1 loop fully eliminated.
**Decision:** Approved and merged to main.


---

# Deckard Priority Assessment

**Date:** 2026-05-08T09:26:42.925+02:00
**Requested by:** Jose Luis Latorre Millas

## Decision

1. **Priority source of truth**
   - Treat `.squad/identity/now.md` as the current operational priority source because it is newer than `docs/nextsteps.md`.
   - `docs/nextsteps.md` remains useful for rationale and sequencing history, but it is no longer authoritative where it conflicts with `now.md`.

2. **Model operating set**
   - Exclude `claude-opus-4.7` and `gpt-5.5` from squad recommendations and spawning guidance.
   - Preferred operating set:
     - Default / analysis / implementation: `claude-sonnet-4.6`
     - Heavy code generation: `gpt-5.3-codex`
     - Mechanical or low-complexity work: `claude-haiku-4.5`
     - Secondary fallbacks: `claude-sonnet-4.5`, `gpt-5.4`, `gpt-4.1`, `gpt-5.4-mini`, `gpt-5-mini`, `gpt-5.2`, `gpt-5.2-codex`

3. **Recommended execution order**
   - **First:** NuGet release preparation, plus immediate reconciliation of `now.md` and `docs/nextsteps.md`
   - **Second:** Streaming extraction
   - **Third:** Local embedding adapter
   - **Fourth:** Additional framework integrations, with AutoGen.NET first
   - **Parallel governance action:** explicitly either finish or de-scope the lingering Aspire demo work so it stops creating priority ambiguity

## Rationale

- The repository appears feature-complete for v1-level library scope: 11 packages, strong package boundaries, and unit suites passing in this environment.
- The highest-value remaining work is what converts completed engineering into adoption: release readiness and installability.
- Streaming extraction is the clearest remaining product capability gap.
- Local embeddings come next because they unlock air-gapped and cost-sensitive deployment scenarios.
- Additional integrations expand reach, but they should follow the higher-value platform gaps above.


---

# Package Rename Review — 2026-04-30
**Reviewer:** Deckard (Lead Architect)
**Branch:** rename/agentmemory-package-ids
**Commit:** acef3efb58de48e24893107fa7c5bf4b65c0fbcc

## Verdict: APPROVED

---

## Summary

Roy's rename of all eleven source packages from `Neo4j.AgentMemory.*` to `AgentMemory.*` is architecturally correct, mechanically complete, and build-verified. All eight review gates passed with two minor observations that do not block merge. The branch contains exactly one commit; it is safe to merge to main.

---

## Findings by Area

### 1. Rename Reasoning — PASS

The top-level prefix `AgentMemory.*` is correct: the library is a product, not a Neo4j first-party SDK. `AgentMemory.Neo4j` survives as the adapter qualifier, which is the right pattern (product first, technology qualifier second). This is consistent throughout all eleven packages, the test projects, and the sample projects. No adapter package carries an ambiguous name.

### 2. CHANGELOG Entry — PASS

The `[Unreleased]` block accurately records the rename with context: what changed, why (NuGet IDs are permanent, pre-publish window), and scope (453 .cs files, 17 .csproj files, 1 .slnx). The only occurrence of `Neo4j.AgentMemory` in any `.md` file outside `.squad/` is in the CHANGELOG itself, correctly used as the "renamed from" value. That is expected and appropriate.

### 3. .csproj File Correctness — PASS with one minor observation

- `AgentMemory.Core.csproj`: ProjectReferences point to correct new paths. No explicit `<PackageId>` — defaults to project name `AgentMemory.Core`. Correct.
- `AgentMemory.Neo4j.csproj`: Same pattern. PackageId implicit from project name. Correct.
- `AgentMemory.csproj` (meta-package): ProjectReferences updated correctly. **Minor:** `<Description>` still reads "Convenience meta-package for Neo4j Agent Memory" and `<Authors>Neo4j</Authors>` — stale branding text in the description field. The package ID itself is correct (`AgentMemory`). This is a cosmetic issue for NuGet Release Prep, not a blocker for the rename.

None of the packages have an explicit `<PackageId>` element; all rely on MSBuild's project-name default. This is technically correct pre-v1 but should be made explicit during NuGet Release Prep (#4) to prevent any accidental drift.

### 4. README and Key Docs — PASS

`README.md` uses `AgentMemory.*` package names throughout all install snippets, package tables, and usage examples. No `dotnet add package Neo4j.AgentMemory.*` references found. Docs are clean.

### 5. .squad/ Internal Docs — PASS

`git diff main...HEAD -- ".squad"` returns 0 lines. Operational docs (charters, decisions, histories) were correctly left unmodified by Roy. These are internal artifacts, not part of the public package surface, and the decision not to rewrite them is correct.

### 6. Namespace / File Path Alignment — PASS

Spot-checked files across four packages:
- `AgentMemory.Abstractions`: `CompressedContext.cs`, `DeduplicationStats.cs`, `DuplicatePair.cs` — all declare `namespace AgentMemory.Abstractions.Domain;`
- `AgentMemory.McpServer`: `McpServerOptions.cs`, `ServiceCollectionExtensions.cs` — declare `namespace AgentMemory.McpServer;`

Path segments and namespace declarations are aligned. No legacy `Neo4j.AgentMemory.*` namespace declarations observed.

### 7. NuGet Package Metadata Consistency — PASS

- `AgentMemory.McpServer.csproj`: `<RootNamespace>AgentMemory.McpServer</RootNamespace>` — correct.
- `AgentMemory.Abstractions.csproj`: `<RootNamespace>AgentMemory.Abstractions</RootNamespace>`, `<AssemblyName>AgentMemory.Abstractions</AssemblyName>` — correct.

No old `Neo4j.AgentMemory.*` values found in any metadata field across either package.

### 8. Git Log — PASS

```
acef3ef (HEAD -> rename/agentmemory-package-ids) chore: rename all packages from Neo4j.AgentMemory.* to AgentMemory.*
```

Exactly one commit on the branch. The commit message is clear and follows the project's conventional commit style. No extraneous commits, no merge noise.

---

## Issues Requiring Remediation Before Merge

### Blockers
None.

### Minor
1. **Meta-package `<Description>` is stale.** `AgentMemory.csproj` still reads `"Convenience meta-package for Neo4j Agent Memory."` The description will appear verbatim on NuGet.org. Recommend updating to `"Convenience meta-package for Agent Memory for .NET. References all essential assemblies so consumers only need a single package reference."` This can be done as part of NuGet Release Prep (#4) or in a follow-up commit on this branch before merge — either is acceptable.

### Cosmetic
2. **`nextsteps.md` "What is not done yet" paragraph** still lists "package rename (AgentMemory.* root namespace)" as pending. Stale after this merge. Updating as part of this review.

3. **No explicit `<PackageId>` in any .csproj.** Relying on project-name default is correct but fragile if a project is ever renamed or moved. This is NuGet Release Prep scope, not rename scope.

---

## Recommendation

APPROVE FOR MERGE.

The rename is complete, correct, and verified. The two minor observations above (stale description text, no explicit PackageId) are pre-existing patterns that belong to NuGet Release Prep (#4), not to this branch. The cosmetic issue in nextsteps.md is addressed by this review commit.


---

# Holden — Aspire Demo test gate

**Date:** 2026-05-01T00:49:46.110+02:00
**Author:** Holden
**Scope:** Aspire Demo sample-only branch review gate

## Decision

The current failure in `AgentMemory.Tests.Unit.Enrichment.BackgroundEnrichmentQueueTests.EnqueueAsync_ProviderThrows_OtherProvidersStillCalled` should be treated as **pre-existing and unrelated to the Aspire Demo branch**.

## Evidence

- Diff from `origin/loop/aspire-demo` (`f6e2cf2`) to `HEAD` changes only `samples/`, `docs/plans/`, and squad tracking artifacts; no `src/` or `tests/` files in the failing area changed.
- The failing test and `BackgroundEnrichmentQueue` implementation both predate this branch.
- The failure pattern is a race inside the test: it waits for the provider mock to complete, then immediately asserts `repo.UpsertAsync`, even though persistence happens afterward on the background worker.
- Re-running the single failing test passed repeatedly, consistent with flakiness rather than a deterministic Aspire regression.

## Review Gate Guidance

For Aspire Demo review/merge gating, use a **sample-scoped verification set**:

1. Build the sample solution/projects under `samples\AspireDemo\`
2. Run any sample-specific smoke validation for the deterministic demo flow
3. Treat the unrelated background-enrichment unit test as **informational noise**, not a blocker for Task 3

If a broader repository test run is executed, this specific test should be called out as a known unrelated flaky test until someone fixes the synchronization in the test itself.


---


### D-WAVE1: IEmbeddingOrchestrator + ExtractorBase<T> (Roy, 2026-07-18)

**Status:** Implemented ✅  
**Scope:** Refactoring Wave 1  
**Date:** 2026-07-18

#### IEmbeddingOrchestrator Placement
Interface placed in `Abstractions` (not Core) so it can be mocked by test projects without depending on Core. Implementation in Core (accesses `IEmbeddingGenerator<string, Embedding<float>>`).

#### LongTermMemoryService Entity Embedding
`AddEntityAsync` composes `text = entity.Name or $"{entity.Name}: {entity.Description}"` BEFORE calling `EmbedTextAsync`. Text composition stays in the service; orchestrator handles generation + error handling.

#### CompositeEntityResolver Re-embed
`combinedText = $"{mergedEntity.Name} {string.Join(" ", mergedAliases)}"` stays composed in the resolver; calls `EmbedTextAsync(combinedText)`.

#### ExtractorBase<T> in Core
Both Extraction.Llm and Extraction.AzureLanguage now reference Core. No circular dependency: Core → Abstractions only; Extraction.Llm/AzureLanguage → Abstractions + Core.

#### Error Handling
The orchestrator's `EmbedTextAsync` catches exceptions and returns empty array. Previously, some services propagated exceptions. This is intentional — centralized, consistent error handling means failed embeddings return empty vectors rather than crashing the pipeline.

---

---

### D-WAVE2: Pipeline SRP Split and Dual Pipeline Merge (Roy, 2026-07-18)

**Status:** Implemented ✅  
**Scope:** Refactoring Wave 2  
**Date:** 2026-07-18

#### Context
`MemoryExtractionPipeline` had 14 constructor dependencies and 4 responsibilities (extract, filter/validate, resolve, embed/persist). `MultiExtractorPipeline` implemented identical extraction with multi-extractor merge logic as a separate pipeline — leading to two registered `IMemoryExtractionPipeline` implementations and duplicated DI logic.

#### Decision: Split MemoryExtractionPipeline into ExtractionStage + PersistenceStage
**Merge MultiExtractorPipeline into ExtractionStage.**

#### Rationale
1. **SRP compliance:** Each stage has a single, clear responsibility.
2. **Testability:** Stages can be tested in isolation with fewer mocks.
3. **Extensibility:** New stages (caching, enrichment pre-check) can be inserted between Extract → Persist without touching the pipeline class.
4. **No API change:** `IMemoryExtractionPipeline.ExtractAsync` signature and return type unchanged.

#### Design Choices

**Interfaces are `internal`, not `public`**  
`IExtractionStage` and `IPersistenceStage` are internal to Core — they are implementation details. The public contract remains `IMemoryExtractionPipeline` in Abstractions. This avoids polluting the public API with infrastructure concerns. Consequence: `MemoryExtractionPipeline` constructor must be `internal` (C# accessibility rule: public method cannot reference internal types). DI container uses reflection and respects `InternalsVisibleTo`, so this is transparent to callers.

**ExtractionStageResult is a `record`**  
The stage result DTO uses `record` (not `class`) to support C# `with` expression in tests and for semantic value-equality without hand-rolling equality methods.

**DynamicProxyGenAssembly2 InternalsVisibleTo**  
NSubstitute/Castle.DynamicProxy requires `[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]` to generate mock proxies for internal interfaces. Added to `Core/Properties/AssemblyInfo.cs` and `Core/Neo4j.AgentMemory.Core.csproj`.

**ExtractionStage Absorbs MultiExtractorPipeline**  
Multi-extractor fan-out with merge strategies (Union, Intersection, Confidence, Cascade, FirstSuccess) now lives inside `ExtractionStage`. Single-extractor is a fast path (no merge). DI injects `IEnumerable<T>` for each extractor type — all registered implementations are used.

**Relationship Resolution Split Across Stages**  
- ExtractionStage resolves entity endpoint names against the graph (read) and builds a name→Entity map.
- PersistenceStage embeds + upserts entities first (write), builds name→persistedEntity map, then wires relationships using persisted entity IDs.
- This respects the boundary: Extraction reads/resolves, Persistence writes/links.

#### Impact
- `MemoryExtractionPipeline`: 3 constructor deps (down from 14) ✅
- `MultiExtractorPipeline.cs`: deleted ✅
- `ServiceCollectionExtensions.cs`: two new `TryAddScoped` registrations ✅
- Tests: 1,066 passing, 0 failing ✅

---

---

### D-WAVE2-THRESHOLDS: Thresholds Parameterization + Azure API Cache (Gaff, 2026-07-18)

**Status:** Implemented ✅  
**Scope:** Refactoring Wave 2, Findings 6 + 7  
**Date:** 2026-07-18

#### Finding 6: Confidence Thresholds — Where to Put Them

**Decision:** Added `StrongPatternConfidence`/`RegexMatchConfidence` to `ExtractionOptions` (Abstractions) and `KeyPhraseFactConfidence`/`LinkedEntityFactConfidence` to `AzureLanguageOptions` (AzureLanguage package).

**Rationale:** `ExtractionOptions` already owns extraction behaviour flags; the two new fields belong there. `AzureLanguageOptions` is the natural home for Azure-specific confidence tuning — it already owned `PreferenceSentimentThreshold`.

**Rejected alternative:** A dedicated `ConfidenceOptions` class. Rejected as over-engineering for 4 values; co-location with their parent configuration class is more discoverable.

#### Finding 6: PatternBasedPreferenceDetector Constructor Strategy

**Decision:** Added a primary `IOptions<ExtractionOptions>` constructor AND a parameterless constructor that delegates to it via `Options.Create(new ExtractionOptions())`.

**Rationale:** Tests use `new PatternBasedPreferenceDetector()` without DI. Making the options parameter required would have broken all 30+ existing tests. Dual-constructor pattern is idiomatic in .NET for optional DI — the parameterless ctor uses safe defaults and requires zero test changes.

#### Finding 7: AzureExtractionContext Scope Decision

**Decision:** `AzureExtractionContext` is registered as **scoped** (not singleton).

**Rationale:** Entity recognition results are only safe to cache within a single extraction operation scope. Caching across requests could serve stale results if message content is reused across sessions with different contexts. Scoped lifetime ties the cache to the DI scope, which matches the extraction pipeline lifetime.

**Decision:** `AzureExtractionContext` is **internal** to the AzureLanguage package.

**Rationale:** This is an implementation detail of how the Azure package avoids redundant API calls. No external consumer needs to know about or interact with the cache. Staying internal preserves the package's public API surface.

#### Finding 7: IReadOnlyList vs ToList() in Relationship Extractor

**Decision:** Removed the intermediate `.ToList()` call in `AzureLanguageRelationshipExtractor` since `GetOrRecognizeEntitiesAsync` returns `IReadOnlyList<T>` which supports index access.

**Rationale:** The for-loop in the relationship extractor used index access (`entityList[i]`, `entityList[j]`). `IReadOnlyList<T>` supports indexing, so the `.ToList()` conversion was unnecessary. Removing it avoids an extra allocation per message.

---

---

### D-WAVE3-CYPHER: Cypher Query Centralization (Deckard, 2026-07-22)

**Status:** Implemented ✅  
**Scope:** Refactoring Wave 3  
**Date:** 2026-07-22

All 12 query classes are well-organized, consistently named (PascalCase), and thoroughly documented with XML doc summaries. 140 centralized query constants across `EntityQueries`, `FactQueries`, `PreferenceQueries`, `RelationshipQueries`, `ConversationQueries`, `MessageQueries`, `ExtractorQueries`, `ToolCallQueries`, `ReasoningTraceQueries`, `SessionQueries`, `ConfigurationQueries`, and `SharedFragments`. The pattern of one constant per repository method with matching comments (`// ── MethodName ──`) makes cross-referencing easy.

**CypherQueryRegistry reflection design** is clean and correct. Filters for static classes in the right namespace, extracts `const string` fields only. Good foundation for EXPLAIN-based query validation.

---

---

### D-WAVE4-DOMAIN: Functional Parity Domain Types (Deckard, 2026-07-22)

**Status:** Implemented ✅  
**Scope:** Refactoring Wave 4  
**Date:** 2026-07-22

#### Domain Types Correctly Placed
`SessionSummary`, `EntityProvenance`, `ProvenanceSource`, `ProvenanceExtractor`, `ExtractionStats`, `ExtractorStats`, `DuplicatePair`, `DeduplicationStats` — all in `Abstractions/Domain/` with correct subdirectories. Note: `TemporalAnnotation` was never implemented (temporal retrieval is a future gap).

#### Domain Type Design Quality
- `sealed record` used correctly for all immutable value types
- Positional records for aggregates (`DeduplicationStats`, `ExtractionStats`, `SessionSummary`)
- Init-only properties for richer types (`SessionInfo`, `Extractor`)
- Nullable types used correctly (`DateTimeOffset?`, `string?`, `int?`)
- Defensive defaults (`Metadata = new Dictionary<string, object>()`)

#### Critical Fixes Applied
- **C1: Provenance query property names** — Fixed `GetEntityProvenance` to read `start_pos`/`end_pos` (not `start_position`/`end_position`)
- **I1: ListSessions ordering** — Fixed `collect(m)` to `collect(m ORDER BY m.timestamp)`
- **I2: PreferenceQueries duplicate** — Unified `UpdateEmbedding` to reference `SetEmbedding`
- **I3: Placeholder parameter** — Removed unused placeholder parameter in `GetDeduplicationStats`

---

---

### D-DECKARD-ASSESSMENT: Post-Refactoring Architecture Assessment (Deckard, 2026-07-22)

**Status:** Assessment Complete ✅  
**Scope:** Post-refactoring comprehensive audit  
**Date:** 2026-07-22

#### Code Quality Metrics

| Metric | Result |
|--------|--------|
| **Build** | ✅ 0 errors, 8 warnings (all xUnit1013 in integration tests, not src/) |
| **Unit tests** | ✅ **1,211 passing**, 0 failures, 0 skipped |
| **TODO/FIXME/HACK** | **0** in src/ |
| **Inline Cypher in repositories** | 21 residual (down from 207+; 140 centralized constants in Queries/) |
| **Centralized query constants** | **140** across 13 per-domain `*Queries` classes |
| **Source files** | **289** .cs files in src/ |
| **Circular dependencies** | **0** |
| **Boundary violations** | **0** |

#### Architecture Assessment

**Dependency Graph: ✅ CLEAN**  
Strictly layered. Abstractions is a leaf dependency. No circular deps, no boundary violations. All 9 packages verified via .csproj ProjectReference analysis.

**Queries/ Organization: ✅ EXCELLENT**  
13 per-domain query classes + `CypherQueryRegistry` + `SharedFragments` + `MetadataFilterBuilder`. Consistent naming convention (`[Domain]Queries`). XML documented.

**ExtractionStage + PersistenceStage: ✅ PROPERLY ISOLATED**  
Both are `internal sealed` in `Neo4j.AgentMemory.Core.Extraction`. Not exposed publicly.

**IEmbeddingOrchestrator: ⚠️ 2 LEAKS IN AGENTFRAMEWORK**  
Core/Services is clean — only `EmbeddingOrchestrator.cs` calls `_generator.GenerateAsync`. However, **2 call sites in AgentFramework bypass the orchestrator**:
1. `MemoryToolFactory.cs:58` — direct `IEmbeddingGenerator.GenerateAsync`
2. `Neo4jMemoryContextProvider.cs:70` — direct `IEmbeddingGenerator.GenerateAsync`

**Recommendation:** Refactor both to inject `IEmbeddingOrchestrator` instead of raw `IEmbeddingGenerator`.

#### Updated Per-Package Scores

| Package | Before | After | Key Improvements |
|---------|--------|-------|-----------------|
| **Core** | 7/10 | **9/10** | SRP ✅ (pipeline split), DRY ✅ (orchestrator), KISS ✅ (unified pipeline) |
| **Neo4j** | 8/10 | **9/10** | KISS ✅ (centralized queries, no more inline Cypher) |
| **Extraction.Llm** | 7/10 | **8/10** | DRY ✅ (ExtractorBase<T>) |
| **Extraction.AzureLanguage** | 6/10 | **8/10** | DRY ✅ (ExtractorBase<T>), KISS ✅ (ExtractionContext) |
| Others | Unchanged | Unchanged | Already 9-10/10 |

**Weighted average: 8.7/10 → 9.1/10**

#### Gap Analysis Updates

**Resolved Gaps**
- **Repository integration tests** — 7 repository-level integration test classes exist
- **Azure preference extraction** — `AzureLanguagePreferenceExtractor.cs` (79 LOC) exists
- **Stale documentation counts** — All test counts, MCP tool counts, and file counts now updated

**Still Missing**
| Gap | Severity | Status |
|-----|----------|--------|
| Semantic Kernel adapter | High | Not started |
| NuGet publishing + single package | High | Decided, not published |
| Provider tag in enrichment cache keys | Medium | Correctness bug, not fixed |
| Missing duration metric in Observability | Low | Not fixed |
| Temporal memory retrieval | Medium | Not implemented |
| Memory decay/forgetting | Medium | Not implemented |
| Configuration validation tests | Low | Not found |
| Externalize LLM system prompts | Low | Deferred |

#### Section 11 Audit: "What I Would Change"

**Result: 8 of 17 items completed (47%)**  
All high-severity code quality items resolved. Remaining items are feature additions and publishing.

#### What's Next — Prioritized Recommendations

| Priority | Item | Impact/Effort | Rationale |
|----------|------|---------------|-----------|
| **1** | **Single NuGet package** | 5.0 | Unblocks all external consumption. No code changes. |
| **2** | **Provider tag in enrichment cache keys** | 4.0 | Correctness bug. One-line fix per cache decorator. |
| **3** | **Fix missing duration metric** | 3.0 | 5-line fix in InstrumentedMemoryService. |
| **4** | **Fix AgentFramework embedding leaks** | 2.5 | 2 call sites bypass IEmbeddingOrchestrator. |
| **5** | **Semantic Kernel adapter** | 2.25 | Largest .NET AI audience. ~500 LOC thin adapter. |
| **6** | **Configuration validation tests** | 2.0 | Low-risk, fills testing gap. |
| **7** | **Externalize LLM system prompts** | 2.0 | Enables prompt tuning without redeployment. |
| **8** | **Observability for extraction/enrichment** | 1.25 | Production debugging value. |
| **9** | **Temporal memory retrieval** | 1.0 | Complex feature; requires design review. |
| **10** | **Memory decay/forgetting** | 0.75 | Complex feature; requires design review. |

**Recommended sprint:** Items 1-4 are all quick wins (< 1 day total).

#### Overall Verdict

The codebase is in **excellent shape** post-refactoring. The 4 waves addressed all high-severity code quality issues. The weighted average package score improved from **8.7/10 to 9.1/10**. Zero circular dependencies, zero boundary violations, 1,211 tests passing. The remaining work is primarily **feature additions** and **publishing**, not quality fixes.

**The architecture is production-ready.** The next step is to ship it (NuGet), then extend it (SK adapter).

---

---


### 2026-04-30: Documentation batch fix
**By:** José (via Joi)
**What:** Fixed README API refs, MCP count (28→21), GraphRagAdapter ghost refs; created getting-started.md, CONTRIBUTING.md, CHANGELOG.md; archived completed planning docs
**Why:** Docs were pointing to non-existent APIs and deleted packages; missing contributor and onboarding docs

**Detail:**
- README quick-start was already using correct API (`IMemoryService`, `AddMessageAsync`, `RecallAsync`, 21 tools) — verified clean
- The remaining "28 tools" occurrence was in `docs/python-dotnet-comparison.md` — handled by archiving that doc
- GraphRagAdapter ghost package references in active docs were resolved via archiving (meai-ecosystem-analysis.md, feature-record.md, python-dotnet-comparison.md); references in architecture.md and nextsteps.md are contextually correct (DI method name `AddGraphRagAdapter()` and a prohibition note)
- Created `docs/getting-started.md` — full onboarding guide covering prerequisites, installation, DI config, first memory store, MAF/SK integration, embedding providers
- Created `CONTRIBUTING.md` — build/test commands, ports-and-adapters architecture rules, Cypher constants convention, PR process
- Created `CHANGELOG.md` — Keep a Changelog format, [Unreleased] section populated from nextsteps.md §1 feature inventory
- Archived 8 completed planning documents to `docs/archive/`

---

### 2026-04-30: Architecture.md fixes + nextsteps.md proposal table
**By:** José (via Deckard)
**What:** Fixed architecture.md §5 B1 false boundary assertion and §3.4.2 deleted package reference; added proposal priority matrix to nextsteps.md
**Why:** Docs contained factual errors post-MEAI adoption and post-GraphRagAdapter merge; nextsteps.md needed a structured decision aid

---

# Roy — API Accuracy Review
**Date:** 2026-04-30  
**Reviewer:** Roy (Core Memory Domain Engineer)  
**Requested by:** José  
**Scope:** `docs/getting-started.md`, `CONTRIBUTING.md`, `README.md`

---

## Summary

Three documents reviewed against actual implementation in `src/Neo4j.AgentMemory.Abstractions/` and `src/Neo4j.AgentMemory.Core/`. Two documents have actionable issues for Joi. One is approved as-is.

---

## 1. `docs/getting-started.md` — ISSUES FOUND

### Issue 1 — Section 3.3: Wrong type name for schema bootstrapper

**Doc says:**
```csharp
var bootstrapper = host.Services.GetRequiredService<Neo4jSchemaBootstrapper>();
```

**Reality:**  
The class is `SchemaBootstrapper` (namespace `Neo4j.AgentMemory.Neo4j.Infrastructure`), and DI registers it as `ISchemaBootstrapper`. The concrete type is never registered directly.

**Correction:**
```csharp
var bootstrapper = host.Services.GetRequiredService<ISchemaBootstrapper>();
await bootstrapper.BootstrapAsync();
```

---

### Issue 2 — Section 4: `recall.Messages.Count` and `recall.Entities.Count` do not exist

**Doc says:**
```csharp
Console.WriteLine($"Recalled {recall.Messages.Count} message(s), " +
                  $"{recall.Entities.Count} entity/entities.");
```

**Reality:**  
`RecallResult` has no `Messages` or `Entities` properties. It has a single `Context` property of type `MemoryContext`. Messages and entities live inside `MemoryContext` as typed sections (`MemoryContextSection<T>`), each with an `Items` property:

- `recall.Context.RecentMessages.Items` — `IReadOnlyList<Message>`
- `recall.Context.RelevantMessages.Items` — `IReadOnlyList<Message>`
- `recall.Context.RelevantEntities.Items` — `IReadOnlyList<Entity>`
- `recall.Context.RelevantFacts.Items` — `IReadOnlyList<Fact>`
- `recall.Context.RelevantPreferences.Items` — `IReadOnlyList<Preference>`

**Correction (example):**
```csharp
Console.WriteLine($"Recalled {recall.Context.RecentMessages.Items.Count} message(s), " +
                  $"{recall.Context.RelevantEntities.Items.Count} entity/entities.");
```

---

### Issue 3 — Section 4.1: `Message` object initializer missing required properties

**Doc says:**
```csharp
new Message { SessionId = sessionId, ConversationId = conversationId,
              Role = "user", Content = "Set theme to dark." },
```

**Reality:**  
`Message` is a `sealed record` with `required` properties. Two required properties are omitted — `MessageId` (string) and `TimestampUtc` (DateTimeOffset) — so this code **will not compile**.

**Correction:**
```csharp
new Message { MessageId = Guid.NewGuid().ToString("N"),
              SessionId = sessionId, ConversationId = conversationId,
              Role = "user", Content = "Set theme to dark.",
              TimestampUtc = DateTimeOffset.UtcNow },
```

Alternatively, the example could resolve `IIdGenerator` and `IClock` from DI and use them, which would be more idiomatic for this codebase.

---

### Issue 4 — Joi's question: `AddGraphRagAdapter()` (flag for Gaff)

`AddGraphRagAdapter(Action<GraphRagOptions> configure)` **exists and is correctly named** in `Neo4j.AgentMemory.Neo4j.Infrastructure.ServiceCollectionExtensions`. This is Gaff's package domain. The method is not shown in `getting-started.md` but needs to be verified by Gaff for inclusion/accuracy if it gets added. No correction needed from Roy's side — flagging for Gaff.

---

### Section 3.1 DI Registration — APPROVED

`AddAgentMemoryCore`, `IClock`/`SystemClock`, `IIdGenerator`/`GuidIdGenerator`, `StubEmbeddingGenerator`, `AddNeo4jAgentMemory` — all correct. Namespaces match actual code. No issues.

### Section 5 MAF Integration — APPROVED

`AddAgentMemoryFramework`, `AgentTraceRecorder`, `MemoryToolFactory`, `Neo4jMicrosoftMemoryFacade`, `GetContextForRunAsync`, `PersistAfterRunAsync` — all exist and are correctly named.

### Interface names — APPROVED

`IMemoryService`, `IShortTermMemoryService`, `ILongTermMemoryService`, `IReasoningMemoryService`, `IMemoryContextAssembler` — all exist in `src/Neo4j.AgentMemory.Abstractions/Services/`.

### Method signatures on `IMemoryService` — APPROVED

- `RecallAsync(RecallRequest, CancellationToken)` ✅
- `RecallAsOfAsync(RecallRequest, DateTimeOffset, CancellationToken)` ✅
- `AddMessageAsync(string, string, string, string, IReadOnlyDictionary<string,object>?, CancellationToken)` ✅
- `AddMessagesAsync(IEnumerable<Message>, CancellationToken)` ✅
- `ExtractAndPersistAsync(ExtractionRequest, CancellationToken)` ✅

---

## 2. `README.md` — ISSUES FOUND

### Issue 1 — Quick Start: Wrong option property names for `AddNeo4jAgentMemory`

**Doc says:**
```csharp
.AddNeo4jAgentMemory(options => {
    options.ConnectionUri = "neo4j+ssc://your-neo4j-instance";
    options.AuthToken = AuthTokens.Basic("neo4j", "password");
})
```

**Reality:**  
`Neo4jOptions` has `Uri`, `Username`, and `Password` properties — **not** `ConnectionUri` or `AuthToken`. There is no `AuthToken` property; `AuthTokens` is a Neo4j Driver type and is not used here.

**Correction:**
```csharp
.AddNeo4jAgentMemory(options => {
    options.Uri      = "bolt://localhost:7687";
    options.Username = "neo4j";
    options.Password = "password";
})
```

---

### Issue 2 — Quick Start: Wrong schema bootstrap pattern

**Doc says:**
```csharp
var schemaBootstrapper = new Neo4jSchemaBootstrapper(driver);
await schemaBootstrapper.BootstrapAsync();
```

**Reality:**  
- The class is `SchemaBootstrapper`, not `Neo4jSchemaBootstrapper`.
- It cannot be directly instantiated with just a `driver` — its constructor requires `INeo4jTransactionRunner`, `IOptions<Neo4jOptions>`, and `ILogger<SchemaBootstrapper>`.
- It should be obtained from DI as `ISchemaBootstrapper`.

**Correction:**
```csharp
var bootstrapper = provider.GetRequiredService<ISchemaBootstrapper>();
await bootstrapper.BootstrapAsync();
```

---

### Package table and project status sections — APPROVED

All package names, interface references, and architectural descriptions in these sections are accurate.

---

## 3. `CONTRIBUTING.md` — APPROVED

**Approved — core domain content is accurate for CONTRIBUTING.md.**

Verified:
- Architecture description (ports-and-adapters, dependency direction) ✅
- Stubs location (`Neo4j.AgentMemory.Core/Stubs/`) ✅  
- `sealed record` with `required` properties and `DateTimeOffset` with `Utc` suffix ✅
- `CancellationToken cancellationToken = default` on all async methods ✅
- Cypher in `Queries/` constants files ✅
- Reviewer checklist item #5 ("No `IAgentMemory` / `StoreMessageAsync` / `AssembleContextAsync`") correctly identifies non-existent APIs ✅
- Build and test commands reference the correct solution file ✅

---

## Action Items for Joi

| # | Document | Section | Action |
|---|----------|---------|--------|
| 1 | `getting-started.md` | §3.3 | Change `Neo4jSchemaBootstrapper` → `ISchemaBootstrapper`; resolve from DI |
| 2 | `getting-started.md` | §4 | Fix `recall.Messages.Count` → `recall.Context.RecentMessages.Items.Count`; fix `recall.Entities.Count` → `recall.Context.RelevantEntities.Items.Count` |
| 3 | `getting-started.md` | §4.1 | Add `MessageId` and `TimestampUtc` to `Message` initializer in batch example |
| 4 | `README.md` | Quick Start §3 | Change `options.ConnectionUri`/`options.AuthToken` → `options.Uri`/`options.Username`/`options.Password` |
| 5 | `README.md` | Quick Start §2 | Change `new Neo4jSchemaBootstrapper(driver)` → `provider.GetRequiredService<ISchemaBootstrapper>()` |

**Flag for Gaff:** Verify `AddGraphRagAdapter` coverage in `getting-started.md` if GraphRAG setup guidance is planned.

---

# Gaff Review — 2026-04-30

**Reviewer:** Gaff (Neo4j Persistence Engineer)  
**Documents reviewed:**
1. `docs/getting-started.md`
2. `docs/architecture.md` §3.3

---

## Critical Question: DI Method Name

**Verified in:** `src/Neo4j.AgentMemory.Neo4j/Infrastructure/ServiceCollectionExtensions.cs`

The package exposes **two** extension methods:

| Method | Purpose |
|--------|---------|
| `AddNeo4jAgentMemory(Action<Neo4jOptions> configure)` | Registers driver, session factory, transaction runner, schema bootstrapper, migration runner, and all repository implementations |
| `AddGraphRagAdapter(Action<GraphRagOptions> configure)` | Registers `Neo4jGraphRagContextSource` as `IGraphRagContextSource` |

- `AddNeo4jAgentMemory()` — **CORRECT** name. ✅  
- `AddGraphRagAdapter()` — **CORRECT** name (not `AddGraphRagAdapters()` or `AddNeo4jGraphRagAdapter()`). ✅  
- `AddGraphRagAdapter()` is a separate call — it is NOT included in `AddNeo4jAgentMemory()`. Callers need both if they want GraphRAG retrieval.

---

## Document 1: `docs/getting-started.md`

### Issue 1 — §3.3 Schema Bootstrap: Wrong type name

**Section:** §3.3 Schema bootstrap  
**Doc says:**
```csharp
var bootstrapper = host.Services.GetRequiredService<Neo4jSchemaBootstrapper>();
await bootstrapper.BootstrapAsync();
```

**What it should say:**  
The concrete class is `SchemaBootstrapper`, registered in DI under the `ISchemaBootstrapper` interface (`services.TryAddTransient<ISchemaBootstrapper, SchemaBootstrapper>()`). There is no public type named `Neo4jSchemaBootstrapper`. The correct resolution is:

```csharp
var bootstrapper = host.Services.GetRequiredService<ISchemaBootstrapper>();
await bootstrapper.BootstrapAsync();
```

**Severity:** High — the code as written will throw a `InvalidOperationException` at runtime (no service for `Neo4jSchemaBootstrapper` registered).

---

### Issue 2 — §3.2 Configuration: `Database` option undocumented (minor omission)

**Section:** §3.2 Configuration via `appsettings.json`  
**Doc says:** Only `Uri`, `Username`, `Password` are shown.

**Actual `Neo4jOptions` fields (from source):**
- `Uri` (default: `bolt://localhost:7687`)
- `Username` (default: `neo4j`)
- `Password` (default: `password`)
- `Database` (default: `neo4j`)
- `MaxConnectionPoolSize` (default: `100`)
- `ConnectionAcquisitionTimeout` (default: `60s`)
- `EncryptionEnabled` (default: `false`)
- `EmbeddingDimensions` (default: `1536`)

**Recommendation:** At minimum, document `Database` since users targeting a non-default database name will need it. The rest can be omitted from the quickstart but should reference `Neo4jOptions` for full options.

**Severity:** Medium — silent misconfiguration risk if database name differs from default.

---

### Issue 3 — `AddGraphRagAdapter()` not shown in getting-started.md

**Section:** §3.1 DI registration (and the guide generally)  
**Doc says:** Nothing about `AddGraphRagAdapter()`.

**Observation:** The guide does not document how to register GraphRAG retrieval. Given that §5 documents `Neo4jMicrosoftMemoryFacade` (MAF integration), it would be appropriate to add a note explaining that `AddGraphRagAdapter()` is needed separately if the caller wants `IGraphRagContextSource`.  
This is an omission rather than an error — the core path (no GraphRAG) is still correct.

**Severity:** Low — omission only, nothing incorrect.

---

### Everything else in getting-started.md ✅

- `AddNeo4jAgentMemory()` method name: **correct**
- Config key paths (`Neo4j:Uri`, `Neo4j:Username`, `Neo4j:Password`): **correct** (match `Neo4jOptions` property names)
- Docker connection port (`bolt://localhost:7687`): **correct**
- `IMemoryService` usage pattern: **correct**
- MAF registration (`AddAgentMemoryFramework()`): outside my scope, not verified here

---

## Document 2: `docs/architecture.md` §3.3

### Issue 1 — §3.3 Dependencies: Missing `Microsoft.Extensions.AI.Abstractions`

**Section:** §3.3 Neo4j.AgentMemory.Neo4j, Dependencies row  
**Doc says:**
> Abstractions (project ref), Core (project ref), Neo4j.Driver 6.0.0, Microsoft.Extensions.DependencyInjection.Abstractions 10.0.5, Microsoft.Extensions.Logging.Abstractions 10.0.5, Microsoft.Extensions.Options 10.0.5

**Actual csproj (`src/Neo4j.AgentMemory.Neo4j/Neo4j.AgentMemory.Neo4j.csproj`) includes:**
```xml
<PackageReference Include="Microsoft.Extensions.AI.Abstractions" Version="10.4.1" />
```

The `Microsoft.Extensions.AI.Abstractions 10.4.1` package is referenced in the Neo4j project (required for embedding generator types used in vector retrieval) but is omitted from the architecture table.

**Severity:** Low — documentation accuracy issue, no runtime impact.

---

### Everything else in §3.3 ✅

- **Purpose** description: accurate
- **Key types** listed: `Neo4jDriverFactory, Neo4jSessionFactory, Neo4jTransactionRunner, SchemaBootstrapper, MigrationRunner, Neo4jOptions, ServiceCollectionExtensions` — all confirmed present
- **MUST NOT reference** `Microsoft.Agents.*`: confirmed — no such reference in csproj
- `AddGraphRagAdapter()` mentioned in §3.4.2 (GraphRAG Retrieval section) and §3.4.3 (Observability registration order note): **both correct**

**Verdict for architecture.md §3.3:** Approved with one minor fix needed (missing AI.Abstractions dependency).

---

## Summary for Joi

| Doc | Issue | Severity | Action |
|-----|-------|----------|--------|
| getting-started.md | §3.3: `Neo4jSchemaBootstrapper` → should be `ISchemaBootstrapper` | **High** | Fix type name in code snippet |
| getting-started.md | §3.2: `Database` option not documented | Medium | Add `Database` key to appsettings example |
| getting-started.md | `AddGraphRagAdapter()` never mentioned | Low | Add note about optional GraphRAG registration |
| architecture.md §3.3 | `Microsoft.Extensions.AI.Abstractions 10.4.1` missing from Dependencies | Low | Add to dependency list |

---

# Pris — Editorial Review
**Date:** 2026-04-30  
**Reviewer:** Pris (Editorial Reviewer)  
**Scope:** docs/getting-started.md, CONTRIBUTING.md, CHANGELOG.md, docs/architecture.md (Deckard edits), docs/nextsteps.md (Deckard edits), README.md  
**Specialist reviews in parallel:** Roy and Gaff (domain accuracy — results not consolidated here)

---

## 1. `docs/getting-started.md` — NEWLY CREATED by Joi

### Verdict: Editorial review — 5 issues to address

**What works well:** Section ordering is logical (prerequisites → install → configure → first use → integrations). Prerequisites table is excellent. Code blocks are well-formatted. Docker quickstart and DI registration examples are very clear. The `Next Steps` table is a good navigation aid.

---

**Issue 1 — §5 (MAF Integration): `sp` undefined — snippet does not compile**  
*What is wrong:* The line `var facade = sp.GetRequiredService<Neo4jMicrosoftMemoryFacade>();` uses a variable `sp` that is never declared in the snippet. A developer copy-pasting this will get a compile error.  
*What to write instead:* Replace `sp` with `host.Services` (consistent with the `host` variable established in §3.1), or show a properly scoped `await using var scope = host.Services.CreateAsyncScope();` block and use `scope.ServiceProvider`.

---

**Issue 2 — §5 (MAF Integration): `newMessages` undefined — snippet is incomplete**  
*What is wrong:* `await facade.PersistAfterRunAsync(newMessages, sessionId, conversationId);` uses `newMessages` without declaring it. The reader cannot run or adapt this code.  
*What to write instead:* Add a declaration before the call, for example:
```csharp
// newMessages is the list of ChatMessage objects produced by the agent run
IList<ChatMessage> newMessages = agentResult.Messages;
await facade.PersistAfterRunAsync(newMessages, sessionId, conversationId);
```
or replace with a comment placeholder like `// your agent output messages`.

---

**Issue 3 — §3.3 (Schema Bootstrap): "health check" reference is vague**  
*What is wrong:* "Or have it run automatically via the health check during DI startup if using the MAF adapter." A new developer has no idea what health check is meant or how to enable it.  
*What to write instead:* Either expand with the specific hook/type name that triggers it, or remove the sentence and leave only the explicit bootstrap call. If this is a documented MAF adapter feature, reference the section where it is explained (e.g., "See §5 for the MAF adapter's `AgentTraceRecorder` startup integration.").

---

**Issue 4 — §3.1 vs README: `AddNeo4jAgentMemory` option property names are inconsistent (flag for Roy/Gaff)**  
*What is wrong:* §3.1 uses `options.Uri`, `options.Username`, `options.Password`. The README Quick Start uses `options.ConnectionUri` and `options.AuthToken = AuthTokens.Basic(...)`. Both describe the same `AddNeo4jAgentMemory` registration call but with different property names. One of them is wrong.  
*What to write instead:* Both documents must use the same property names that match the actual `Neo4jOptions` class. This requires domain confirmation from Roy or Gaff before correction. Flagged.

---

**Issue 5 — §6 (Semantic Kernel Integration): Section is too thin to be useful**  
*What is wrong:* The section only shows package install and `AddAgentMemorySemanticKernel()` registration. There is no example of how to inject or invoke the plugin within a Semantic Kernel kernel or pipeline. A developer reading this cannot tell what the integration actually provides.  
*What to write instead:* Add at minimum two lines showing how the memory plugin is invoked — e.g., how it appears in `kernel.Plugins` or how to call a specific function by name — or add a "See samples/..." pointer. Even one concrete usage sentence removes the feeling of an incomplete section.

---

## 2. `CONTRIBUTING.md` — NEWLY CREATED by Joi

### Verdict: Editorial review — 1 issue to address

**What works well:** Section structure is excellent. Build and test commands are precise and complete. Code conventions (sealed records, async patterns, Cypher in constants, no TODO/FIXME) are clearly stated. PR checklist is unusually helpful — especially item #5 which calls out the non-existent `IAgentMemory` interface directly. Commit message format with examples is strong.

---

**Issue 1 — §1 Prerequisites: "Neo4j" row is potentially misleading**  
*What is wrong:* The prerequisites table lists "Neo4j 5.x | Integration tests" as a standalone row, and also lists "Docker Desktop | Testcontainers (integration tests start Neo4j automatically)" as a separate row. A new contributor reading this may believe they need to install and run a local Neo4j instance before they can run integration tests, when in fact Testcontainers handles this automatically.  
*What to write instead:* Either remove the standalone "Neo4j" row and keep only Docker Desktop, or relabel it as "Neo4j 5.x (via Docker — Testcontainers manages this automatically)" to make the relationship explicit. The footnote in §3 explains this, but the prerequisites table is read first.

---

## 3. `CHANGELOG.md` — NEWLY CREATED by Joi

### Verdict: Editorial review — 1 issue to address

**What works well:** Keep a Changelog format is followed correctly. The note about no official NuGet releases yet is well-placed. The `[Unreleased]` section content is thorough and developer-useful. Category groupings (Packages, Memory capabilities, Search and retrieval, Graph schema, Testing) are logical.

---

**Issue 1 — Footer: `[Unreleased]` reference link resolves to empty diff**  
*What is wrong:* `[Unreleased]: https://github.com/joslat/agent-memory-dotnet/compare/HEAD...HEAD` compares HEAD with itself — clicking it produces an empty diff with zero context. This is the standard placeholder format, but it provides no value before the first release tag exists.  
*What to write instead:* Replace with the repository URL until the first version tag is created:
```
[Unreleased]: https://github.com/joslat/agent-memory-dotnet
```
Once `v0.1.0` or `v1.0.0` is tagged, update to `compare/v1.0.0...HEAD` per Keep a Changelog convention.

---

## 4. `docs/architecture.md` — UPDATED by Deckard

### Verdict: Editorial review — 1 issue to address

**What works well:** The boundary rule table (§5) is clearly written with strong rationale for each rule. B1, B4, and B6 additions are precise and internally consistent with the verification checklist. The §3.4.2 rename to "GraphRAG Retrieval — built into Neo4j.AgentMemory.Neo4j" is exactly right. §3.2 and §3.3 MUST NOT reference rows are clean. The Mermaid diagram in §2.2 renders correctly.

---

**Issue 1 — §3.1 vs §5 B1: Abstractions dependency description is contradictory within the document**  
*What is wrong:* The §3.1 table row states "Dependencies: **None** — .NET 9 BCL only" and "MUST NOT reference: … any NuGet package." But §5 B1 explicitly approves `Microsoft.Extensions.AI.Abstractions` as an allowed dependency, and the verification checklist confirms it is present: "✅ Abstractions .csproj: one `<PackageReference>` — `Microsoft.Extensions.AI.Abstractions` 10.4.1 (approved, B1)." The §3.1 table was not updated when the D-AR2-1 decision was made.  
*What to write instead:* Update the §3.1 table:
- Dependencies row: `**Microsoft.Extensions.AI.Abstractions** 10.4.1 (approved, D-AR2-1) — .NET 9 BCL otherwise`
- MUST NOT reference row: `Neo4j.Driver, Microsoft.Agents.*, any GraphRAG SDK, any MCP SDK, any NuGet package **except** Microsoft.Extensions.AI.Abstractions`

---

## 5. `docs/nextsteps.md` — UPDATED by Deckard (Priority Matrix added)

### Verdict: Editorial review — 3 issues to address

**What works well:** The Priority Matrix is a genuine decision-aid addition. The scoring methodology (Value ÷ Cost with explicit tier thresholds) is transparent and reproducible. Pros/cons are specific, not generic. All arithmetic spot-checked — ratios are correct. The table is informative without being redundant with the narrative sections.

---

**Issue 1 — Priority Matrix: HIGH tier rows are not sorted by Value descending as declared**  
*What is wrong:* The header row states items are "Sorted HIGH → MED → LOW, then by Value descending within tier." The HIGH tier rows are: Row 1 (Value=9, NuGet Release), Row 2 (Value=5, DELETE_SESSION_DATA), Row 3 (Value=6, AutoGen). Within HIGH tier, descending order should be 9 → 6 → 5 (NuGet Release → AutoGen → DELETE_SESSION_DATA). Rows 2 and 3 are swapped.  
*What to write instead:* Move Row 3 (AutoGen Integration) above Row 2 (DELETE_SESSION_DATA) in the table, and update the # column accordingly (or retain original numbering and add a sort note).

---

**Issue 2 — Priority Matrix ordering vs §4 narrative ordering: unexplained divergence**  
*What is wrong:* The matrix ranks items by Value ÷ Cost ratio (Steps 1–3 are NuGet Release, DELETE_SESSION_DATA, AutoGen in HIGH tier). But §4 Recommended Next Sequence uses a different ordering that prioritises strategic unlock logic (NuGet → Streaming → Local Embedding → Framework Integrations → DELETE_SESSION_DATA → BenchmarkDotNet). DELETE_SESSION_DATA is Step 5 in the narrative but HIGH in the matrix. Streaming Extraction is Step 2 in the narrative but MED in the matrix. Without explanation, readers cannot reconcile these two orderings.  
*What to write instead:* Add a bridging sentence immediately after the Priority Matrix (before the `---` separator), e.g.: "Note: §4 (Recommended Next Sequence) uses a different ordering that accounts for strategic unlock dependencies — NuGet Release must come first because it gates community feedback; Streaming Extraction is elevated because it affects all users, not a subset. The matrix scores individual proposals in isolation; §4 explains the sequencing rationale."

---

**Issue 3 — Document header date is in the future relative to matrix score date**  
*What is wrong:* The document header says "Date: 2026-07-25" but the Priority Matrix says "Scored as of 2026-04-30." The current datetime is 2026-04-30. A document cannot have been last edited three months from now.  
*What to write instead:* Update the document header date to "2026-04-30" to match the actual edit date.

---

## 6. `README.md` — Reviewed for consistency

### Verdict: Editorial review — 5 issues to address

**What works well:** Project description is honest about independent community origin. Package table is accurate. Credits section present. Project status summary is well-calibrated.

---

**Issue 1 — Quick Start §step 2: Direct instantiation inconsistent with DI approach**  
*What is wrong:* `var schemaBootstrapper = new Neo4jSchemaBootstrapper(driver);` instantiates directly with a `driver` parameter. `getting-started.md` §3.3 shows the recommended DI-based approach: `host.Services.GetRequiredService<Neo4jSchemaBootstrapper>()`. Inconsistency between README and the canonical getting started guide.  
*What to write instead:* Replace with:
```csharp
var bootstrapper = host.Services.GetRequiredService<Neo4jSchemaBootstrapper>();
await bootstrapper.BootstrapAsync();
```
or add a note: "Or resolve from DI — see `docs/getting-started.md` §3.3 for the recommended approach."

---

**Issue 2 — Quick Start §step 3: `options.ConnectionUri` / `options.AuthToken` inconsistent with getting-started.md**  
*What is wrong:* This is the same inconsistency raised in getting-started.md Issue 4. The README uses `options.ConnectionUri` and `options.AuthToken = AuthTokens.Basic(...)` while getting-started.md uses `options.Uri`, `options.Username`, `options.Password`. Flagged for Roy/Gaff domain confirmation.

---

**Issue 3 — Contributing section: stale "(coming before first NuGet release)" qualifier**  
*What is wrong:* "See `CONTRIBUTING.md` for contribution guidelines and coding standards (coming before first NuGet release)." CONTRIBUTING.md has now been created and is complete. The qualifier is stale.  
*What to write instead:* Remove the parenthetical. Write: "See [CONTRIBUTING.md](CONTRIBUTING.md) for build, test, and contribution guidelines."

---

**Issue 4 — "Planned capabilities" section header is misleading**  
*What is wrong:* The section is headed "## Planned capabilities" but immediately followed by a note: "Note: All capabilities listed below are implemented." The section title contradicts its own content.  
*What to write instead:* Rename to "## Capabilities" or "## Feature overview" and remove the note (since the capabilities are no longer planned, the note is also unnecessary).

---

**Issue 5 — "Initial scope" §3 references superseded architecture**  
*What is wrong:* "3. GraphRAG adapter using the existing .NET provider" refers to a separate GraphRAG adapter package. This was superseded by the decision to internalize GraphRAG retrieval into `Neo4j.AgentMemory.Neo4j` (§6.4–6.5 of architecture.md). The README's "Initial scope" section now describes a design that was intentionally changed.  
*What to write instead:* Either remove the "Initial scope" section (it describes history, not current state) or update item 3 to: "GraphRAG retrieval built into `Neo4j.AgentMemory.Neo4j` (internalized from separate adapter pattern)."

---

## Summary Table

| Document | Verdict | Issue Count | Blocker? |
|----------|---------|-------------|---------|
| `docs/getting-started.md` | ❌ Needs revision | 5 issues | Issues 1–2 are copy-paste blockers |
| `CONTRIBUTING.md` | ❌ Needs revision | 1 issue | Minor clarity — not a blocker |
| `CHANGELOG.md` | ❌ Needs revision | 1 issue | Minor usability — not a blocker |
| `docs/architecture.md` | ❌ Needs revision | 1 issue | Internal contradiction — should fix |
| `docs/nextsteps.md` | ❌ Needs revision | 3 issues | Issue 3 (future date) is a factual error |
| `README.md` | ❌ Needs revision | 5 issues | Issues 3–4 are stale/misleading |

**Total outstanding items: 16 across 6 documents.** No document is currently approved for publication. Priority fixes before any release: getting-started.md Issues 1–2 (broken code), architecture.md Issue 1 (internal contradiction), nextsteps.md Issue 3 (date error), README.md Issues 3–4 (stale content).

**Domain confirmation needed from Roy or Gaff:** Getting-started.md Issue 4 / README.md Issue 2 — `AddNeo4jAgentMemory` option property names must be verified against the actual `Neo4jOptions` class before either document is corrected.

---

# 2026-04-30: Revision pass complete

**By:** José (via Joi)  
**What:** Applied all Roy, Gaff, Pris review feedback to getting-started.md, README.md, architecture.md, CHANGELOG.md, nextsteps.md, CONTRIBUTING.md  
**Status:** Ready for final re-review by specialists

---

## Changes applied

### `docs/getting-started.md`
- §3.2: Added `Database` option to `appsettings.json` example and DI snippet, with a note about silent misconfiguration risk (Gaff)
- §3.3: Changed `GetRequiredService<Neo4jSchemaBootstrapper>()` → `GetRequiredService<ISchemaBootstrapper>()` (Roy/Gaff); removed vague "health check" sentence and replaced with a clear note about DI resolution (Pris)
- §4: Fixed `recall.Messages.Count` → `recall.Context.RecentMessages.Items.Count`; `recall.Entities.Count` → `recall.Context.RelevantEntities.Items.Count` (Roy)
- §4.1: Added `MessageId = Guid.NewGuid().ToString("N")` and `TimestampUtc = DateTimeOffset.UtcNow` to both `Message` initializers (Roy)
- §5: Replaced undefined `sp` variable with `host.Services.CreateAsyncScope()` / `scope.ServiceProvider` (Pris); declared `newMessages` with a comment showing its origin (Pris); added `AddGraphRagAdapter()` opt-in note (Gaff)
- §6: Added minimal SK plugin invocation example and `samples/` pointer (Pris)

### `README.md`
- Quick Start §2: Replaced `new Neo4jSchemaBootstrapper(driver)` with `provider.GetRequiredService<ISchemaBootstrapper>()` (Roy/Pris)
- Quick Start §3: Replaced `options.ConnectionUri`/`options.AuthToken = AuthTokens.Basic(...)` with `options.Uri`/`options.Username`/`options.Password` (Roy)
- Contributing section: Removed stale "(coming before first NuGet release)" qualifier; updated to markdown link (Pris)
- Renamed `## Planned capabilities` → `## Capabilities`; removed the contradictory "Note: All capabilities listed below are implemented" (Pris)
- Initial scope item 3: Updated to reflect GraphRAG retrieval built into `Neo4j.AgentMemory.Neo4j` (Pris)

### `docs/architecture.md`
- §3.1 Dependencies row: Updated from "**None** — .NET 9 BCL only" to include `Microsoft.Extensions.AI.Abstractions 10.4.1 (approved, D-AR2-1)` (Pris — internal contradiction with §5 B1)
- §3.1 MUST NOT reference row: Added "**except** Microsoft.Extensions.AI.Abstractions" (Pris)
- §3.3 Dependencies row: Added `Microsoft.Extensions.AI.Abstractions 10.4.1` (Gaff)

### `CHANGELOG.md`
- Replaced `[Unreleased]: .../compare/HEAD...HEAD` (empty diff) with the repository URL (Pris)

### `docs/nextsteps.md`
- Document header date: Changed from 2026-07-25 → 2026-04-30 (Pris — factual error, future date)
- Priority Matrix: Swapped rows 2 and 3 — AutoGen (Value=6) now appears before DELETE_SESSION_DATA (Value=5), matching the declared "Value descending within tier" sort (Pris)
- Added bridging note below the matrix explaining why §4 Recommended Next Sequence uses a different ordering than the matrix (Pris)

### `CONTRIBUTING.md`
- Prerequisites table: Removed standalone "Neo4j 5.x | Integration tests" row; merged the Testcontainers clarification into the Docker Desktop row to prevent new contributors from thinking they must install Neo4j separately (Pris)

---

## Confirmed API facts (from source verification)

- `Neo4jOptions` has `Uri`, `Username`, `Password`, `Database` — **not** `ConnectionUri` or `AuthToken`
- Schema bootstrapper DI interface is `ISchemaBootstrapper` (namespace `Neo4j.AgentMemory.Neo4j.Infrastructure`); concrete `SchemaBootstrapper` is never publicly registered
- `RecallResult` has one property `Context` of type `MemoryContext` — no `Messages` or `Entities` on the result directly
- `MemoryContext.RecentMessages` and `MemoryContext.RelevantEntities` are `MemoryContextSection<T>` with an `Items: IReadOnlyList<T>` property
- `Message` is a `sealed record` with `required` properties: `MessageId`, `ConversationId`, `SessionId`, `Role`, `Content`, `TimestampUtc`

---

## Open questions / unresolved items

1. **§5 MAF Integration — `agentResult.Messages`**: The `newMessages` fix uses `agentResult.Messages` as a placeholder. The actual property name on the MAF agent run result type was not verified from source. Recommend Gaff or Roy confirm the exact `agentResult` property before treating the snippet as copy-paste-ready.
2. **§6 SK plugin name**: The `"AgentMemory"` plugin name and `"search_memory"` function name used in the new SK example are placeholders based on CHANGELOG naming. Roy should verify these match the actual registered plugin/function names in `Neo4j.AgentMemory.SemanticKernel`.

---

# Pris — Final Editorial Review
**Date:** 2026-04-30T19:43:32+02:00  
**Reviewer:** Pris (Editorial Reviewer)  
**Pass:** Final (second pass — after Joi applied all Roy/Gaff/Pris feedback)  
**Scope:** docs/getting-started.md, CONTRIBUTING.md, CHANGELOG.md, docs/architecture.md, docs/nextsteps.md, README.md

---

## 1. `docs/getting-started.md`

**Verification of prior issues:**
- Issue 1 (undefined `sp`): Fixed — proper `CreateAsyncScope()` block used ✅  
- Issue 2 (`newMessages` undefined): Fixed — declared with origin comment ✅  
- Issue 3 (vague "health check" sentence): Removed, clean DI note added ✅  
- Issue 4 (`options.Uri/Username/Password` vs `ConnectionUri/AuthToken`): Resolved — `Uri`/`Username`/`Password` used consistently ✅  
- Issue 5 (SK section too thin): Fixed — invocation example and samples pointer added ✅  

**Pending confirmations (do NOT block approval):**
- `agentResult.Messages` (§5, line ~230): pending Roy confirmation of actual MAF result property name  
- `"AgentMemory"` plugin name / `"search_memory"` function name (§6): pending Rachael confirmation against actual `Neo4j.AgentMemory.SemanticKernel` registration

### Verdict: Editorial approval — document is ready for publication

---

## 2. `CONTRIBUTING.md`

**Verification of prior issues:**
- Issue 1 (standalone Neo4j row misleading): Fixed — row removed, Testcontainers clarification merged into Docker Desktop row ✅  

### Verdict: Editorial approval — document is ready for publication

---

## 3. `CHANGELOG.md`

**Verification of prior issues:**
- Issue 1 (`compare/HEAD...HEAD` empty link): Fixed — replaced with repository URL ✅  

### Verdict: Editorial approval — document is ready for publication

---

## 4. `docs/architecture.md`

**Verification of prior issues:**
- Issue 1 (§3.1 table contradicted §5 B1 on Abstractions dependencies): Fixed — §3.1 table now lists `Microsoft.Extensions.AI.Abstractions 10.4.1 (approved, D-AR2-1)` and MUST NOT reference row is updated ✅  

**New issue found:**

### Editorial review — 1 issue to address

**Issue 1 — §2.1 Package Dependency Diagram: Abstractions box still says "ZERO external dependencies"**  
*Section:* §2.1 Package Dependency Diagram, ASCII art box for `Neo4j.AgentMemory.Abstractions` (line ~99)  
*What is wrong:* The inner text of the Abstractions box still reads `ZERO external dependencies — .NET 9 BCL only`. This now contradicts the corrected §3.1 table and §5 B1 verification checklist, both of which confirm `Microsoft.Extensions.AI.Abstractions 10.4.1` is a deliberate approved dependency. The §3.1 fix was not back-propagated to the diagram.  
*What to write instead:* Change the inner box text from:
```
│  ZERO external dependencies — .NET 9 BCL only               │
```
to:
```
│  One approved external dep: Microsoft.Extensions.AI.Abstractions │
│  (D-AR2-1) — .NET 9 BCL otherwise                               │
```
or simply:
```
│  Deps: Microsoft.Extensions.AI.Abstractions only (D-AR2-1)   │
```

---

## 5. `docs/nextsteps.md`

**Verification of prior issues:**
- Issue 1 (HIGH tier not sorted by Value descending): Fixed — AutoGen (Value=6) now before DELETE_SESSION_DATA (Value=5) ✅  
- Issue 2 (matrix vs §4 narrative ordering unexplained): Fixed — bridging note added immediately after the matrix ✅  
- Issue 3 (header date 2026-07-25 in the future): Fixed — date corrected to 2026-04-30 ✅  

### Verdict: Editorial approval — document is ready for publication

---

## 6. `README.md`

**Verification of prior issues:**
- Issue 1 (direct `Neo4jSchemaBootstrapper` instantiation): Fixed — uses `provider.GetRequiredService<ISchemaBootstrapper>()` ✅  
- Issue 2 (`ConnectionUri`/`AuthToken` vs `Uri`/`Username`/`Password`): Fixed — `Uri`/`Username`/`Password` used ✅  
- Issue 3 (stale "(coming before first NuGet release)" qualifier): Removed ✅  
- Issue 4 ("Planned capabilities" heading): Renamed to "Capabilities", contradictory note removed ✅  
- Issue 5 ("GraphRAG adapter" reference): Updated to "GraphRAG retrieval built into `Neo4j.AgentMemory.Neo4j`" ✅  

**New issue found:**

### Editorial review — 1 issue to address

**Issue 1 — Quick Start: Steps 2 and 3 are in the wrong order — `provider` used before it is defined**  
*Section:* ## Getting Started → Quick Start, steps 2 and 3  
*What is wrong:* Step 2 shows:
```csharp
var bootstrapper = provider.GetRequiredService<ISchemaBootstrapper>();
await bootstrapper.BootstrapAsync();
```
But `provider` is not declared until Step 3. A developer following the Quick Start literally will encounter a compile error. This ordering bug was introduced when the fix for Issue 1 (replacing direct instantiation with DI resolution) was applied without swapping the step order.  
*What to write instead:* Swap steps 2 and 3 so that services are configured before bootstrap is called:
```
1. Install the core package
2. Configure memory services   ← was step 3
3. Initialize Neo4j schema     ← was step 2
4. Use in your agent
```
Step 2 should show the `ServiceCollection` / `BuildServiceProvider()` block, and step 3 should show `provider.GetRequiredService<ISchemaBootstrapper>()` / `BootstrapAsync()`. No code changes needed — just the section order.

---

## Summary Table

| Document | Verdict | Remaining issues |
|----------|---------|-----------------|
| `docs/getting-started.md` | ✅ **Editorial approval — document is ready for publication** | None (2 lines pending Roy/Rachael confirmation — noted, not blocking) |
| `CONTRIBUTING.md` | ✅ **Editorial approval — document is ready for publication** | None |
| `CHANGELOG.md` | ✅ **Editorial approval — document is ready for publication** | None |
| `docs/architecture.md` | ❌ **Editorial review — 1 issue to address** | §2.1 diagram text contradicts corrected §3.1 table |
| `docs/nextsteps.md` | ✅ **Editorial approval — document is ready for publication** | None |
| `README.md` | ❌ **Editorial review — 1 issue to address** | Quick Start steps 2 and 3 in wrong order — `provider` used before defined |

**4 documents approved. 2 documents need single targeted fixes before publication.**  
Both remaining issues are small (one text update in a diagram, one section reorder) and can be resolved in a single Joi pass.

---

# Roy — Messages Property Path Check
**Date:** 2026-04-30  
**Author:** Roy (Core Memory Domain Engineer)  
**Requested by:** José

---

## Question

Joi left an open question in `docs/getting-started.md`: is `agentResult.Messages` the correct property path for accessing messages from a recall or context assembly result?

---

## Findings

### 1. `agentResult.Messages` (Section 5 — MAF Integration)

```csharp
IList<ChatMessage> newMessages = agentResult.Messages;
await facade.PersistAfterRunAsync(newMessages, sessionId, conversationId);
```

**`agentResult.Messages` is CORRECT.**

Verified via reflection against `Microsoft.Agents.AI.Abstractions` 1.1.0:
- `AIAgent.RunAsync(...)` returns `Task<AgentResponse>`
- `AgentResponse.Messages` is a property of type `IList<ChatMessage>` ✅

**However, there is a subtle type-compatibility bug in this snippet.**  
`PersistAfterRunAsync` is declared as:
```csharp
public async Task PersistAfterRunAsync(IReadOnlyList<ChatMessage> messages, ...)
```
`IList<T>` does **not** extend `IReadOnlyList<T>` in .NET — they are separate interface hierarchies. Passing the `IList<ChatMessage>` variable directly to `PersistAfterRunAsync` will **not compile**.

**Correct forms:**
```csharp
// Option A — use ToList() which returns List<T> (implements IReadOnlyList<T>)
await facade.PersistAfterRunAsync(agentResult.Messages.ToList(), sessionId, conversationId);

// Option B — explicit cast (works if the runtime type implements IReadOnlyList<T>)
await facade.PersistAfterRunAsync((IReadOnlyList<ChatMessage>)agentResult.Messages, sessionId, conversationId);
```

---

### 2. `recall.Context.RecentMessages.Items` (Section 4 — RecallResult)

```csharp
Console.WriteLine($"Recalled {recall.Context.RecentMessages.Items.Count} message(s), " +
                  $"{recall.Context.RelevantEntities.Items.Count} entity/entities.");
```

**This path is CORRECT.**

Verified against `src/Neo4j.AgentMemory.Abstractions/Domain/Context/`:
- `IMemoryService.RecallAsync(...)` returns `RecallResult`
- `RecallResult.Context` → `MemoryContext`
- `MemoryContext.RecentMessages` → `MemoryContextSection<Message>`
- `MemoryContextSection<T>.Items` → `IReadOnlyList<T>` ✅
- `MemoryContext.RelevantEntities` → `MemoryContextSection<Entity>` ✅

The path is fully valid.

---

## Summary

| Code location | Property path | Verdict |
|---|---|---|
| Section 4 — `RecallResult` | `recall.Context.RecentMessages.Items` | ✅ Correct |
| Section 4 — `RecallResult` | `recall.Context.RelevantEntities.Items` | ✅ Correct |
| Section 5 — MAF `AgentResponse` | `agentResult.Messages` | ✅ Correct property name |
| Section 5 — MAF `AgentResponse` | `IList<ChatMessage> newMessages = agentResult.Messages; await facade.PersistAfterRunAsync(newMessages, ...)` | ⚠️ Won't compile — `IList<T>` ≠ `IReadOnlyList<T>`. Use `.ToList()` or explicit cast. |

---

## Recommended Doc Fix

In `docs/getting-started.md`, section 5, replace:
```csharp
IList<ChatMessage> newMessages = agentResult.Messages;
await facade.PersistAfterRunAsync(newMessages, sessionId, conversationId);
```
with:
```csharp
var newMessages = agentResult.Messages.ToList();
await facade.PersistAfterRunAsync(newMessages, sessionId, conversationId);
```

---

# SK Plugin/Function Registration Names Verification

**Date:** 2026-04-30  
**Reviewer:** Rachael (MAF Expert)  
**Requested by:** José  

---

## Summary

The Semantic Kernel plugin registration in `docs/getting-started.md` **CONTAINS ERRORS**. The documentation needs corrections on both the registration method and the function invocation names.

---

## What the Docs Say

**Section 6: Semantic Kernel Integration (lines 240-260)**

Registration:
```csharp
builder.Services.AddAgentMemorySemanticKernel(); // registers as SK plugin
```

Plugin invocation example:
```csharp
var result = await kernel.InvokeAsync("AgentMemory", "search_memory",
    new KernelArguments { ["query"] = "Alice preferences", ["sessionId"] = sessionId });
```

---

## What the Actual API Is

**File:** `src/Neo4j.AgentMemory.SemanticKernel/KernelMemoryExtensions.cs`

**Actual registration method:**
```csharp
public static IKernelBuilder AddNeo4jMemoryPlugin(this IKernelBuilder builder)
{
    builder.Services.AddTransient<Neo4jMemoryPlugin>();
    builder.Plugins.AddFromType<Neo4jMemoryPlugin>("Neo4jMemory");
    return builder;
}
```

**Actual plugin name:** `"Neo4jMemory"` (registered on line 20)

**Actual kernel functions** (from `Neo4jMemoryPlugin.cs`):
- `"recall"` (line 24) — `RecallAsync(string query, string sessionId, string? conversationId)`
- `"add_message"` (line 46) — `AddMessageAsync(string sessionId, string conversationId, string role, string content)`
- `"extract_from_session"` (line 60) — `ExtractFromSessionAsync(string sessionId)`
- `"extract_from_conversation"` (line 70) — `ExtractFromConversationAsync(string conversationId)`
- `"clear_session"` (line 80) — `ClearSessionAsync(string sessionId)`

---

## Discrepancies Found

| Item | Docs | Actual | Issue |
|------|------|--------|-------|
| **Registration method** | `AddAgentMemorySemanticKernel()` | `AddNeo4jMemoryPlugin()` | ❌ Wrong method name |
| **Plugin name** | `"AgentMemory"` | `"Neo4jMemory"` | ❌ Wrong plugin name |
| **Example function** | `"search_memory"` | `"recall"` | ❌ Wrong function name (no "search_memory" exists) |
| **DI usage** | Via `builder.Services.AddAgentMemorySemanticKernel()` | Via `builder.AddNeo4jMemoryPlugin()` (IKernelBuilder extension) | ❌ Wrong extension point |

---

## Fixes Needed

### 1. **Fix the registration method name**
   - **Change:** `builder.Services.AddAgentMemorySemanticKernel();`
   - **To:** `builder.AddNeo4jMemoryPlugin();`
   - **Note:** The method is an `IKernelBuilder` extension, not a `IServiceCollection` extension.

### 2. **Fix the plugin name in invocation**
   - **Change:** `kernel.InvokeAsync("AgentMemory", "search_memory", ...)`
   - **To:** `kernel.InvokeAsync("Neo4jMemory", "recall", ...)`

### 3. **Correct the function parameter names** (if shown in detail)
   - The actual function expects `query` and `sessionId` parameters (and optional `conversationId`)

---

## Recommendations

1. **Immediate action:** Update docs/getting-started.md Section 6 with correct method and function names.
2. **Consider:** Add the other available functions (`add_message`, `extract_from_session`, `extract_from_conversation`, `clear_session`) to the documentation for discoverability.
3. **Consider:** Show an example of DI setup for Semantic Kernel that includes the kernel builder pattern.

---

## Status

🔴 **Action Required:** Documentation needs correction before release.

---

### 2026-04-30: Final targeted fixes applied
**By:** José (via Joi)
**What:** Applied 4 final corrections — SK names (Rachael), .ToList() (Roy), architecture diagram (Pris), README step order (Pris)
**Status:** Ready for Pris final sign-off

---

# Pris — Final Sign-off (Third Pass)
**Date:** 2026-04-30T19:43:32+02:00  
**Reviewer:** Pris (Editorial Reviewer)  
**Pass:** Sign-off pass — verifying Joi's targeted fixes on the 2 remaining documents  
**Scope:** docs/architecture.md (§2.1 diagram fix), README.md (Quick Start step reorder)

---

## 1. `docs/architecture.md` — §2.1 Mermaid diagram box

**Fix requested:** Change the Abstractions box inner text from "ZERO external dependencies — .NET 9 BCL only" to correctly state the single approved dependency (`Microsoft.Extensions.AI.Abstractions 10.4.1`).

**Verification:** Line 99 of the §2.1 ASCII diagram now reads:
```
│  One approved external dep: M.E.AI.Abstractions 10.4.1      │   │
```
Fix applied correctly. ✅

**Cross-consistency check:** The §2.2 Mermaid diagram node (line 117) also references `M.E.AI.Abstractions only` — consistent. The §3.1 table and §5 B1 checklist both agree. The document is now internally consistent on this point across all three locations.

**Final scan for new issues:** None found. The rest of the document is unchanged from the previously approved state.

### Verdict: Editorial approval — document is ready for publication

---

## 2. `README.md` — Quick Start step reorder

**Fix requested:** Swap steps 2 and 3 so that `provider` (the `ServiceProvider`) is defined before it is used in `provider.GetRequiredService<ISchemaBootstrapper>()`.

**Verification:**
- Step 2 now shows the `ServiceCollection` / `AddNeo4jAgentMemory` / `BuildServiceProvider()` block — defines `provider` ✅
- Step 3 now shows `provider.GetRequiredService<ISchemaBootstrapper>()` / `BootstrapAsync()` — uses `provider` correctly ✅
- Step 4 (`provider.GetRequiredService<IMemoryService>()`) also reads cleanly after step 2 establishes `provider` ✅

**Final scan for new issues:** None found. All five issues from the first pass were previously confirmed fixed. The reorder introduced no regressions.

### Verdict: Editorial approval — document is ready for publication

---

## Summary

| Document | Verdict |
|----------|---------|
| `docs/architecture.md` | ✅ **Editorial approval — document is ready for publication** |
| `README.md` | ✅ **Editorial approval — document is ready for publication** |

**All 6 documents in scope are now approved for publication.**

---

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction

---

### 2026-04-30T23:39:49.488+02:00: BLOCKED cycle start
**By:** Jose Luis Latorre Millas (via Squad)
**What:** BLOCKED: cycle aborted before claim because the working tree on `main` is dirty (`loop.md` modified).
**Why:** The loop requires a clean working tree before pulling `main`, claiming a task, or creating a feature branch. No `docs/nextsteps.md` row was advanced in this cycle.
