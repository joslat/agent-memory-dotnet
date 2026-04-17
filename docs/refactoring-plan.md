# Refactoring Plan — Agent Memory for .NET

**Author:** Deckard (Lead / Solution Architect)  
**Requested by:** Jose Luis Latorre Millas  
**Date:** April 2026  
**Scope:** All 7 code quality findings from architecture-review-assessment.md + additional improvements  
**Related:** `docs/architecture-review-assessment.md` §7, `docs/improvement-suggestions.md` §4

---

## Executive Summary

This plan addresses **7 concrete code quality findings** identified in the architecture review, organized into 3 implementation waves by severity. Each finding includes specific file:line references, proposed solution, implementation steps, and risk assessment.

**Estimated total effort:** 10–15 developer-days  
**Test baseline:** 1,058 passing unit tests (must remain green after each wave)

---

## Findings Overview

| # | Finding | Category | Severity | Wave |
|---|---------|----------|----------|------|
| 1 | Embedding generation scattered across 5+ call sites | DRY | 🔴 High | 1 |
| 2 | Extraction.Llm and Extraction.AzureLanguage ~95% structurally identical | DRY | 🔴 High | 1 |
| 3 | MemoryExtractionPipeline (393 LOC) does extraction + validation + resolution + persistence | SRP | 🟡 Medium | 2 |
| 4 | Dual pipeline ambiguity (MemoryExtractionPipeline vs MultiExtractorPipeline) | KISS | 🟡 Medium | 2 |
| 5 | Cypher queries inline in C# strings across 10 repositories | Maintainability | 🟡 Medium | 3 |
| 6 | Confidence thresholds hardcoded (0.5, 0.8, 0.85, 0.95) | DRY | 🟡 Medium | 2 |
| 7 | AzureLanguageRelationshipExtractor re-calls entity recognition (API waste) | Performance | 🟡 Medium | 2 |

---

## Wave 1: 🔴 High Severity (Embedding + Extraction Unification)

### Finding 1: Embedding Generation Scattered Across 5+ Call Sites

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

## Wave 2: 🟡 Medium Severity (Pipeline, Thresholds, API Waste)

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

## Wave 3: Lower Priority (Cypher Centralization + Extras)

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

---

*This plan reflects the codebase as of April 2026 with 9 packages and 1,058 passing unit tests. Each finding references verified file:line locations from the actual source code.*
