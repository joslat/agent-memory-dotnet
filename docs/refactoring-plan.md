# Refactoring Plan — Agent Memory for .NET

**Author:** Deckard (Lead / Solution Architect)  
**Requested by:** Jose Luis Latorre Millas  
**Date:** April 2026  
**Scope:** 7 code quality findings from architecture-review-assessment.md + 11 functional parity gaps from cypher-analysis.md + additional improvements  
**Related:** `docs/architecture-review-assessment.md` §7, `docs/cypher-analysis.md` §3, `docs/improvement-suggestions.md` §4

---

## Status: ✅ All 4 Waves Complete

| Wave | Focus | Status | Tests |
|------|-------|--------|-------|
| **Wave 1** | IEmbeddingOrchestrator + ExtractorBase<T> (DRY) | ✅ Complete | 1,059 |
| **Wave 2** | Pipeline SRP split + Thresholds + Azure API cache | ✅ Complete | 1,066 |
| **Wave 3** | Cypher Query Centralization (207+ → per-domain) | ✅ Complete | 1,066 |
| **Wave 4** | 11 Functional Parity Gaps (G1-G11: 82.1% → 98.5%) | ✅ Complete | 1,124 |

**Final Result:** 1,211 unit tests passing, **98.5% parity with Python agent-memory**, zero circular dependencies, zero boundary violations.

---

## Executive Summary

This plan addressed **7 concrete code quality findings** + **11 functional parity gaps** (from `cypher-analysis.md`) identified in the architecture review and query analysis, organized into **4 implementation waves** by severity and dependency. All waves are now complete.

**Actual effort:** ~10 developer-days  
**Test baseline:** Started at 1,058 passing unit tests; now at 1,211

---

## Findings Overview — All Addressed

| # | Finding | Category | Severity | Wave | Status |
|---|---------|----------|----------|------|--------|
| 1 | Embedding generation scattered across 5+ call sites | DRY | 🔴 High | 1 | ✅ Implemented: `IEmbeddingOrchestrator` |
| 2 | Extraction.Llm and Extraction.AzureLanguage ~95% identical | DRY | 🔴 High | 1 | ✅ Implemented: `ExtractorBase<T>` |
| 3 | MemoryExtractionPipeline (393 LOC) does too much | SRP | 🟡 Medium | 2 | ✅ Split: ExtractionStage + PersistenceStage |
| 4 | Dual pipeline ambiguity | KISS | 🟡 Medium | 2 | ✅ Merged into unified pipeline |
| 5 | Cypher queries inline across 10 repositories | Maintainability | 🟡 Medium | 3 | ✅ Centralized: Queries/ per-domain classes |
| 6 | Confidence thresholds hardcoded | DRY | 🟡 Medium | 2 | ✅ Parameterized via ConfidenceOptions |
| 7 | AzureLanguageRelationshipExtractor API waste | Performance | 🟡 Medium | 2 | ✅ Fixed: shared ExtractionContext |
| 8 | 11 functional parity gaps (G1-G11) | Parity | 🟡 Medium | 4 | ✅ All 11 implemented; parity 98.5% |

---

## Deferred Items (from "Additional Improvements")

The following items were listed in the implementation schedule (A1-A5) but were **not implemented** during Waves 1-4. They remain valid improvements:

| Item | Description | Status |
|------|-------------|--------|
| A1 | Single NuGet package | 📅 Decided but not published |
| A2 | Provider tag in enrichment cache keys | 📅 Not started |
| A3 | Fix missing duration metric in Observability | 📅 Not started |
| A4 | Externalize LLM system prompts | ⚠️ Deferred — low urgency |
| A5 | Semantic Kernel adapter | 📅 Strategic — requires separate design |

---

## What's Next

Prioritized by impact/effort ratio. See `docs/architecture-review-assessment.md` §10-11 for full details.

| # | Item | Impact | Effort | Rationale |
|---|------|--------|--------|-----------|
| 1 | **Single NuGet package** | Very High | Trivial | Unblocks all external consumption. No code changes — packaging only. |
| 2 | **Provider tag in enrichment cache keys** | Medium | Trivial | Correctness bug fix. One-line change per cache decorator. |
| 3 | **Fix missing duration metric** | Low | Trivial | 5-line fix in `InstrumentedMemoryService.ExtractFromSessionAsync`. |
| 4 | **Semantic Kernel adapter** | Very High | Medium | Opens solution to largest .NET AI audience (>10K stars). ~500 LOC. |
| 5 | **Fix AgentFramework embedding leaks** | Medium | Low | 2 call sites bypass `IEmbeddingOrchestrator` in `MemoryToolFactory` and `Neo4jMemoryContextProvider`. |
| 6 | **Configuration validation tests** | Low | Low | Verify options defaults/constraints. |
| 7 | **Externalize LLM system prompts** | Medium | Low | Prompt tuning without redeployment. |
| 8 | **Observability for extraction/enrichment** | Medium | Medium | Production debugging of extraction latency. |
| 9 | **Temporal memory retrieval** | Medium | High | `RecallAsOfAsync` for point-in-time memory snapshots. |
| 10 | **Memory decay/forgetting** | Medium | High | Prevents infinite memory growth. Requires design review. |

---

## Completed Work (Archive)

> The following sections contain the detailed implementation plans for Waves 1-4.
> All items below were successfully implemented. Preserved for reference.

## Wave 1: 🔴 High Severity (Embedding + Extraction Unification) — ✅ COMPLETE

**Problem:**

`IEmbeddingGenerator<string, Embedding<float>>.GenerateAsync()` is called from **12+ locations** across 5 services in Core, each with its own text composition, error handling, and null-checking logic:

| File | Line(s) | What It Embeds |
|------|---------|----------------|
| `Core/Services/ShortTermMemoryService.cs` | 73, 92 | `message.Content` (2 call sites) |
| `Core/Services/LongTermMemoryService.cs` | 51, 83, 115 | Entity name, preference text, fact SPO text (3 call sites) |
| `Core/Services/MemoryService.cs` | 199, 219, 238 | Entity name, fact SPO, preference text (3 call sites) |
| `Core/Services/MemoryExtractionPipeline.cs` | 140, 186, 247 | Entity name, fact SPO, preference text (3 call sites) |
| `Core/Services/MemoryContextAssembler.cs` | 57 | Query text (1 call site) |
| `Core/Resolution/CompositeEntityResolver.cs` | 100 | Combined entity text (1 call site) |
| `Core/Resolution/SemanticMatchEntityMatcher.cs` | 32 | Candidate entity name (1 call site) |

**Issues:**
- Text composition logic for each domain type is duplicated (e.g., fact text = `$"{subject} {predicate} {object}"` appears in multiple places)
- No centralized error handling for embedding failures
- No centralized caching or batching opportunity
- Changing embedding strategy (e.g., what text to embed for an entity) requires editing 5+ files

**Solution: Create `IEmbeddingOrchestrator` Service**

```csharp
// New file: src/Neo4j.AgentMemory.Core/Services/IEmbeddingOrchestrator.cs
public interface IEmbeddingOrchestrator
{
    Task<float[]> EmbedEntityAsync(string entityName, CancellationToken ct = default);
    Task<float[]> EmbedFactAsync(string subject, string predicate, string obj, CancellationToken ct = default);
    Task<float[]> EmbedPreferenceAsync(string preferenceText, CancellationToken ct = default);
    Task<float[]> EmbedMessageAsync(string content, CancellationToken ct = default);
    Task<float[]> EmbedQueryAsync(string query, CancellationToken ct = default);
    Task<float[]> EmbedTextAsync(string text, CancellationToken ct = default);
}
```

**Implementation Steps:**

1. Define `IEmbeddingOrchestrator` interface in `Abstractions/Services/` (or `Core/Services/` if it needs `IEmbeddingGenerator`)
2. Create `EmbeddingOrchestrator : IEmbeddingOrchestrator` in `Core/Services/`
   - Centralizes text composition: `EmbedFactAsync` builds `"{subject} {predicate} {object}"`
   - Centralizes null-safety and error handling
   - Delegates to `IEmbeddingGenerator<string, Embedding<float>>.GenerateAsync()`
3. Register in DI (`AddAgentMemoryCore()`)
4. Replace all 12+ direct `GenerateAsync()` calls with orchestrator methods
5. Update unit tests — most just need to mock `IEmbeddingOrchestrator` instead of `IEmbeddingGenerator`

**Files to Create:**
- `src/Neo4j.AgentMemory.Abstractions/Services/IEmbeddingOrchestrator.cs` (interface)
- `src/Neo4j.AgentMemory.Core/Services/EmbeddingOrchestrator.cs` (implementation)

**Files to Modify:**
- `src/Neo4j.AgentMemory.Core/Services/ShortTermMemoryService.cs` (lines 73, 92)
- `src/Neo4j.AgentMemory.Core/Services/LongTermMemoryService.cs` (lines 51, 83, 115)
- `src/Neo4j.AgentMemory.Core/Services/MemoryService.cs` (lines 199, 219, 238)
- `src/Neo4j.AgentMemory.Core/Services/MemoryExtractionPipeline.cs` (lines 140, 186, 247)
- `src/Neo4j.AgentMemory.Core/Services/MemoryContextAssembler.cs` (line 57)
- `src/Neo4j.AgentMemory.Core/Resolution/CompositeEntityResolver.cs` (line 100)
- `src/Neo4j.AgentMemory.Core/Resolution/SemanticMatchEntityMatcher.cs` (line 32)
- `src/Neo4j.AgentMemory.Core/ServiceCollectionExtensions.cs` (DI registration)

**Test Impact:**
- ~50+ tests that mock `IEmbeddingGenerator` may need updating to also/instead mock `IEmbeddingOrchestrator`
- New tests: `EmbeddingOrchestratorTests` (text composition, error handling, null safety)

**Risk:** Low. Internal refactor only. No public API changes. All existing embedding behavior preserved through delegation.

---

### Finding 2: Extraction.Llm and Extraction.AzureLanguage ~95% Structurally Identical

**Problem:**

Two separate packages (522 + 509 LOC) implement the exact same 4 extractor interfaces with near-identical structure:

| LLM File | Azure File | Shared Pattern |
|----------|------------|----------------|
| `LlmEntityExtractor.cs` (121 LOC) | `AzureLanguageEntityExtractor.cs` (87 LOC) | Implements `IEntityExtractor`, `ExtractAsync()`, empty-check, try/catch→log→return-empty, domain mapping |
| `LlmFactExtractor.cs` (108 LOC) | `AzureLanguageFactExtractor.cs` (99 LOC) | Implements `IFactExtractor`, same pattern |
| `LlmPreferenceExtractor.cs` (106 LOC) | `AzureLanguagePreferenceExtractor.cs` (106 LOC) | Implements `IPreferenceExtractor`, same pattern |
| `LlmRelationshipExtractor.cs` (108 LOC) | `AzureLanguageRelationshipExtractor.cs` (79 LOC) | Implements `IRelationshipExtractor`, same pattern |
| `ServiceCollectionExtensions.cs` (31 LOC) | `ServiceCollectionExtensions.cs` (56 LOC) | Registers 4 extractors + options |
| `LlmExtractionOptions.cs` | `AzureLanguageOptions.cs` | Options class for configuration |
| `Internal/LlmResponseModels.cs` | `Internal/AzureModels.cs, ITextAnalyticsClientWrapper.cs, TextAnalyticsClientWrapper.cs` | Backend-specific DTOs |

**What's identical (~95%):**
- Error handling pattern (try/catch → log warning → return `Array.Empty<T>()`)
- Empty message guard (`if (messages.Count == 0) return Array.Empty<T>()`)
- Interface implementation pattern (4 extractors implementing 4 interfaces)
- DI registration pattern (4 `TryAddScoped` / `AddScoped` calls)
- All 4 LLM extractors share identical `BuildChatOptions()` and `BuildConversationText()` private methods

**What differs (~5%):**
- Extraction engine: `IChatClient` (LLM) vs `ITextAnalyticsClientWrapper` (Azure)
- Text-to-domain mapping: JSON deserialization (LLM) vs API-specific response mapping (Azure)
- LLM prompts (hardcoded const strings per extractor)

**Solution: Unified Extraction with Strategy Pattern**

Keep the two NuGet packages for dependency isolation, but extract shared pipeline code:

```
Extraction.Llm (keeps IChatClient + M.E.AI dependency)
├── LlmEntityExtractor, LlmFactExtractor, etc. — simplified to just the LLM-specific logic
├── Shared error handling via base class or composition
└── ServiceCollectionExtensions

Extraction.AzureLanguage (keeps Azure.AI.TextAnalytics dependency)
├── AzureLanguageEntityExtractor, etc. — simplified
└── ServiceCollectionExtensions
```

The shared patterns (empty-check, error handling, logging) can be extracted into a base class in Core or Abstractions:

```csharp
// New in Abstractions or Core:
public abstract class ExtractorBase<T> : IExtractor<T>
{
    protected abstract Task<IReadOnlyList<T>> ExtractCoreAsync(
        IReadOnlyList<Message> messages, CancellationToken ct);

    public async Task<IReadOnlyList<T>> ExtractAsync(
        IReadOnlyList<Message> messages, CancellationToken ct = default)
    {
        if (messages.Count == 0) return Array.Empty<T>();
        try { return await ExtractCoreAsync(messages, ct); }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "{Type} extraction failed; returning empty list.", typeof(T).Name);
            return Array.Empty<T>();
        }
    }
}
```

**Implementation Steps:**

1. Create `ExtractorBase<T>` in `Core/Extraction/` with shared empty-check + error handling
2. Extract `BuildConversationText()` helper to a shared utility in Core (used by all 4 LLM extractors)
3. Have each LLM extractor extend `ExtractorBase<T>` and override `ExtractCoreAsync()`
4. Have each Azure extractor extend `ExtractorBase<T>` and override `ExtractCoreAsync()`
5. Remove duplicate code from all 8 extractor implementations
6. Update DI registrations (no API change)
7. Update tests

**Files to Create:**
- `src/Neo4j.AgentMemory.Core/Extraction/ExtractorBase.cs`
- `src/Neo4j.AgentMemory.Core/Extraction/ConversationTextBuilder.cs` (shared helper)

**Files to Modify:**
- All 4 files in `src/Neo4j.AgentMemory.Extraction.Llm/` (simplify to use base class)
- All 4 files in `src/Neo4j.AgentMemory.Extraction.AzureLanguage/` (simplify to use base class)

**Test Impact:**
- Existing extractor tests should pass without modification (behavior unchanged)
- New tests: `ExtractorBaseTests` for shared error handling

**Risk:** Medium. Touching 8+ extractor files. Mitigated by no behavior change — just structural.

---

## Wave 2: 🟡 Medium Severity (Pipeline, Thresholds, API Waste) — ✅ COMPLETE (1,066 tests)

### Finding 3: MemoryExtractionPipeline SRP Violation (393 LOC)

**Problem:**

`src/Neo4j.AgentMemory.Core/Services/MemoryExtractionPipeline.cs` (393 lines, 14 constructor dependencies) handles **four distinct responsibilities:**

1. **Extraction orchestration** (lines 66–113): Run 4 extractors in parallel with fault tolerance
2. **Validation & filtering** (lines 117–131, 174–181, 237–243, 292–298): Confidence filtering + entity validation
3. **Entity resolution** (lines 133–169): Resolve extracted entities, deduplicate, build resolvedEntityMap
4. **Persistence + provenance** (lines 144, 148–161, 186–230, 247–286, 316–343): Embed, upsert to repos, create EXTRACTED_FROM relationships

The 14 constructor parameters are:
- 4 extractors (`IEntityExtractor`, `IFactExtractor`, `IPreferenceExtractor`, `IRelationshipExtractor`)
- 1 resolver (`IEntityResolver`)
- 1 embedding generator (`IEmbeddingGenerator`)
- 4 repositories (`IEntityRepository`, `IFactRepository`, `IPreferenceRepository`, `IRelationshipRepository`)
- 1 options (`IOptions<ExtractionOptions>`)
- 1 clock (`IClock`)
- 1 ID generator (`IIdGenerator`)
- 1 logger (`ILogger`)

**Solution: Split into ExtractionStage + PersistenceStage**

```
ExtractionStage (extract + validate + resolve → ExtractionResult)
    ├── Runs extractors in parallel
    ├── Confidence filtering
    ├── Entity validation
    └── Entity resolution

PersistenceStage (ExtractionResult → persisted graph)
    ├── Embedding generation (via IEmbeddingOrchestrator from Finding 1)
    ├── Repository upserts
    └── EXTRACTED_FROM provenance relationships

ExtractionPipelineOrchestrator (composes both stages)
    └── ExtractAndPersistAsync → ExtractionStage → PersistenceStage
```

**Implementation Steps:**

1. Create `IExtractionStage` and `ExtractionStage` (extraction + validation + resolution)
2. Create `IPersistenceStage` and `PersistenceStage` (embed + persist + provenance)
3. Refactor `MemoryExtractionPipeline` to compose the two stages
4. Each stage has ~5-7 deps instead of 14
5. Update DI registrations

**Files to Create:**
- `src/Neo4j.AgentMemory.Core/Extraction/ExtractionStage.cs`
- `src/Neo4j.AgentMemory.Core/Extraction/PersistenceStage.cs`

**Files to Modify:**
- `src/Neo4j.AgentMemory.Core/Services/MemoryExtractionPipeline.cs` (refactor to compose stages)
- `src/Neo4j.AgentMemory.Core/ServiceCollectionExtensions.cs` (register new stages)

**Test Impact:**
- Existing pipeline tests may need adjustment for new composition
- New: `ExtractionStageTests`, `PersistenceStageTests` (more focused unit tests)

**Risk:** Medium. Pipeline is heart of extraction. Requires thorough test coverage before and after.

---

### Finding 4: Dual Pipeline Ambiguity

**Problem:**

Two classes implement `IMemoryExtractionPipeline` with overlapping but different responsibilities:

| Aspect | `MemoryExtractionPipeline` (393 LOC) | `MultiExtractorPipeline` (159 LOC) |
|--------|--------------------------------------|--------------------------------------|
| Location | `Core/Services/MemoryExtractionPipeline.cs` | `Core/Services/MultiExtractorPipeline.cs` |
| Extractors | Takes **single** `IEntityExtractor`, etc. | Takes `IEnumerable<IEntityExtractor>`, etc. |
| Validation | ✅ Confidence filtering + EntityValidator | ❌ None |
| Resolution | ✅ Entity resolution via `IEntityResolver` | ❌ None |
| Persistence | ✅ Embeds + persists + creates provenance | ❌ None — returns raw extraction results only |
| Merge | N/A (single extractor) | ✅ Merge strategies (Union, HighestConfidence, Consensus) |
| What it returns | Fully persisted result with metadata | Raw merged extraction results (unpersisted) |

**The confusion:** Both implement `IMemoryExtractionPipeline`, but `MultiExtractorPipeline` is **extraction-only** (no validation, no persistence) while `MemoryExtractionPipeline` is a **full pipeline**. A consumer registering `MultiExtractorPipeline` gets no persistence. There's no documentation explaining when to use which.

**Solution: Merge into one pipeline with configurable multi-extractor support**

After Finding 3's stage split, the solution becomes natural:
- `ExtractionStage` supports both single and multiple extractors (with merge strategies)
- `PersistenceStage` handles embed + persist (always needed)
- `MemoryExtractionPipeline` composes them
- `MultiExtractorPipeline` is deleted or becomes a thin wrapper

**Implementation Steps:**

1. After Finding 3 is complete, modify `ExtractionStage` to accept `IEnumerable<IEntityExtractor>` etc.
2. Add merge strategy support from `MultiExtractorPipeline` into `ExtractionStage`
3. If only one extractor registered, skip merge (current `MultiExtractorPipeline` already does this)
4. Delete `MultiExtractorPipeline.cs`
5. Update DI: single `IMemoryExtractionPipeline` registration
6. Document the unified pipeline in code comments and architecture.md

**Files to Delete:**
- `src/Neo4j.AgentMemory.Core/Services/MultiExtractorPipeline.cs`

**Files to Modify:**
- `src/Neo4j.AgentMemory.Core/Extraction/ExtractionStage.cs` (add multi-extractor support)
- `src/Neo4j.AgentMemory.Core/ServiceCollectionExtensions.cs`

**Test Impact:**
- `MultiExtractorPipelineTests` → migrate to `ExtractionStageTests`
- Merge strategy tests remain valid

**Risk:** Low-Medium. `MultiExtractorPipeline` is relatively simple. Main risk is ensuring merge strategy tests still pass.

---

### Finding 5 (Moved to Wave 3): Inline Cypher Queries

See Wave 3 below.

---

### Finding 6: Confidence Thresholds Hardcoded

**Problem:**

Numeric confidence thresholds are scattered across the codebase, some configurable via Options and some hardcoded:

**Already configurable (in `ExtractionOptions`):**
| Threshold | Value | File:Line |
|-----------|-------|-----------|
| `MinConfidenceThreshold` | 0.5 | `Abstractions/Options/ExtractionOptions.cs:15` |
| `AutoMergeThreshold` | 0.95 | `Abstractions/Options/ExtractionOptions.cs:19` |
| `SameAsThreshold` | 0.85 | `Abstractions/Options/ExtractionOptions.cs:21` |
| `FuzzyMatchThreshold` | 0.85 | `Abstractions/Options/ExtractionOptions.cs:36` |
| `SemanticMatchThreshold` | 0.8 | `Abstractions/Options/ExtractionOptions.cs:38` |
| `MinConfidenceThreshold` (LTM) | 0.5 | `Abstractions/Options/LongTermMemoryOptions.cs:18` |

**Hardcoded (need parameterization):**
| Threshold | Value | File:Line | Context |
|-----------|-------|-----------|---------|
| 0.95 | `StrongPatternConfidence` | `Core/Extraction/PatternBasedPreferenceDetector.cs:15` | Named const, but not configurable |
| 0.85 | `RegexMatchConfidence` | `Core/Extraction/PatternBasedPreferenceDetector.cs:16` | Named const, but not configurable |
| 0.7 | key phrase fact confidence | `Extraction.AzureLanguage/AzureLanguageFactExtractor.cs:78` | Inline magic number |
| 0.8 | linked entity fact confidence | `Extraction.AzureLanguage/AzureLanguageFactExtractor.cs:95` | Inline magic number |
| 0.85 | LLM response model default | `Extraction.Llm/Internal/LlmResponseModels.cs:53` | Default for preference confidence |
| 0.8 | LLM response model default | `Extraction.Llm/Internal/LlmResponseModels.cs:71` | Default for relationship confidence |
| 0.8 | MCP similarity threshold | `McpServer/Tools/AdvancedMemoryTools.cs:102` | Parameter default |

**Solution:**

1. Move `PatternBasedPreferenceDetector` thresholds to `ExtractionOptions`
2. Move Azure hardcoded confidence values to `AzureLanguageOptions`
3. LLM response model defaults are acceptable (they're JSON deserialization fallbacks)
4. MCP threshold is a tool parameter default — acceptable

**Implementation Steps:**

1. Add `StrongPatternConfidence` and `RegexMatchConfidence` properties to `ExtractionOptions`
2. Add `KeyPhraseFactConfidence` and `LinkedEntityFactConfidence` to `AzureLanguageOptions`
3. Replace magic numbers with options values in affected files
4. Add tests verifying defaults match current values

**Files to Modify:**
- `src/Neo4j.AgentMemory.Abstractions/Options/ExtractionOptions.cs` (add 2 properties)
- `src/Neo4j.AgentMemory.Extraction.AzureLanguage/AzureLanguageOptions.cs` (add 2 properties)
- `src/Neo4j.AgentMemory.Core/Extraction/PatternBasedPreferenceDetector.cs` (use options)
- `src/Neo4j.AgentMemory.Extraction.AzureLanguage/AzureLanguageFactExtractor.cs` (use options)

**Test Impact:** Minimal. Default values preserve current behavior.

**Risk:** Very low. Additive change with backward-compatible defaults.

---

### Finding 7: AzureLanguageRelationshipExtractor Re-Calls Entity Recognition

**Problem:**

`src/Neo4j.AgentMemory.Extraction.AzureLanguage/AzureLanguageRelationshipExtractor.cs` line 46:

```csharp
var entities = await _client.RecognizeEntitiesAsync(
    message.Content, _options.DefaultLanguage, cancellationToken);
```

This calls `RecognizeEntitiesAsync()` for every message to find entity pairs for co-occurrence relationships. However, `AzureLanguageEntityExtractor.cs` (lines 47-49) makes the **exact same API call** on the same messages. When both extractors run in the same pipeline, entity recognition is called **twice per message** — doubling Azure API costs and latency.

**Solution: Shared Extraction Context**

Introduce an `ExtractionContext` that caches results within a single pipeline run:

```csharp
public class ExtractionContext
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<AzureRecognizedEntity>> _entityCache = new();

    public async Task<IReadOnlyList<AzureRecognizedEntity>> GetOrRecognizeEntitiesAsync(
        string content, string language, ITextAnalyticsClientWrapper client, CancellationToken ct)
    {
        if (_entityCache.TryGetValue(content, out var cached)) return cached;
        var result = await client.RecognizeEntitiesAsync(content, language, ct);
        var list = result.ToList();
        _entityCache.TryAdd(content, list);
        return list;
    }
}
```

**Implementation Steps:**

1. Create `ExtractionContext` in `Extraction.AzureLanguage/Internal/`
2. Register as scoped in DI (one per pipeline run)
3. Inject into both `AzureLanguageEntityExtractor` and `AzureLanguageRelationshipExtractor`
4. Both use `context.GetOrRecognizeEntitiesAsync()` instead of direct API calls
5. Entity extractor runs first (or in parallel — cache handles it), relationship extractor reuses

**Files to Create:**
- `src/Neo4j.AgentMemory.Extraction.AzureLanguage/Internal/ExtractionContext.cs`

**Files to Modify:**
- `src/Neo4j.AgentMemory.Extraction.AzureLanguage/AzureLanguageEntityExtractor.cs` (inject + use context)
- `src/Neo4j.AgentMemory.Extraction.AzureLanguage/AzureLanguageRelationshipExtractor.cs` (inject + use context)
- `src/Neo4j.AgentMemory.Extraction.AzureLanguage/ServiceCollectionExtensions.cs` (register context)

**Test Impact:** Minimal. New test: `ExtractionContextTests` verifying caching behavior.

**Risk:** Low. Only adds caching; doesn't change extraction logic.

---

## Wave 3: Lower Priority (Cypher Centralization + Extras) — ✅ COMPLETE (1,066 tests)

### Finding 5: Cypher Queries Inline Across 10 Repositories

**Problem:**

All Cypher queries are embedded as inline C# strings in repository methods. The `Neo4j` package has **207+ Cypher statements** across 15 files:

| File | MATCH/MERGE/CREATE/CALL Count |
|------|------------------------------|
| `Neo4jEntityRepository.cs` | 54 |
| `Neo4jFactRepository.cs` | 27 |
| `Neo4jMessageRepository.cs` | 24 |
| `Neo4jPreferenceRepository.cs` | 19 |
| `Neo4jReasoningTraceRepository.cs` | 12 |
| `Neo4jToolCallRepository.cs` | 12 |
| `Neo4jExtractorRepository.cs` | 10 |
| `Neo4jRelationshipRepository.cs` | 9 |
| `Neo4jConversationRepository.cs` | 6 |
| `Neo4jReasoningStepRepository.cs` | 6 |
| `SchemaBootstrapper.cs` | 34 |
| `MigrationRunner.cs` | 3 |
| Other (retrievers, services) | 5 |

Issues:
- No syntax highlighting for Cypher in C# strings
- Difficult to audit all queries in one place
- No query reuse (some patterns repeat)
- Python reference centralizes all 60+ queries in `queries.py`

#### Alternatives Analysis

Six approaches were evaluated for centralizing Cypher queries. The analysis considers compile-time safety, IDE support, Python parity, maintainability, and .NET idiom fit.

| Approach | Compile-Time Safety | IDE Support | Python Parity | Dynamic Queries | Verdict |
|----------|:---:|:---:|:---:|:---:|---------|
| **(a) Static C# classes with const strings** | ✅ | ✅ F12, IntelliSense | ✅ Direct equivalent | ✅ via methods | **✅ Recommended** |
| **(b) `.cypher` files (embedded resources)** | ❌ Runtime load | ⚠️ Separate files | ❌ No equivalent | ❌ | ❌ Over-engineered |
| **(c) JSON/YAML storage** | ❌ Runtime load | ❌ No Cypher support | ❌ | ❌ Escaping nightmare | ❌ Worst option |
| **(d) Fluent query builder / DSL** | ✅ | ⚠️ Custom API | ❌ | ✅ | ❌ Wrapping a DSL in a DSL |
| **(e) Single CypherQueries.cs file** | ✅ | ✅ | ✅ 1:1 with queries.py | ✅ | ⚠️ Viable but unwieldy at 207+ |
| **(f) Neo4j OGM / query DSL library** | ⚠️ | ⚠️ | ❌ | ✅ | ❌ No mature .NET OGM exists |

**Why NOT JSON/YAML:** Multi-line Cypher in JSON requires escaping every newline and quotation mark. Parameter names are disconnected from call sites. No IDE navigation from query usage to definition. No compile-time checking. Hot-reloading queries against a database is a security anti-pattern. Zero benefits over const strings.

**Why NOT .cypher files:** Embedded resources add runtime loading complexity, lose compile-time const verification, break F12 navigation, and create a disconnect between query definition and parameter contracts. The IDE Cypher highlighting benefit is marginal — developers spend more time on the C# call site than the query file.

**Why NOT a fluent builder:** Cypher IS the DSL. Wrapping it in a C# fluent API creates a maintenance burden (the builder must track Cypher grammar evolution), reduces readability for anyone who knows Cypher, and makes debugging harder (you can't copy-paste from the builder output to Neo4j Browser).

**Why per-domain classes, not a single file:** Python's `queries.py` (1,248 lines, ~95 constants) works because Python modules don't have the same navigability pressure as C# classes. In .NET, a 207+-constant class is unwieldy. Per-domain classes (EntityQueries, FactQueries, etc.) give:
- Repository ↔ Queries 1:1 mapping (easy to find)
- Each file stays under ~50 constants (manageable)
- Domain teams can own their query files independently
- Matches the repository structure already in place

**Python reference approach:** `queries.py` uses module-level string constants with multi-line triple-quoted strings, organized by 11 semantic domain comments. `query_builder.py` handles dynamic label generation with validation. Our per-domain static classes ARE the .NET-idiomatic translation of this pattern — the split is structural, not conceptual.

#### Cypher Validation Strategy

**Can we validate Cypher syntax at build time?** No. There is no .NET Cypher parser or linter. The Neo4j driver has no offline validation API.

**Can we validate without executing?** Yes — `EXPLAIN` returns the query plan without executing the query. This is the standard Neo4j approach.

**Recommended: Integration-time EXPLAIN validation.** Create a `CypherQueryValidator` that runs `EXPLAIN` on all registered query constants during integration tests or application startup (opt-in). This catches syntax errors, missing indexes, and typos in property names before any data flows:

```csharp
// Integration test or startup validation
public class CypherQueryValidator
{
    public async Task ValidateAllAsync(IAsyncSession session)
    {
        var allQueries = CypherQueryRegistry.GetAll(); // reflection or explicit registration
        foreach (var (name, cypher) in allQueries)
        {
            await session.RunAsync($"EXPLAIN {cypher}"); // throws on syntax error
        }
    }
}
```

This is strictly additive — opt-in, no production overhead, runs in Testcontainers integration tests.

#### Query Classification

Our 207+ queries break down by operation type and domain:

| Domain | MERGE | MATCH | CREATE | CALL | DELETE | Other |
|--------|:---:|:---:|:---:|:---:|:---:|:---:|
| Entity | 8 | 20 | 4 | 6 | 4 | 12 |
| Fact | 3 | 10 | 2 | 4 | 2 | 6 |
| Message | 3 | 8 | 3 | 4 | 2 | 4 |
| Preference | 3 | 6 | 2 | 4 | 2 | 2 |
| Schema | — | — | 34 | — | — | — |
| Other | 5 | 15 | 3 | 5 | 4 | 7 |

~88% are static `const string` (extractable as-is). ~7% use string interpolation for conditional clauses (e.g., optional session filters). ~5% use ternary selection between two complete queries. The dynamic queries become static helper methods returning strings, placed in the same domain query class.

**Solution: Per-Domain Static Query Classes (Confirmed)**

The original plan is correct. Enhancements added based on analysis:

```
src/Neo4j.AgentMemory.Neo4j/Queries/
├── MetadataFilterBuilder.cs       (ALREADY EXISTS — shared WHERE clause builder)
├── EntityQueries.cs               (~54 constants + 2 methods for dynamic queries)
├── FactQueries.cs                 (~27 constants)
├── MessageQueries.cs              (~24 constants + 1 method for dynamic filter)
├── PreferenceQueries.cs           (~19 constants)
├── RelationshipQueries.cs         (~9 constants)
├── ConversationQueries.cs         (~6 constants)
├── ReasoningQueries.cs            (~18 constants — traces + steps combined)
├── ToolCallQueries.cs             (~12 constants)
├── ExtractorQueries.cs            (~10 constants)
├── SchemaQueries.cs               (~34 constants — constraints, indexes, DDL)
├── SharedFragments.cs             (reusable Cypher fragments: vector search CALL, datetime patterns)
└── CypherQueryRegistry.cs         (collects all queries for EXPLAIN validation)
```

**Naming convention** (matches Python `queries.py` style):
```csharp
public static class EntityQueries
{
    public const string Upsert = @"MERGE (e:Entity {id: $id}) ...";
    public const string GetById = "MATCH (e:Entity {id: $id}) RETURN e";
    public const string SearchByVector = @"CALL db.index.vector.queryNodes(...) ...";
    public const string GetByNameWithAliases = "MATCH (e:Entity) WHERE e.name = $name OR $name IN e.aliases RETURN e";

    // Dynamic queries that need conditional logic
    public static string GetByName(bool includeAliases) => includeAliases
        ? GetByNameWithAliases
        : "MATCH (e:Entity {name: $name}) RETURN e";
}
```

**Implementation Steps:**

1. Create per-domain `*Queries.cs` files in `Queries/` directory
2. Extract all `const string cypher` into named constants following `DOMAIN_OPERATION` naming
3. Extract dynamic queries (`var cypher = ...`) into static methods with the same conditional logic
4. Create `SharedFragments.cs` for reusable patterns (vector search CALL, EXPLAIN prefix)
5. Create `CypherQueryRegistry.cs` that collects all query constants via reflection
6. Replace all inline strings in repositories with constant/method references
7. Add integration test: `CypherQueryValidationTests` that runs `EXPLAIN` on all registered queries
8. Verify all 1,058+ existing tests pass (behavior unchanged)

**Files to Create:** 12 query files (10 domain + SharedFragments + CypherQueryRegistry)

**Files to Modify:** All 15 Neo4j repository/infrastructure files

**Test Impact:** Zero behavior change to existing tests. New: `CypherQueryValidationTests` (integration test running EXPLAIN on all queries).

**Risk:** Low. Mechanical extraction. The EXPLAIN validation is additive and opt-in. No behavior change to any existing query.

---

## Wave 4: Functional Parity Gaps (After Wave 3 — New Queries Into Centralized Classes) — ✅ COMPLETE (1,124 tests)

**Functional Parity Result:** 82.1% → **98.5%** (all 11 gaps G1-G11 implemented)

> **Prerequisite:** Wave 3 (Finding 5 — Cypher Centralization) complete.  
> New queries integrated directly into the per-domain query classes (`MessageQueries`, `EntityQueries`, `ExtractorQueries`, `ConversationQueries`).

### Finding 8: 11 Functional Parity Gaps — Missing Queries vs Python

**Source:** `docs/cypher-analysis.md` §3 — Genuine Gaps  
**Parity impact:** All 11 gaps now implemented: functional parity raised from 82.1% → **98.5%** (remaining delta = decided omissions only)

These Python `agent-memory` queries with no .NET equivalent were NOT decided omissions. Grouped by domain and ordered by priority.

#### Group A: Message Lifecycle (🟡 Medium Priority) — ✅ IMPLEMENTED (G1-G3)

Three missing operations that complete the message CRUD surface:

| Gap | Python Query | What It Does | Interface Change |
|-----|-------------|--------------|------------------|
| G1 | `DELETE_MESSAGE` (#14) | Delete a message + cascade-delete MENTIONS relationships | Add `DeleteAsync(messageId, cascade: true)` to `IMessageRepository` |
| G2 | `DELETE_MESSAGE_NO_CASCADE` (#15) | Delete a message node only (preserve relationships) | Reuse same method with `cascade: false` |
| G3 | `LIST_SESSIONS` (#16) | List all sessions with conversation count, message count, and preview text | Add `ListSessionsAsync(limit?)` to `IConversationRepository` |

**Implementation — G1 + G2 (Message Delete):**

```csharp
// IMessageRepository addition:
Task<bool> DeleteAsync(string messageId, bool cascade = true, CancellationToken ct = default);
```

Neo4j implementation:
```csharp
// Cascade: DETACH DELETE (removes MENTIONS, HAS_MESSAGE, NEXT_MESSAGE, FIRST_MESSAGE)
const string CascadeDelete = @"
    MATCH (m:Message {id: $id})
    OPTIONAL MATCH (m)-[r]-()
    DELETE r, m
    RETURN COUNT(m) > 0 AS deleted";

// No cascade: DELETE node only (orphans relationships — caller manages)
const string SimpleDelete = @"
    MATCH (m:Message {id: $id})
    DELETE m
    RETURN COUNT(m) > 0 AS deleted";
```

**Files to modify:**
- `src/Neo4j.AgentMemory.Abstractions/Repositories/IMessageRepository.cs` (add `DeleteAsync`)
- `src/Neo4j.AgentMemory.Neo4j/Repositories/Neo4jMessageRepository.cs` (implement)
- New test: `tests/Neo4j.AgentMemory.Neo4j.Tests/Repositories/Neo4jMessageRepositoryTests.cs`

**Implementation — G3 (List Sessions):**

```csharp
// IConversationRepository addition:
Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(int limit = 50, CancellationToken ct = default);

// New domain model:
public record SessionSummary(
    string SessionId,
    int ConversationCount,
    int MessageCount,
    string? LastMessagePreview,
    DateTimeOffset? LastActivity);
```

Neo4j Cypher (matches Python's `LIST_SESSIONS`):
```cypher
MATCH (c:Conversation)
WITH c.session_id AS sessionId, collect(c) AS conversations
OPTIONAL MATCH (c2:Conversation {session_id: sessionId})-[:HAS_MESSAGE]->(m:Message)
WITH sessionId, SIZE(conversations) AS convCount, collect(m) AS messages
RETURN sessionId,
       convCount,
       SIZE(messages) AS msgCount,
       messages[-1].content AS lastPreview,
       messages[-1].timestamp AS lastActivity
ORDER BY lastActivity DESC
LIMIT $limit
```

**Files to modify:**
- `src/Neo4j.AgentMemory.Abstractions/Domain/SessionSummary.cs` (create)
- `src/Neo4j.AgentMemory.Abstractions/Repositories/IConversationRepository.cs` (add `ListSessionsAsync`)
- `src/Neo4j.AgentMemory.Neo4j/Repositories/Neo4jConversationRepository.cs` (implement)

**Effort:** Low (2-3 hours for all three)

#### Group B: Provenance Queries (🟡 Medium Priority) — ✅ IMPLEMENTED (G4-G8)

Five queries completing the provenance tracking system. Write-side (EXTRACTED_FROM, EXTRACTED_BY) already existed — these are the read/query side:

| Gap | Python Query | What It Does | Interface Change |
|-----|-------------|--------------|------------------|
| G4 | `GET_ENTITY_PROVENANCE` (#65) | Get sources + extractors for an entity (full provenance chain) | Add `GetProvenanceAsync(entityId)` to `IExtractorRepository` |
| G5 | `GET_ENTITIES_FROM_MESSAGE` (#66) | Get all entities extracted from a specific message | Add `GetEntitiesFromMessageAsync(messageId)` to `IEntityRepository` |
| G6 | `GET_EXTRACTION_STATS` (#68) | Overall extraction statistics (total entities, avg per message) | Add `GetExtractionStatsAsync()` to `IExtractorRepository` |
| G7 | `GET_EXTRACTOR_STATS` (#69) | Per-extractor entity counts and confidence averages | Add `GetExtractorStatsAsync(extractorName)` to `IExtractorRepository` |
| G8 | `DELETE_ENTITY_PROVENANCE` (#71) | Remove all provenance relationships from an entity | Add `DeleteProvenanceAsync(entityId)` to `IExtractorRepository` |

**Implementation — G4 (Entity Provenance):**

```csharp
// New domain model:
public record EntityProvenance(
    string EntityId,
    IReadOnlyList<ProvenanceSource> Sources,
    IReadOnlyList<ProvenanceExtractor> Extractors);

public record ProvenanceSource(string MessageId, double? Confidence, int? StartPos, int? EndPos);
public record ProvenanceExtractor(string ExtractorName, double Confidence, int? ExtractionTimeMs);
```

```cypher
MATCH (e:Entity {id: $entityId})
OPTIONAL MATCH (e)-[ef:EXTRACTED_FROM]->(m:Message)
OPTIONAL MATCH (e)-[eb:EXTRACTED_BY]->(ex:Extractor)
RETURN e, collect(DISTINCT {messageId: m.id, confidence: ef.confidence, startPos: ef.start_position, endPos: ef.end_position}) AS sources,
       collect(DISTINCT {extractorName: ex.name, confidence: eb.confidence, extractionTimeMs: eb.extraction_time_ms}) AS extractors
```

**Implementation — G5 (Entities From Message):**

```cypher
MATCH (m:Message {id: $messageId})<-[:EXTRACTED_FROM]-(e:Entity)
RETURN e ORDER BY e.name
```

**Implementation — G6 + G7 (Stats):**

```csharp
// New domain models:
public record ExtractionStats(int TotalEntities, int TotalMessages, double AvgEntitiesPerMessage);
public record ExtractorStats(string ExtractorName, int EntityCount, double AvgConfidence, int TotalExtractions);
```

```cypher
-- G6: Overall stats
OPTIONAL MATCH (e:Entity)
WITH COUNT(e) AS totalEntities
OPTIONAL MATCH (m:Message)<-[:EXTRACTED_FROM]-(:Entity)
WITH totalEntities, COUNT(DISTINCT m) AS totalMessages
RETURN totalEntities, totalMessages,
       CASE WHEN totalMessages > 0 THEN toFloat(totalEntities) / totalMessages ELSE 0.0 END AS avgPerMessage

-- G7: Per-extractor stats
MATCH (ex:Extractor {name: $extractorName})
OPTIONAL MATCH (ex)<-[eb:EXTRACTED_BY]-(e:Entity)
RETURN ex.name AS name, COUNT(e) AS entityCount, AVG(eb.confidence) AS avgConfidence, COUNT(eb) AS totalExtractions
```

**Implementation — G8 (Delete Provenance):**

```cypher
MATCH (e:Entity {id: $entityId})
OPTIONAL MATCH (e)-[ef:EXTRACTED_FROM]->()
OPTIONAL MATCH (e)-[eb:EXTRACTED_BY]->()
DELETE ef, eb
RETURN COUNT(ef) + COUNT(eb) AS deleted
```

**Files to modify:**
- `src/Neo4j.AgentMemory.Abstractions/Domain/EntityProvenance.cs` (create)
- `src/Neo4j.AgentMemory.Abstractions/Domain/ExtractionStats.cs` (create)
- `src/Neo4j.AgentMemory.Abstractions/Domain/ExtractorStats.cs` (create)
- `src/Neo4j.AgentMemory.Abstractions/Repositories/IExtractorRepository.cs` (add 4 methods)
- `src/Neo4j.AgentMemory.Abstractions/Repositories/IEntityRepository.cs` (add `GetEntitiesFromMessageAsync`)
- `src/Neo4j.AgentMemory.Neo4j/Repositories/Neo4jExtractorRepository.cs` (implement 4 methods)
- `src/Neo4j.AgentMemory.Neo4j/Repositories/Neo4jEntityRepository.cs` (implement 1 method)

**Effort:** Low-Medium (3-4 hours for all five)

#### Group C: Deduplication Monitoring (🟡 Medium Priority) — ✅ IMPLEMENTED (G9-G11)

Three missing queries that complete the entity deduplication workflow. The write-side (SAME_AS, MergeEntities) already exists — these are the discovery/monitoring side:

| Gap | Python Query | What It Does | Interface Change |
|-----|-------------|--------------|------------------|
| G9 | `FIND_SIMILAR_ENTITIES_BY_EMBEDDING` (#72) | Vector search for potential duplicate entities (excluding self) | Add `FindSimilarByEmbeddingAsync(entityId, threshold, limit)` to `IEntityRepository` |
| G10 | `GET_POTENTIAL_DUPLICATES` (#74) | Get pending SAME_AS pairs for manual review | Add `GetPendingDuplicatesAsync(limit)` to `IEntityRepository` |
| G11 | `GET_DEDUPLICATION_STATS` (#79) | Merged/pending/rejected SAME_AS counts | Add `GetDeduplicationStatsAsync()` to `IEntityRepository` |

**Implementation — G9 (Find Similar Entities):**

```csharp
Task<IReadOnlyList<(Entity Entity, double Similarity)>> FindSimilarByEmbeddingAsync(
    string entityId, double minSimilarity = 0.85, int limit = 10, CancellationToken ct = default);
```

```cypher
MATCH (source:Entity {id: $entityId}) WHERE source.embedding IS NOT NULL
CALL db.index.vector.queryNodes('entity_embedding_idx', $limit + 1, source.embedding)
YIELD node, score
WHERE node.id <> $entityId AND score >= $minSimilarity
RETURN node, score
ORDER BY score DESC
LIMIT $limit
```

**Implementation — G10 (Pending Duplicates):**

```csharp
public record DuplicatePair(Entity Source, Entity Target, double Similarity, string Status);

Task<IReadOnlyList<DuplicatePair>> GetPendingDuplicatesAsync(int limit = 50, CancellationToken ct = default);
```

```cypher
MATCH (a:Entity)-[s:SAME_AS {status: 'pending'}]->(b:Entity)
RETURN a, b, s.confidence AS similarity, s.status
ORDER BY s.confidence DESC
LIMIT $limit
```

**Implementation — G11 (Deduplication Stats):**

```csharp
public record DeduplicationStats(int PendingCount, int ConfirmedCount, int RejectedCount, int MergedCount);

Task<DeduplicationStats> GetDeduplicationStatsAsync(CancellationToken ct = default);
```

```cypher
OPTIONAL MATCH ()-[s:SAME_AS]->()
WITH s.status AS status, COUNT(s) AS cnt
RETURN
  SUM(CASE WHEN status = 'pending' THEN cnt ELSE 0 END) AS pending,
  SUM(CASE WHEN status = 'confirmed' THEN cnt ELSE 0 END) AS confirmed,
  SUM(CASE WHEN status = 'rejected' THEN cnt ELSE 0 END) AS rejected,
  SUM(CASE WHEN status = 'merged' THEN cnt ELSE 0 END) AS merged
```

**Files to modify:**
- `src/Neo4j.AgentMemory.Abstractions/Domain/DuplicatePair.cs` (create)
- `src/Neo4j.AgentMemory.Abstractions/Domain/DeduplicationStats.cs` (create)
- `src/Neo4j.AgentMemory.Abstractions/Repositories/IEntityRepository.cs` (add 3 methods)
- `src/Neo4j.AgentMemory.Neo4j/Repositories/Neo4jEntityRepository.cs` (implement 3 methods)

**Effort:** Low (2-3 hours for all three)

#### Finding 8 Summary

| Group | Gaps | Priority | Effort | Parity Impact |
|-------|------|----------|--------|---------------|
| A: Message Lifecycle | G1-G3 | 🟡 Medium | 2-3 hours | +3 queries |
| B: Provenance Queries | G4-G8 | 🟡 Medium | 3-4 hours | +5 queries |
| C: Dedup Monitoring | G9-G11 | 🟡 Medium | 2-3 hours | +3 queries |
| **Total** | **11** | **🟡 Medium** | **~1-1.5 days** | **82.1% → 98.5%** |

**Risk:** Low. All additive — new interface methods, new queries, new tests. Zero changes to existing methods or queries. Queries go directly into the per-domain centralized classes from Wave 3 (Finding 5), avoiding double work.

**Sequencing note:** This entire Finding depends on Wave 3 completion. New Cypher goes into `MessageQueries`, `EntityQueries`, `ExtractorQueries`, `ConversationQueries` — not inline in repository files.

---

## Additional Improvements (from improvement-suggestions.md)

These are not part of the 7 core findings but are recommended based on the architecture review:

### A1: Single NuGet Package (DECIDED)

**Status:** ✅ Decided (April 2026)  
**Action:** Publish `Neo4j.AgentMemory` as single NuGet package bundling all 9 assemblies.  
**Implementation:** Create packaging .csproj that includes all project outputs. See `architecture-review-assessment.md` §3.  
**Effort:** Trivial (1-2 hours)

### A2: Provider Tag in Enrichment Cache Keys (S10)

**Status:** Ready  
**Action:** Include provider name in cache key format: `enrichment:{provider}:{entityName}:{entityType}`  
**File:** `src/Neo4j.AgentMemory.Enrichment/Enrichment/CachingEnrichmentDecorator.cs`  
**Effort:** Trivial (30 min)

### A3: Fix Missing Duration Metric in Observability (S13)

**Status:** Ready  
**Action:** Add `Stopwatch` + `_metrics.ExtractionDuration.Record()` to `InstrumentedMemoryService.ExtractFromSessionAsync`  
**File:** `src/Neo4j.AgentMemory.Observability/InstrumentedMemoryService.cs`  
**Effort:** Trivial (15 min)

### A4: Externalize LLM System Prompts (S11)

**Status:** Ready  
**Action:** Move hardcoded system prompts from LLM extractors to embedded resources or `LlmExtractionOptions`  
**Files:** All 4 `Llm*Extractor.cs` files in `src/Neo4j.AgentMemory.Extraction.Llm/`  
**Effort:** Low (2-3 hours)

### A5: Semantic Kernel Adapter (S15)

**Status:** Strategic — requires separate design  
**Action:** Create `Neo4j.AgentMemory.SemanticKernel` package (~500 LOC)  
**Effort:** Medium (3-5 days)

---

## Implementation Schedule

```
Wave 1 (Days 1-5): 🔴 High Severity
  ├── Finding 1: IEmbeddingOrchestrator          (2 days)
  └── Finding 2: Extraction base class            (2-3 days)

Wave 2 (Days 6-10): 🟡 Medium Severity
  ├── Finding 6: Parameterize thresholds          (0.5 day)
  ├── Finding 7: Azure extraction context         (0.5 day)
  ├── Finding 3: Pipeline stage split             (2 days)
  ├── Finding 4: Merge dual pipelines             (1 day)
  └── Extras A1-A3: Quick wins                    (0.5 day)

Wave 3 (Days 11-15): Lower Priority
  ├── Finding 5: Cypher centralization            (2-3 days)
  ├── Extra A4: Externalize prompts               (0.5 day)
  └── Buffer for integration testing              (1-2 days)

Wave 4 (Days 16-17): Functional Parity Gaps  ← AFTER Cypher centralization
  ├── Finding 8A: Message lifecycle gaps (G1-G3)  (2-3 hours)
  ├── Finding 8B: Provenance queries (G4-G8)      (3-4 hours)
  ├── Finding 8C: Dedup monitoring (G9-G11)       (2-3 hours)
  └── New queries go directly into centralized per-domain classes
```

---

## Verification Criteria

After **each wave**:
1. `dotnet build Neo4j.AgentMemory.slnx` — 0 errors, 0 warnings
2. `dotnet test` — all 1,058+ tests pass
3. No new circular dependencies
4. No boundary violations
5. Git commit with clear description of changes

After **all waves**:
- Architecture review re-assessment (updated scores in improvement-suggestions.md)
- Core package score: 7/10 → target 9/10
- Extraction packages: 6-7/10 → target 8-9/10
- Functional parity: 82.1% → target 98.5% (Wave 4)

---

*This plan reflects the codebase as of April 2026 with 9 packages; started at 1,058 passing unit tests. All 4 waves are now complete with 1,211 tests passing. Each finding references verified file:line locations from the actual source code. Wave 4 queries slot directly into the centralized Cypher classes created in Wave 3. See "Deferred Items" and "What's Next" above for remaining work.*
