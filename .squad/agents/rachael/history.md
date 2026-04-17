# Rachael — History

## Project Context
- **Project:** Agent Memory for .NET
- **User:** Jose Luis Latorre Millas
- **Stack:** .NET 9, C#, Microsoft Agent Framework
- **Role focus:** MAF adapter — context provider, message store, tools, lifecycle
- **Reference:** /Neo4j/neo4j-maf-provider/dotnet/ for MAF integration patterns

## Recent Work (Wave 1, 2026-07-18)

**Sprint:** MEAI Migration & Phase Unblocking

1. **IEmbeddingProvider → IEmbeddingGenerator<T> Migration (D-AR2-1)** — Full migration across 30 files (Abstractions, Core, AgentFramework, Stubs, Samples, Tests). 401 insertions, 361 deletions. Breaking change: consumers must update DI registrations. Consumer migration guide provided.
2. **Dependency Update** — Abstractions.csproj now depends on Microsoft.Extensions.AI.Abstractions 10.4.1 (previously zero external deps). Uses batch `GenerateAsync([text])` API.
3. **Ecosystem Analysis** — Confirmed MEAI as foundational; migration unblocks SK integration (effort drops from medium to trivial ~200 LOC). Test suite complete: 1,059 tests green ✅

---

## Learnings

### MAF Post-Run Lifecycle (2026-04-14)

Comprehensive ecosystem integration strategy assessment:

1. **D-RACHAEL-1 (MEAI Ecosystem Validation)** — Confirmed MEAI is already foundational (5 of 10 packages). Quantified split-personality problem: custom IEmbeddingProvider (Abstractions) vs MEAI IEmbeddingGenerator (GraphRagAdapter) forces dual registration on consumers. Migration mechanical but high-impact: 11 Core/AgentFramework files, ~2-3 day sprint. Full support for Deckard's D-AR2-1 as cornerstone decision.

2. **LLM Integration Pattern Inventory** — All 4 extractors + ContextCompressor already use MEAI IChatClient (no changes needed). Only embedding generation is split.

3. **Adapter Strategy Verification** — neo4j-maf-provider is read-only retrieval; AgentMemory is full lifecycle. ProjectReference bridge pattern correct. Potential future: standalone Retrieval package for search-only consumers.

4. **Semantic Kernel Readiness** — SK uses IEmbeddingGenerator<T> exclusively. Post-D-AR2-1, SK integration trivial (~200 LOC). Positions .NET for Q3 market expansion.

5. **Migration Feasibility** — Risk LOW (internal refactor, external migration guide). Implementation timeline: 2-3 days. Unblocks SK integration and reduces consumer confusion.

**Output:** docs/meai-ecosystem-analysis.md (532 lines)

**Strategic insight:** MEAI consolidation is not just architecture cleanup—it's a market positioning move enabling SK integration and reducing ecosystem fragmentation.

## Learnings

### MAF Post-Run Lifecycle (2026-04-14)

- `AIContextProvider.StoreAIContextAsync(InvokedContext)` is the canonical post-run hook in MAF 1.1.0. It receives `RequestMessages` (all inputs), `ResponseMessages` (assistant outputs), and `InvokeException` (null if success).
- `InvokedContext` and `InvokingContext` have the same `.Session.StateBag` / `.Agent.Id` shape for session ID extraction. A shared `ExtractIds(AgentSession?, AIAgent?)` helper avoids duplication.
- Only `ResponseMessages` should be persisted post-run; request messages were either already in memory or injected as context.
- Post-run failures must be swallowed (logged, not re-thrown) — the agent response must not break due to memory failures.
- `AgentFrameworkOptions.AutoExtractOnPersist = true` was already in place; the missing piece was wiring it into `StoreAIContextAsync`.

### MCP Tool Patterns (2026-04-14)

- New tools go in a `[McpServerToolType]` class, registered via `.WithTools<T>()` in `AddAgentMemoryMcpTools()`. The service count comment in the extension method should be kept accurate.
- `memory_record_tool_call` delegates to `IReasoningMemoryService.RecordToolCallAsync(stepId, ...)` — the `stepId` (not `traceId`) is the correct foreign key.
- `memory_export_graph` and `memory_find_duplicates` use raw Cypher via `IGraphQueryService` and must gate behind `McpServerOptions.EnableGraphQuery`.
- `extract_and_persist` builds a transient `Message` record (with `IIdGenerator` + `IClock`) and calls `IMemoryService.ExtractAndPersistAsync` directly — no separate pipeline reference needed since the facade already wraps it.
- `memory_find_duplicates` uses `toLower CONTAINS` + length ratio in Cypher for a practical, embedding-free duplicate heuristic.
- Pre-existing duplicate method errors in `Neo4j.AgentMemory.Neo4j` are a known issue unrelated to MAF/MCP work.

### Azure Language Preference Extraction (G4)

- To add sentiment analysis to the `ITextAnalyticsClientWrapper`, extend the interface with `AnalyzeSentimentAsync` and add `AzureSentimentResult` to `AzureModels.cs`; the real wrapper delegates to `TextAnalyticsClient.AnalyzeSentimentAsync` mapping `TextSentiment` enum to lowercase string.
- `AzureLanguagePreferenceExtractor` pairs sentiment analysis with key phrase extraction per message: if `PositiveScore >= threshold` → "like" preferences; if `NegativeScore >= threshold` → "dislike" preferences; neutral/mixed below threshold → returns empty.
- `PreferenceSentimentThreshold` (default 0.7) lives in `AzureLanguageOptions`; this makes the sensitivity configurable per deployment without code changes.
- The `ExtractedPreference.Category` field is used as the preference type ("like"/"dislike"), and `PreferenceText` is the human-readable statement ("likes C#").
- 12 new xUnit tests cover: positive/negative/neutral sentiment, confidence mapping, empty input, Azure service errors, threshold customisation, and multiple-preference extraction from rich text.
- Two pre-existing test failures in `Neo4jEntityRepositoryExtensionsTests` are unrelated to Azure extraction and were present before this work.

### MEAI Migration Execution (2025-07-18)

- Executed D-AR2-1 Option A: replaced `IEmbeddingProvider` with MEAI's `IEmbeddingGenerator<string, Embedding<float>>` across all packages.
- **30 files changed** (12 production, 11 test, 3 samples, 2 stubs deleted/created, 1 interface deleted, 1 csproj updated).
- `GenerateEmbeddingAsync` extension method is NOT available in `Microsoft.Extensions.AI.Abstractions` v10.4.1 — must use the batch `GenerateAsync([text])` API and index into `result[0].Vector.ToArray()`.
- `EmbeddingGeneratorMetadata` constructor in v10.4.1 does NOT have a `dimensions` named parameter — only `providerName` and optional `providerUri`/`defaultModelId`.
- For NSubstitute mocking of `IEmbeddingGenerator<string, Embedding<float>>`, mock the `GenerateAsync` method (the interface method) — extension methods like `GenerateEmbeddingAsync` cannot be intercepted by NSubstitute.
- Created `MockFactory.EmbeddingResult()` helpers to convert `float[]` to `Task<GeneratedEmbeddings<Embedding<float>>>` — reduces test boilerplate significantly.
- The Blended sample previously registered both `IEmbeddingProvider` AND `IEmbeddingGenerator` — now a single `IEmbeddingGenerator<string, Embedding<float>>` registration serves both Core and GraphRagAdapter.
- All 1059 unit tests pass after migration.

- `Microsoft.Extensions.AI.Abstractions` (10.4.1) is already referenced in 5 of our 10 packages: Core, AgentFramework, GraphRagAdapter, Extraction.Llm — we are deeply invested in MEAI.
- The codebase has a "split personality" problem: our custom `IEmbeddingProvider` in Abstractions vs MEAI's `IEmbeddingGenerator<string, Embedding<float>>` in GraphRagAdapter. Consumers must register both for blended scenarios.
- LLM extraction (all 4 extractors) and `ContextCompressor` already use MEAI's `IChatClient` — no change needed there.
- `neo4j-maf-provider` is read-only retrieval (vector/fulltext/hybrid). Our package is a strict superset — full memory lifecycle. The GraphRagAdapter wraps their retrievers via project reference.
- `IEmbeddingProvider` is used in 11 Core/AgentFramework files; migration to `IEmbeddingGenerator` is mechanical but touches many files.
- Adding `Microsoft.Extensions.AI.Abstractions` to Abstractions.csproj adds ~100KB, zero new transitive deps (DI.Abstractions and System.Text.Json already in scope).
- Full analysis written to `docs/meai-ecosystem-analysis.md`.

