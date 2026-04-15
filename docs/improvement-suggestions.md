# Architecture Improvement Suggestions

**Author:** Deckard (Lead / Solution Architect)  
**Requested by:** Jose Luis Latorre Millas  
**Date:** July 2026  
**Scope:** Full architecture audit of all 10 source packages + improvement roadmap

---

## 1. Executive Summary

The Agent Memory for .NET solution is **architecturally sound** — zero boundary violations, zero circular dependencies, clean ports-and-adapters layering, and 1,058 passing unit tests across 265 source files (~14,650 LOC). The dependency direction is correct at every level: adapters depend inward, Core depends only on Abstractions, and Abstractions has zero external dependencies. However, this audit identifies **14 actionable improvements** focused on DRY violations (especially embedding generation scattered across 5 call sites and near-identical extraction projects), oversized classes (MemoryExtractionPipeline at 393 LOC doing extraction + validation + resolution + persistence), and package consolidation opportunities (10 packages could be pragmatically reduced to 7). The most impactful change is merging Extraction.Llm and Extraction.AzureLanguage into a unified Extraction package with a strategy pattern — this eliminates ~95% structural duplication while enabling runtime strategy selection.

---

## 2. Architecture Audit Results

### Per-Project Assessment

| # | Project | Files | LOC | SRP | Dependencies | DRY | ISP | KISS | Coupling | Cohesion | Score |
|---|---------|-------|-----|-----|-------------|-----|-----|------|----------|----------|-------|
| 1 | **Abstractions** | 107 | 3,347 | ✅ | ✅ Zero deps | ✅ | ⚠️ | ✅ | ✅ | ✅ | **9/10** |
| 2 | **Core** | 41 | 3,433 | ⚠️ | ✅ Abstractions only | ❌ | ✅ | ⚠️ | ✅ | ⚠️ | **7/10** |
| 3 | **Neo4j** | 28 | 2,918 | ✅ | ✅ Abstractions + Core | ✅ | ✅ | ⚠️ | ✅ | ✅ | **8/10** |
| 4 | **AgentFramework** | 14 | 943 | ✅ | ✅ Abstractions + Core | ✅ | ✅ | ✅ | ✅ | ✅ | **10/10** |
| 5 | **GraphRagAdapter** | 10 | 476 | ✅ | ✅ Abstractions + neo4j-maf | ✅ | ✅ | ✅ | ✅ | ✅ | **10/10** |
| 6 | **Extraction.Llm** | 10 | 522 | ✅ | ✅ Abstractions + Core | ❌ | ✅ | ✅ | ⚠️ | ✅ | **7/10** |
| 7 | **Extraction.AzureLanguage** | 12 | 509 | ✅ | ✅ Abstractions only | ❌ | ✅ | ⚠️ | ⚠️ | ✅ | **6/10** |
| 8 | **Enrichment** | 13 | 772 | ✅ | ✅ Abstractions only | ✅ | ✅ | ✅ | ✅ | ✅ | **9/10** |
| 9 | **Observability** | 8 | 427 | ✅ | ✅ Abstractions + Core | ✅ | ✅ | ✅ | ✅ | ✅ | **9/10** |
| 10 | **McpServer** | 22 | 1,302 | ✅ | ✅ Abstractions only | ✅ | ✅ | ✅ | ✅ | ✅ | **9/10** |

**Legend:** ✅ Good | ⚠️ Minor concern | ❌ Needs attention

### Key Findings by Category

| Finding | Category | Severity | Projects Affected |
|---------|----------|----------|-------------------|
| Embedding generation scattered across 5+ call sites | DRY | 🔴 High | Core (4 services) |
| Two `IMemoryExtractionPipeline` implementations with no selection logic | KISS | 🟡 Medium | Core |
| `MemoryExtractionPipeline` at 393 LOC does extraction + validation + resolution + persistence | SRP | 🟡 Medium | Core |
| Extraction.Llm and Extraction.AzureLanguage are ~95% structurally identical | DRY | 🔴 High | Extraction.* |
| `IEntityRepository` has 13 methods (others have 3-6) | ISP | 🟡 Medium | Abstractions |
| Cypher queries inline in C# strings across 10 repositories | Maintainability | 🟡 Medium | Neo4j |
| Hardcoded LLM system prompts require recompilation to change | KISS | 🟢 Low | Extraction.Llm |
| `AzureLanguageRelationshipExtractor` re-calls entity recognition (API waste) | Performance | 🟡 Medium | Extraction.AzureLanguage |
| Confidence thresholds hardcoded in multiple places (0.5, 0.8, 0.85, 0.95) | DRY | 🟡 Medium | Core |
| Zero TODO/FIXME/HACK comments across all 265 source files | Quality | ✅ Positive | All |
| Zero circular dependencies | Architecture | ✅ Positive | All |
| Zero boundary violations | Architecture | ✅ Positive | All |

---

## 3. Cross-Reference Map

### Actual Project References (verified from .csproj files)

```
                              ┌─ AgentFramework ─────── MAF 1.1.0
                              │    → Abstractions
                              │    → Core
                              │
                              ├─ GraphRagAdapter ────── neo4j-maf-provider
                              │    → Abstractions
                              │
                              ├─ McpServer ──────────── MCP 1.2.0
FOUNDATION                    │    → Abstractions
                              │
Abstractions ← Core ← Neo4j  ├─ Extraction.Llm ────── M.E.AI 10.4.1
 (0 deps)      (FuzzySharp)  │    → Abstractions
               (M.E.*)       │    → Core
                              │
                              ├─ Extraction.Azure ───── Azure.AI.TextAnalytics
                              │    → Abstractions
                              │
                              ├─ Enrichment ─────────── M.E.Http, M.E.Caching
                              │    → Abstractions
                              │
                              └─ Observability ──────── OpenTelemetry.Api
                                   → Abstractions
                                   → Core
```

### Cross-Reference Matrix (Source → Source only)

| Project | Abstractions | Core | Neo4j | Others |
|---------|:---:|:---:|:---:|:---:|
| **Abstractions** | — | ❌ | ❌ | ❌ |
| **Core** | ✅ | — | ❌ | ❌ |
| **Neo4j** | ✅ | ✅ | — | ❌ |
| **AgentFramework** | ✅ | ✅ | ❌ | ❌ |
| **GraphRagAdapter** | ✅ | ❌ | ❌ | ❌ |
| **McpServer** | ✅ | ❌ | ❌ | ❌ |
| **Extraction.Llm** | ✅ | ✅ | ❌ | ❌ |
| **Extraction.Azure** | ✅ | ❌ | ❌ | ❌ |
| **Enrichment** | ✅ | ❌ | ❌ | ❌ |
| **Observability** | ✅ | ✅ | ❌ | ❌ |

**Verdict:** ✅ Zero inappropriate cross-references. Every dependency arrow is justified and minimal. The asymmetry between Extraction.Llm (needs Core) and Extraction.Azure (doesn't) is intentional — Llm uses Core's pipeline infrastructure.

---

## 4. Improvement Suggestions

### S1: Consolidate Embedding Generation into a Dedicated Service

| Attribute | Value |
|-----------|-------|
| **Category** | DRY |
| **Current State** | Embedding generation via `IEmbeddingProvider.GenerateEmbeddingAsync()` is called from 5+ locations: `ShortTermMemoryService` (2×), `LongTermMemoryService` (3×), `MemoryExtractionPipeline` (3×), `MemoryContextAssembler` (1×), `MemoryService.GenerateEmbeddingsBatchAsync()`. Each call site has its own try-catch, null-check, and text-construction logic. |
| **Proposed Improvement** | Extract an `IEmbeddingOrchestrator` service with methods like `EmbedEntityAsync(Entity)`, `EmbedFactAsync(Fact)`, `EmbedMessageAsync(Message)` that centralize text composition (what text to embed for each type), null-safety, and error handling. All services delegate to this single orchestrator. |
| **Impact Score** | 8/10 — Eliminates most DRY violations, simplifies unit testing, makes embedding strategy changes single-point |
| **Effort Score** | 3/10 — Extract-method refactor, no API changes needed |
| **Priority** | **High** (Impact/Effort = 2.7) |
| **Risk** | Low. Internal refactor only. No public API changes. |

---

### S2: Merge Extraction.Llm and Extraction.AzureLanguage into Unified Extraction Package

| Attribute | Value |
|-----------|-------|
| **Category** | DRY / Architecture |
| **Current State** | Two separate packages (522 + 509 LOC) with ~95% identical structure: both implement the same 4 extractor interfaces (`IEntityExtractor`, `IFactExtractor`, `IRelationshipExtractor`, `IPreferenceExtractor`), both have identical error-handling patterns (try-catch → log → return empty), both have their own options classes and DI registrations. They differ only in the underlying extraction engine (IChatClient vs. TextAnalyticsClient). |
| **Proposed Improvement** | Create `Neo4j.AgentMemory.Extraction` with an `IExtractionEngine` strategy interface. Each engine (LLM, AzureLanguage) becomes a strategy. Shared pipeline code (validation, error handling, DI registration) lives once. Optional: separate NuGet packages for engine-specific dependencies via `Neo4j.AgentMemory.Extraction.Llm` and `Neo4j.AgentMemory.Extraction.AzureLanguage` that only contain the strategy implementation + SDK dependency — but share the pipeline package. |
| **Impact Score** | 7/10 — Reduces duplication, enables runtime strategy switching, simplifies new-engine onboarding |
| **Effort Score** | 5/10 — Requires refactoring 2 packages, updating DI, updating tests |
| **Priority** | **High** (Impact/Effort = 1.4) |
| **Risk** | Medium. Breaking change for consumers of the current packages. Mitigated by semantic versioning. |

---

### S3: Split MemoryExtractionPipeline into Extraction + Persistence Stages

| Attribute | Value |
|-----------|-------|
| **Category** | SRP / SOLID |
| **Current State** | `MemoryExtractionPipeline` (393 LOC, 11 dependencies) handles: parallel extraction from 4 extractors, confidence filtering, entity validation, entity resolution with dedup, embedding generation, provenance tracking (EXTRACTED_FROM relationships), and persistence to 4 repositories. It also coexists with `MultiExtractorPipeline` (159 LOC) — a second implementation of the same interface with no clear selection mechanism. |
| **Proposed Improvement** | Split into `ExtractionStage` (extract + validate + resolve) and `PersistenceStage` (embed + persist + provenance). Compose them in a pipeline orchestrator. Clarify the role of `MultiExtractorPipeline` vs `MemoryExtractionPipeline` — either merge them or document when each should be used. |
| **Impact Score** | 6/10 — Improves testability and readability, clarifies pipeline semantics |
| **Effort Score** | 5/10 — Significant refactor of Core pipeline logic |
| **Priority** | **Medium** (Impact/Effort = 1.2) |
| **Risk** | Medium. Pipeline is the heart of extraction — thorough testing required. |

---

### S4: Centralize Cypher Queries into Constants/Builder

| Attribute | Value |
|-----------|-------|
| **Category** | Maintainability / DRY |
| **Current State** | Cypher queries are inline C# strings across 10 repository implementations (some 20+ lines long). No syntax highlighting, no reusability, and difficult to review or audit. The Python reference (`queries.py`) centralizes 60+ Cypher constants. A `Queries/` folder exists in the Neo4j project but is currently empty or underutilized. |
| **Proposed Improvement** | Create a `CypherQueries` static class (or per-domain classes: `EntityQueries`, `MessageQueries`, etc.) with `const string` or `static readonly string` query definitions. Repository implementations reference these constants. Consider `.cypher` embedded resources for complex multi-line queries. |
| **Impact Score** | 5/10 — Improves maintainability, enables query auditing, aligns with Python reference |
| **Effort Score** | 3/10 — Mechanical extraction, no behavior change |
| **Priority** | **Medium** (Impact/Effort = 1.7) |
| **Risk** | Low. Purely structural refactor. |

---

### S5: Parameterize Confidence Thresholds

| Attribute | Value |
|-----------|-------|
| **Category** | DRY / Configuration |
| **Current State** | Confidence thresholds are hardcoded in multiple places: 0.95 (auto-merge), 0.85 (flag for review), 0.5 (minimum extraction confidence), 0.8 (various filtering). Some are configurable via `ExtractionOptions.EntityResolutionOptions`, others are not. This creates a split between configurable and hardcoded values. |
| **Proposed Improvement** | Audit all numeric thresholds in Core and extraction packages. Move all to their respective `Options` classes with documented defaults. Ensure `ExtractionOptions`, `EntityResolutionOptions`, and `LlmExtractionOptions` cover all tunable values. |
| **Impact Score** | 5/10 — Enables fine-tuning without code changes, important for production use |
| **Effort Score** | 2/10 — Search and replace + options property additions |
| **Priority** | **High** (Impact/Effort = 2.5) |
| **Risk** | Low. Additive change. Defaults preserve current behavior. |

---

### S6: Fix AzureLanguageRelationshipExtractor Redundant API Calls

| Attribute | Value |
|-----------|-------|
| **Category** | Performance |
| **Current State** | `AzureLanguageRelationshipExtractor` calls `RecognizeEntitiesAsync()` per message to find entity pairs for co-occurrence relationships. However, the entity extractor (`AzureLanguageEntityExtractor`) already makes this same call. When both run in the extraction pipeline, entity recognition is called twice — doubling Azure API costs. |
| **Proposed Improvement** | Introduce an entity cache or shared extraction context within a single pipeline run. When the entity extractor runs first, its results can be reused by the relationship extractor. Alternatively, pass previously-extracted entities as a parameter. |
| **Impact Score** | 6/10 — Cuts Azure API costs in half, improves latency |
| **Effort Score** | 3/10 — Add shared context parameter or cache |
| **Priority** | **High** (Impact/Effort = 2.0) |
| **Risk** | Low. Behavior unchanged; only eliminates redundant calls. |

---

### S7: Narrow IEntityRepository Interface (Interface Segregation)

| Attribute | Value |
|-----------|-------|
| **Category** | SOLID (ISP) |
| **Current State** | `IEntityRepository` has 13 methods including CRUD, vector search, geospatial search (SearchByLocation, SearchInBoundingBox), embedding backfill (GetPageWithoutEmbedding, UpdateEmbedding), batch operations (UpsertBatch), and provenance (CreateExtractedFromRelationship). Other repositories have 3-6 methods. This inconsistency suggests IEntityRepository is overloaded. |
| **Proposed Improvement** | Consider splitting into `IEntityRepository` (core CRUD + search), `IGeoSpatialEntityRepository` (location-based queries), and `IEntityEmbeddingRepository` (embedding backfill). Consumers only take the interfaces they need. Alternatively, accept the pragmatic tradeoff — 13 methods isn't extreme, and splitting increases interface count. |
| **Impact Score** | 3/10 — Cleaner contracts, but practical benefit is modest |
| **Effort Score** | 4/10 — Interface split + update all consumers |
| **Priority** | **Low** (Impact/Effort = 0.75) |
| **Risk** | Medium. Splitting interfaces is a breaking change. May over-fragment a cohesive domain. |

---

### S8: Resolve Dual Pipeline Ambiguity

| Attribute | Value |
|-----------|-------|
| **Category** | KISS / Architecture |
| **Current State** | Core has two `IMemoryExtractionPipeline` implementations: `MemoryExtractionPipeline` (full pipeline with validation, resolution, persistence) and `MultiExtractorPipeline` (parallel multi-extractor with merge strategies). The DI registration (`ServiceCollectionExtensions`) registers one by default, but there's no documentation or factory explaining when to use which. |
| **Proposed Improvement** | Option A: Merge into one pipeline that supports both single-extractor and multi-extractor modes via configuration. Option B: Rename to clarify roles — `DefaultExtractionPipeline` and `MultiProviderExtractionPipeline` — and document the selection criteria. Option C: Make `MultiExtractorPipeline` a decorator around `MemoryExtractionPipeline`. |
| **Impact Score** | 5/10 — Reduces consumer confusion, simplifies DI |
| **Effort Score** | 3/10 — Rename or merge, update DI registration |
| **Priority** | **Medium** (Impact/Effort = 1.7) |
| **Risk** | Low. Either renaming or merging is low-risk. |

---

### S9: Extract Truncation Strategies from MemoryContextAssembler

| Attribute | Value |
|-----------|-------|
| **Category** | SRP |
| **Current State** | `MemoryContextAssembler` (359 LOC) contains 3 inline truncation strategy implementations (OldestFirst, LowestScoreFirst, Proportional) plus budget calculation logic. These are distinct algorithms embedded in the assembler class. |
| **Proposed Improvement** | Extract an `ITruncationStrategy` interface with implementations for each strategy. Inject via DI based on configuration. This follows the same strategy pattern already used for merge strategies. |
| **Impact Score** | 4/10 — Better testability, easier to add new strategies |
| **Effort Score** | 2/10 — Straightforward extraction |
| **Priority** | **Medium** (Impact/Effort = 2.0) |
| **Risk** | Low. Internal refactor. |

---

### S10: Add Provider Tag to Enrichment Cache Keys

| Attribute | Value |
|-----------|-------|
| **Category** | Correctness |
| **Current State** | Enrichment cache key format is `enrichment:{entityName}:{entityType}`. If a deployment switches from WikimediaEnrichmentService to DiffbotEnrichmentService, cached results from the old provider will be served without invalidation. |
| **Proposed Improvement** | Include provider name in cache key: `enrichment:{provider}:{entityName}:{entityType}`. Each implementation passes its provider identifier. |
| **Impact Score** | 4/10 — Prevents subtle data correctness bugs on provider switch |
| **Effort Score** | 1/10 — One-line change per cache decorator |
| **Priority** | **High** (Impact/Effort = 4.0) |
| **Risk** | None. Cache key format change invalidates old entries (desired behavior). |

---

### S11: Externalize LLM System Prompts

| Attribute | Value |
|-----------|-------|
| **Category** | KISS / DX |
| **Current State** | LLM extraction system prompts (entity extraction, fact extraction, relationship extraction, preference extraction) are hardcoded as multi-line strings in C# source files. Changing prompts requires recompilation and redeployment. |
| **Proposed Improvement** | Move prompts to embedded resources (`.txt` files) or make them configurable via `LlmExtractionOptions`. Provide sensible defaults but allow override. |
| **Impact Score** | 4/10 — Enables prompt tuning without deployment, useful for production |
| **Effort Score** | 2/10 — Extract strings to files/options |
| **Priority** | **Medium** (Impact/Effort = 2.0) |
| **Risk** | Low. Defaults preserve behavior. |

---

### S12: Add Observability to Extraction and Enrichment Services

| Attribute | Value |
|-----------|-------|
| **Category** | Observability / DX |
| **Current State** | Observability decorators wrap only `IMemoryService` (9 trace spans) and `IGraphRagContextSource` (1 trace span). Extraction services (Llm, Azure) and Enrichment services have no tracing or metrics. You can see total extraction duration but not which extractor is slow. |
| **Proposed Improvement** | Add `InstrumentedEntityExtractor`, `InstrumentedEnrichmentService`, etc. decorators to the Observability package. Alternatively, add trace spans directly in extractor implementations (lighter approach). |
| **Impact Score** | 5/10 — Critical for production debugging of extraction latency |
| **Effort Score** | 4/10 — Add 4-6 decorator classes following existing pattern |
| **Priority** | **Medium** (Impact/Effort = 1.25) |
| **Risk** | Low. Follows established decorator pattern. |

---

### S13: Fix Inconsistent Duration Metric in Observability

| Attribute | Value |
|-----------|-------|
| **Category** | Correctness |
| **Current State** | `InstrumentedMemoryService.ExtractFromSessionAsync` is missing the stopwatch/duration metric recording that all other instrumented methods have. Inconsistent telemetry. |
| **Proposed Improvement** | Add `Stopwatch` and `_metrics.ExtractionDuration.Record()` to `ExtractFromSessionAsync`, matching the pattern of `ExtractAndPersistAsync`. |
| **Impact Score** | 3/10 — Fixes telemetry gap |
| **Effort Score** | 1/10 — 5-line fix |
| **Priority** | **High** (Impact/Effort = 3.0) |
| **Risk** | None. |

---

### S14: Consider a Meta-Package for Quick Start

| Attribute | Value |
|-----------|-------|
| **Category** | DX |
| **Current State** | Getting started requires installing 3+ separate NuGet packages (Abstractions + Core + Neo4j + at least one extraction package). This is friction for new users. |
| **Proposed Improvement** | Publish a `Neo4j.AgentMemory` convenience meta-package that references Abstractions + Core + Neo4j + Extraction.Llm. Users install one package. Advanced users cherry-pick individual packages. |
| **Impact Score** | 5/10 — Significantly reduces onboarding friction |
| **Effort Score** | 1/10 — Empty project with PackageReferences |
| **Priority** | **High** (Impact/Effort = 5.0) |
| **Risk** | Low. Meta-package doesn't add code — just declares dependencies. |

---

## 5. Package Consolidation Proposal

### Current State: 10 Packages

| # | Package | LOC | Distinct Audience | Verdict |
|---|---------|-----|-------------------|---------|
| 1 | Abstractions | 3,347 | Library authors, all consumers | ✅ Keep |
| 2 | Core | 3,433 | All consumers | ✅ Keep |
| 3 | Neo4j | 2,918 | All consumers (primary persistence) | ✅ Keep |
| 4 | AgentFramework | 943 | MAF developers | ✅ Keep |
| 5 | GraphRagAdapter | 476 | GraphRAG users | ✅ Keep |
| 6 | Extraction.Llm | 522 | LLM-based extraction users | 🔶 Merge |
| 7 | Extraction.AzureLanguage | 509 | Azure NLP users | 🔶 Merge |
| 8 | Enrichment | 772 | Entity enrichment users | ✅ Keep |
| 9 | Observability | 427 | Production deployments | 🔶 Consider merge |
| 10 | McpServer | 1,302 | MCP tool consumers | ✅ Keep |

### Proposed: 7 Packages (with 2 optional sub-packages)

**Before → After:**

```
BEFORE (10 packages):                    AFTER (7 core + 2 optional):

1. Abstractions              →    1. Abstractions              (unchanged)
2. Core                      →    2. Core                      (unchanged)
3. Neo4j                     →    3. Neo4j                     (unchanged)
4. AgentFramework            →    4. AgentFramework            (unchanged)
5. GraphRagAdapter           →    5. GraphRagAdapter           (unchanged)
6. Extraction.Llm        ─┐
                           ├→    6. Extraction                 (unified pipeline + strategy)
7. Extraction.AzureLanguage┘         ├─ Extraction.Llm         (optional: LLM engine dep only)
                                     └─ Extraction.AzureLanguage (optional: Azure SDK dep only)
8. Enrichment                →    7. Enrichment                (unchanged)
9. Observability          ─┐
                           ├→    (merged into Core as opt-in namespace)
10. McpServer                →    8. McpServer                 (unchanged)
```

### Analysis

| Merge Candidate | Rationale | Decision |
|----------------|-----------|----------|
| **Extraction.Llm + Extraction.AzureLanguage** | 95% structural duplication. Same 4 interfaces. Strategy pattern natural. | **MERGE** into `Extraction` with engine sub-packages |
| **Observability → Core** | Only 427 LOC, 2 public types. Always needed in production. | **KEEP SEPARATE** — Observability is opt-in by design. Adding OpenTelemetry.Api to Core forces the dep on everyone. |
| **Enrichment → Core** | External HTTP dependencies (Nominatim, Wikipedia, Diffbot). | **KEEP SEPARATE** — Core should not have HTTP/caching deps. Enrichment is clearly optional. |
| **McpServer → AgentFramework** | Different protocols (MCP vs MAF). Different audiences. | **KEEP SEPARATE** — Correct as-is. |

### Detailed Extraction Merge Proposal

```
Neo4j.AgentMemory.Extraction (base pipeline package)
├── IExtractionEngine.cs          ← NEW: strategy interface
├── ExtractionEngineBase.cs       ← NEW: shared validation, error handling, mapping
├── CompositeEntityExtractor.cs   ← Delegates to IExtractionEngine
├── CompositeFactExtractor.cs
├── CompositeRelationshipExtractor.cs
├── CompositePreferenceExtractor.cs
├── ExtractionOptions.cs          ← Unified options
└── ServiceCollectionExtensions.cs

Neo4j.AgentMemory.Extraction.Llm (LLM engine - depends on base + M.E.AI)
├── LlmExtractionEngine.cs       ← Implements IExtractionEngine
├── LlmExtractionOptions.cs
├── LlmResponseModels.cs (internal)
└── ServiceCollectionExtensions.cs

Neo4j.AgentMemory.Extraction.AzureLanguage (Azure engine - depends on base + Azure SDK)
├── AzureLanguageExtractionEngine.cs  ← Implements IExtractionEngine
├── AzureLanguageOptions.cs
├── ITextAnalyticsClientWrapper.cs (internal)
└── ServiceCollectionExtensions.cs
```

**Benefits:**
- Shared pipeline code lives once (DRY)
- Engine-specific NuGet dependencies isolated to sub-packages
- Runtime strategy selection: `services.AddExtraction().UseLlmEngine(opts => ...)`
- Adding new engines (Claude-native, Gemini, local models) requires only implementing `IExtractionEngine`

---

## 6. Recommended Priority Order

### Tier 1: Quick Wins (< 1 day each, high impact)

| # | Suggestion | Impact | Effort | Ratio |
|---|-----------|--------|--------|-------|
| S14 | Meta-package for quick start | 5 | 1 | **5.0** |
| S10 | Provider tag in enrichment cache keys | 4 | 1 | **4.0** |
| S13 | Fix missing duration metric in Observability | 3 | 1 | **3.0** |
| S5 | Parameterize confidence thresholds | 5 | 2 | **2.5** |
| S1 | Consolidate embedding generation | 8 | 3 | **2.7** |

### Tier 2: Medium Effort, Strong Value (1-3 days each)

| # | Suggestion | Impact | Effort | Ratio |
|---|-----------|--------|--------|-------|
| S6 | Fix Azure redundant API calls | 6 | 3 | **2.0** |
| S9 | Extract truncation strategies | 4 | 2 | **2.0** |
| S11 | Externalize LLM system prompts | 4 | 2 | **2.0** |
| S4 | Centralize Cypher queries | 5 | 3 | **1.7** |
| S8 | Resolve dual pipeline ambiguity | 5 | 3 | **1.7** |

### Tier 3: Larger Refactors (3-5 days, requires design review)

| # | Suggestion | Impact | Effort | Ratio |
|---|-----------|--------|--------|-------|
| S2 | Merge extraction packages | 7 | 5 | **1.4** |
| S3 | Split extraction pipeline stages | 6 | 5 | **1.2** |
| S12 | Observability for extraction/enrichment | 5 | 4 | **1.25** |

### Not Recommended

| # | Suggestion | Why Not |
|---|-----------|---------|
| S7 | Split IEntityRepository | Impact too low for the breaking change. 13 methods is manageable. Accept the pragmatic tradeoff. |

---

## Appendix A: Build & Test Verification

All assessments are based on verified codebase state:

| Metric | Value |
|--------|-------|
| **Build** | ✅ 0 errors, 8 warnings (xUnit1013 in integration tests) |
| **Unit Tests** | ✅ 1,058 passing |
| **Source Files** | 265 |
| **Total LOC** | ~14,650 |
| **TODO/FIXME/HACK** | 0 |
| **Circular Dependencies** | 0 |
| **Boundary Violations** | 0 |

## Appendix B: What's Working Well (Don't Touch)

1. **Abstractions package** — Exemplary. Zero deps, clean contracts, comprehensive domain model.
2. **Dependency direction** — Strict layering is perfectly enforced.
3. **Adapter pattern** — AgentFramework, GraphRagAdapter, McpServer are all thin, well-isolated adapters.
4. **Enrichment decorator chain** — Elegant use of decorator pattern for caching + rate-limiting.
5. **Entity resolution chain** — Well-designed chain of responsibility (Exact → Fuzzy → Semantic).
6. **Stub implementations** — Enable testing without external services. Good DX.
7. **Options pattern** — Consistent use of strongly-typed configuration throughout.
8. **Sealed classes** — Appropriate use prevents unintended inheritance.

---

*This assessment reflects the codebase as of July 2026 against commit state with 1,058 passing unit tests. Recommendations should be revisited after each major refactor.*
