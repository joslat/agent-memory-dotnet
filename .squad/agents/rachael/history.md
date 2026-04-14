# Rachael — History

## Project Context
- **Project:** Agent Memory for .NET
- **User:** Jose Luis Latorre Millas
- **Stack:** .NET 9, C#, Microsoft Agent Framework
- **Role focus:** MAF adapter — context provider, message store, tools, lifecycle
- **Reference:** /Neo4j/neo4j-maf-provider/dotnet/ for MAF integration patterns

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

