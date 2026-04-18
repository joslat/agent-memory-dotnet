# Skill: MAF Adapter Audit Checklist

**Skill type:** Code Review / Architecture Audit  
**Author:** Rachael (MAF Integration Engineer)  
**Last updated:** April 2026

---

## Purpose

Reusable checklist for auditing any MAF (Microsoft Agent Framework 1.1.0) adapter package. Use this when reviewing a new MAF integration or auditing an existing one for compliance with the migration guide.

---

## Critical Checks (must pass)

### 1. Tool Registration — `AIFunction` not custom types
- ✅ Tools must be `AIFunction` instances created via `AIFunctionFactory.Create`
- ✅ Tool methods must use `[Description]` attributes on parameters
- ✅ Factory must return `IReadOnlyList<AIFunction>` (or `IEnumerable<AITool>`)
- ❌ Custom tool wrapper types (`MemoryTool`, `AgentTool`, etc.) are NOT compatible with MAF

### 2. Conversation Storage — `ChatHistoryProvider` not a plain class
- ✅ Neo4j-backed conversation storage must subclass `ChatHistoryProvider`
- ✅ Must override `ProvideChatHistoryAsync(InvokingContext)` and `StoreChatHistoryAsync(InvokedContext)`
- ✅ Use `ProviderSessionState<T>` for any per-session state within the provider
- ❌ A plain service class with `GetMessages`/`AddMessage` is NOT pluggable into `ChatClientAgentOptions.ChatHistoryProvider`

### 3. Context Provider pattern
- ✅ Memory injection must subclass `AIContextProvider` (not plain middleware)
- ✅ Override `ProvideAIContextAsync` (pre-run) and `StoreAIContextAsync` (post-run)
- ✅ Must call `base(null, null)` or `base(null, null, null)` depending on package version
- ✅ Override `StateKey` property for pipeline identity
- ✅ Do NOT store session-specific data in instance fields — use `ProviderSessionState<T>`
- ✅ Post-run failures must be swallowed (logged, never rethrown)

### 4. Session ID extraction
- ✅ Extract from `context.Session?.StateBag` first
- ✅ Fall back to `agent?.Id` (not a random GUID)
- ✅ conversationId fallback: use sessionId, not a new GUID (new GUID breaks cross-turn correlation)
- ✅ State bag key names should be short identifiers like `"session_id"`, NOT HTTP header names like `"X-Session-Id"`

### 5. Package reference
- ✅ Use `Microsoft.Agents.AI Version="1.1.0"` (core package)
- ⚠️ `Microsoft.Agents.AI.Abstractions` may be a minimal-dependency alternative — verify it contains ALL types you use

---

## Important Checks (should pass)

### 6. Null guards on constructors
- All DI-injected constructor parameters should have `?? throw new ArgumentNullException(nameof(param))`

### 7. ConfigureAwait(false) on all awaits
- All `await` calls in library code should use `.ConfigureAwait(false)`

### 8. DI registrations
- `AIContextProvider` implementations: `Scoped` is safe only if agents are not Singleton
- `ChatHistoryProvider` implementations: `Scoped` 
- `MemoryToolFactory` / equivalent: `Transient` (creates tool instances per call)

### 9. Samples must show real `AIAgent`
- At least one sample must demonstrate: `chatClient.AsAIAgent(new ChatClientAgentOptions { AIContextProviders = [provider] })`
- Samples must use `await agent.CreateSessionAsync()` and `await agent.RunAsync(..., session)` for multi-turn
- Samples must NOT rely only on simulated turns (`List<ChatMessage>`)

### 10. Documentation currency
- README key DI patterns must match current code (not pre-migration APIs)
- If `IEmbeddingProvider` appears in README: it's stale, should be `IEmbeddingGenerator<string, Embedding<float>>`

---

## Common Pitfalls Found in This Codebase

| Pitfall | Location | Fix |
|---------|----------|-----|
| Custom tool type incompatible with MAF | `MemoryToolFactory` | Add `CreateAIFunctions()` returning `IReadOnlyList<AIFunction>` |
| Plain class instead of `ChatHistoryProvider` | `Neo4jChatMessageStore` | Create `Neo4jChatHistoryProvider : ChatHistoryProvider` |
| `conversationId` fallback generates new GUID | `Neo4jMemoryContextProvider.ExtractIds` | Use `sessionId` as fallback |
| Options properties named "Header" for StateBag keys | `AgentFrameworkOptions` | Rename to "Key" suffix |
| Duplicate stub class shadowing Core.Stubs import | `BlendedAgent/Program.cs` | Remove local duplicate |
| Stale `IEmbeddingProvider` in README | Both sample READMEs | Replace with `IEmbeddingGenerator<string, Embedding<float>>` |
| Missing `ConfigureAwait(false)` | `AgentTraceRecorder` | Add to all awaits |
| Missing null guards | `AgentTraceRecorder`, `MemoryToolFactory` | Add `?? throw ArgumentNullException` |
