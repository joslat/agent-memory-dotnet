# MAF Layer Audit, Review & Improvement Plan

**Author:** Rachael (MAF Integration Engineer)  
**Date:** April 2026  
**Scope:** Complete audit of `Neo4j.AgentMemory.AgentFramework` adapter layer and MAF samples against the MAF 1.1.0 migration guide.

---

## Executive Summary

The MAF adapter layer is structurally sound and compiles successfully. Core logic — context injection (`Neo4jMemoryContextProvider`), message persistence (`Neo4jChatMessageStore`), the `Neo4jMicrosoftMemoryFacade` convenience wrapper, and the `AgentTraceRecorder` — delegate correctly to Core services with appropriate error isolation. The adapter boundary is clean: no MAF types leak into Core, and no Core domain types are exposed through MAF-specific APIs.

However, there are three significant architectural gaps relative to MAF 1.1.0 best practices. First, `MemoryToolFactory` returns a custom `MemoryTool` type that is **not** a MAF-compatible `AIFunction` — agent tool registration with `.AsAIAgent(tools: [...])` or `ChatClientAgentOptions.ChatOptions.Tools` is broken by design. Second, `Neo4jChatMessageStore` is a plain service class rather than a proper `ChatHistoryProvider` subclass, meaning it cannot be registered in the MAF agent pipeline as the guide recommends. Third, neither sample contains an actual MAF `AIAgent` — both use simulated turns — so there is no demonstrated path from DI registration to working agent integration.

Code quality concerns are largely minor (missing null guards, a potential memory leak in `AgentTraceRecorder`, a dead parameter, misleading property names), but the documentation is stale in two places: both sample READMEs reference the pre-MEAI `IEmbeddingProvider` API that was replaced in Wave 1. Test coverage for the AgentFramework layer is good (BuildContextAsync, PerformStoreAsync, all 6 tools, trace recorder, mapper, facade) but there are no tests for `ServiceCollectionExtensions` and no end-to-end test with a real `AIAgent`.

---

## 1. Migration Guide Compliance

### 1.1 MAF 1.1.0 API Usage

| File | Status | Notes |
|------|--------|-------|
| `Neo4jMemoryContextProvider.cs` | ⚠️ Issue | Correct `AIContextProvider` pattern; `base(null, null, null)` call (3 args) may not match guide's 2-arg example — check `AIContextProvider` constructor signature in `Microsoft.Agents.AI.Abstractions 1.1.0` |
| `Neo4jChatMessageStore.cs` | ❌ Wrong | Plain service class, not a `ChatHistoryProvider`. Can't be wired into MAF pipeline slot |
| `Neo4jMicrosoftMemoryFacade.cs` | ✅ Correct | Thin coordinator; doesn't expose MAF types |
| `AgentTraceRecorder.cs` | ✅ Correct | Delegates cleanly to `IReasoningMemoryService` |
| `AgentFrameworkOptions.cs` | ⚠️ Issue | Property names imply HTTP headers; they are StateBag keys |
| `ContextFormatOptions.cs` | ✅ Correct | No MAF-specific issues |
| `ServiceCollectionExtensions.cs` | ⚠️ Issue | Incomplete registration (AgentTraceRecorder, MemoryToolFactory omitted); `TryAddScoped` for providers is risky |
| `Mapping/MafTypeMapper.cs` | ✅ Correct | Clean bidirectional mapping; no leakage |
| `Tools/MemoryTool.cs` | ❌ Wrong | Custom tool type; incompatible with MAF `AIFunction` contract |
| `Tools/MemoryToolFactory.cs` | ❌ Wrong | Returns `IReadOnlyList<MemoryTool>` — not injectable into MAF tool slots |
| `Neo4j.AgentMemory.AgentFramework.csproj` | ⚠️ Issue | References `Microsoft.Agents.AI.Abstractions 1.1.0`; guide specifies `Microsoft.Agents.AI 1.1.0` |

### 1.2 Deprecated API Usage

No RC1 or RC3 deprecated APIs detected. `AIContextProvider`, `InvokingContext`, `InvokedContext`, `AIAgent`, and `AgentSession` are all 1.1.0 types.

### 1.3 Missing MAF 1.1.0 Features

| Feature | Guide Reference | Our Status |
|---------|----------------|------------|
| `ChatHistoryProvider` implementation | Best Practice #4, Section 9 | ❌ Missing — `Neo4jChatMessageStore` is not a provider subclass |
| `AIFunctionFactory.Create` for tools | Section 5 | ❌ Missing — custom `MemoryTool` used instead |
| `ProviderSessionState<T>` for per-session state | Section 8 warning | 🟡 Not needed today (stateless delegation), but pattern not demonstrated |
| `UseOpenTelemetry()` on chat client (MAF-level OTel) | Section 17.8 | ❌ Not shown in any sample (our own OTel wraps Core, not MAF) |
| Both streaming and non-streaming middleware | Best Practice #10 | ❌ Not demonstrated in samples |
| `CompactionProvider` integration | Section 17.5 | ❌ Not addressed (low priority for Neo4j-backed memory) |
| Real `AIAgent` in samples | Section 3, 4 | ❌ Both samples use simulated turns |
| `SerializeSessionAsync` / `DeserializeSessionAsync` | Section 18 | ❌ Not shown in samples |

---

## 2. Code Quality Issues

### 2.1 Per-File Analysis

---

#### `src/Neo4j.AgentMemory.AgentFramework/Neo4j.AgentMemory.AgentFramework.csproj`

**Issue 1** — `Microsoft.Agents.AI.Abstractions` vs `Microsoft.Agents.AI`  
- **Lines:** `<PackageReference Include="Microsoft.Agents.AI.Abstractions" Version="1.1.0" />`  
- **Severity:** 🟡 Medium  
- **Description:** The migration guide specifies `Microsoft.Agents.AI` as the core package. Using `.Abstractions` may be intentional (minimal dependency) but is undocumented and unverified. If `AIContextProvider`, `InvokingContext`, and `InvokedContext` live in the abstractions package, this is fine. If any concrete types are needed at runtime, this will fail with missing type exceptions.  
- **Fix:** Verify the package contains all types used (`AIContextProvider`, `InvokingContext`, `InvokedContext`, `AIContext`, `AgentSession`, `AIAgent`). If any are missing, switch to `Microsoft.Agents.AI`.

---

#### `src/Neo4j.AgentMemory.AgentFramework/Neo4jMemoryContextProvider.cs`

**Issue 2** — `base(null, null, null)` constructor call  
- **Lines:** 28  
- **Severity:** 🟡 Medium  
- **Description:** The migration guide example for `ServiceBackedMemoryProvider` shows `base(null, null)` (2 args). Our call passes 3 nulls. This is consistent only if the `AIContextProvider` in `Microsoft.Agents.AI.Abstractions 1.1.0` has a 3-parameter constructor. Add an inline comment documenting which constructor overload this calls and why nulls are appropriate.  
- **Fix:**
  ```csharp
  // AIContextProvider(IServiceProvider? sp, ILogger? logger, string? stateKey)
  : base(null, null, null)
  ```
  Add this comment to document the intent.

**Issue 3** — Fallback conversation ID generates a new GUID per invocation  
- **Lines:** 213  
- **Severity:** 🟡 Medium  
- **Description:** `conversationId ??= Guid.NewGuid().ToString("N")` generates a fresh random ID when the StateBag doesn't contain the conversation ID key. This means every agent turn that doesn't populate the StateBag gets a unique conversation ID, making cross-turn memory correlation impossible.  
- **Fix:** Derive the fallback conversation ID from the session ID for consistency:
  ```csharp
  conversationId ??= sessionId;
  ```

**Issue 4** — `StateKey` property not overridden  
- **Lines:** (missing override)  
- **Severity:** 🟢 Low  
- **Description:** The migration guide shows that providers should override `StateKey` when managing session state. While our provider doesn't use `ProviderSessionState<T>` today, overriding `StateKey` is a defensive best practice for provider identity in the pipeline.  
- **Fix:** Add:
  ```csharp
  public override string StateKey => nameof(Neo4jMemoryContextProvider);
  ```

**Issue 5** — `ExtractIds` called twice with duplicate null fallback logic  
- **Lines:** 183–216  
- **Severity:** 🟢 Low  
- **Description:** `ExtractSessionIds(InvokingContext)` and `ExtractSessionIds(InvokedContext)` are wrappers over the same `ExtractIds` method. This is clean. No defect, but the fallback of generating a random GUID for conversationId (Issue 3 above) is the real concern.

---

#### `src/Neo4j.AgentMemory.AgentFramework/Neo4jChatMessageStore.cs`

**Issue 6** — Not a `ChatHistoryProvider` subclass  
- **Lines:** 12  
- **Severity:** 🔴 Critical  
- **Description:** MAF 1.1.0 Best Practice #4 and Section 9 define `ChatHistoryProvider` as the correct way to plug custom storage into the agent pipeline. `Neo4jChatMessageStore` is a plain service class. It cannot be used as `ChatHistoryProvider = new Neo4jChatMessageStore(...)` in `ChatClientAgentOptions` because the types are incompatible. The class is only accessible as a raw service via DI.  
- **Fix:** Create a separate `Neo4jChatHistoryProvider : ChatHistoryProvider` class that implements `ProvideChatHistoryAsync` and `StoreChatHistoryAsync`, delegating to `IMemoryService`. Keep `Neo4jChatMessageStore` as a helper, or fold it in. Example:
  ```csharp
  public sealed class Neo4jChatHistoryProvider : ChatHistoryProvider
  {
      private readonly ProviderSessionState<State> _sessionState;
      private readonly IMemoryService _memoryService;

      public Neo4jChatHistoryProvider(IMemoryService memoryService) : base(null, null)
      {
          _memoryService = memoryService;
          _sessionState = new ProviderSessionState<State>(
              stateInitializer: _ => new State(),
              stateKey: nameof(Neo4jChatHistoryProvider));
      }

      public override string StateKey => _sessionState.StateKey;

      protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
          InvokingContext context, CancellationToken ct)
      {
          var sessionId = /* extract session id */;
          var recall = await _memoryService.RecallAsync(
              new RecallRequest { SessionId = sessionId, Query = string.Empty,
                  Options = new RecallOptions { MaxRecentMessages = 50 } }, ct);
          return recall.Context.RecentMessages.Items.Select(MafTypeMapper.ToChatMessage);
      }

      protected override async ValueTask StoreChatHistoryAsync(
          InvokedContext context, CancellationToken ct)
      {
          // persist RequestMessages + ResponseMessages
      }

      public sealed class State { /* ProviderSessionState payload if needed */ }
  }
  ```

---

#### `src/Neo4j.AgentMemory.AgentFramework/Neo4jMicrosoftMemoryFacade.cs`

**Issue 7** — Dead `messages` parameter in `GetContextForRunAsync`  
- **Lines:** 37, 44  
- **Severity:** 🟡 Medium  
- **Description:** `GetContextForRunAsync` accepts `IReadOnlyList<ChatMessage> messages` but completely ignores it, delegating directly to `_messageStore.GetMessagesAsync(sessionId)`. The parameter was presumably intended to allow the method to decide how much history to fetch based on current context, but it's currently dead code. This misleads callers into thinking current messages influence the retrieval.  
- **Fix:** Either remove the parameter (breaking API change, minor) or use it to determine a query for semantic search:
  ```csharp
  // Option A: remove the unused parameter
  public async Task<IReadOnlyList<ChatMessage>> GetContextForRunAsync(
      string sessionId, string conversationId, CancellationToken ct = default)

  // Option B: use messages as a semantic query hint
  var queryText = string.Join(" ", messages.Where(m => m.Role == ChatRole.User).Select(m => m.Text));
  ```

---

#### `src/Neo4j.AgentMemory.AgentFramework/AgentTraceRecorder.cs`

**Issue 8** — Missing null guards on constructor parameters  
- **Lines:** 21–31  
- **Severity:** 🟡 Medium  
- **Description:** All four constructor parameters (`_reasoningService`, `_clock`, `_idGenerator`, `_logger`) are assigned without null checks. A null `_reasoningService` would cause a `NullReferenceException` on first use, far from the construction site.  
- **Fix:**
  ```csharp
  _reasoningService = reasoningService ?? throw new ArgumentNullException(nameof(reasoningService));
  _clock = clock ?? throw new ArgumentNullException(nameof(clock));
  _idGenerator = idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));
  _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  ```

**Issue 9** — `_stepCounts` ConcurrentDictionary unbounded growth  
- **Lines:** 19, 41, 53, 102  
- **Severity:** 🟡 Medium  
- **Description:** `_stepCounts` grows by one entry per `StartTraceAsync` call and shrinks by one per `CompleteTraceAsync`. If an agent run crashes between start and complete, the entry leaks indefinitely. Since `AgentTraceRecorder` is registered as `Scoped`, the lifetime is per-request — so in practice the leak is bounded to the scope. However, if ever registered as `Singleton`, this becomes a memory leak. Add a comment or use `Scoped` registration explicitly.  
- **Fix (documentation approach):**
  ```csharp
  // NOTE: _stepCounts is scoped to this recorder instance. AgentTraceRecorder must
  // be registered as Scoped (per-request) to bound this dictionary's lifetime.
  private readonly ConcurrentDictionary<string, int> _stepCounts = new();
  ```
- **Fix (defensive cleanup approach):** In `StartTraceAsync`, after creating the new trace entry, consider adding an overflow guard:
  ```csharp
  if (_stepCounts.Count > 1000)
      _logger.LogWarning("AgentTraceRecorder has {Count} uncompleted traces.", _stepCounts.Count);
  ```

**Issue 10** — Missing `ConfigureAwait(false)` on all awaits  
- **Lines:** 39, 69, 83, 100  
- **Severity:** 🟢 Low  
- **Description:** `AgentTraceRecorder` methods await `_reasoningService` calls without `.ConfigureAwait(false)`. All other classes in this package use `ConfigureAwait(false)` consistently.  
- **Fix:** Add `.ConfigureAwait(false)` to every `await` in the class:
  ```csharp
  var trace = await _reasoningService.StartTraceAsync(...).ConfigureAwait(false);
  return await _reasoningService.AddStepAsync(...).ConfigureAwait(false);
  return await _reasoningService.RecordToolCallAsync(...).ConfigureAwait(false);
  await _reasoningService.CompleteTraceAsync(...).ConfigureAwait(false);
  ```

---

#### `src/Neo4j.AgentMemory.AgentFramework/AgentFrameworkOptions.cs`

**Issue 11** — Misleading "Header" naming for StateBag keys  
- **Lines:** 11–12  
- **Severity:** 🟡 Medium  
- **Description:** `DefaultSessionIdHeader = "X-Session-Id"` and `DefaultConversationIdHeader = "X-Conversation-Id"` are used as `StateBag` key names (in `Neo4jMemoryContextProvider.ExtractIds`), not as HTTP headers. The property names suggest HTTP layer usage. The default values follow HTTP header naming convention (`X-*`) which will likely never match actual StateBag keys in production (agents typically use shorter keys like `"sessionId"` or `"session_id"`).  
- **Fix:** Rename to `DefaultSessionIdKey` / `DefaultConversationIdKey` with defaults that match idiomatic StateBag usage:
  ```csharp
  public string DefaultSessionIdKey { get; set; } = "session_id";
  public string DefaultConversationIdKey { get; set; } = "conversation_id";
  ```
  Update `Neo4jMemoryContextProvider.ExtractIds` to use the new names.

---

#### `src/Neo4j.AgentMemory.AgentFramework/ServiceCollectionExtensions.cs`

**Issue 12** — `AgentTraceRecorder` and `MemoryToolFactory` not registered  
- **Lines:** 36–41  
- **Severity:** 🟡 Medium  
- **Description:** `AddAgentMemoryFramework` registers `Neo4jMemoryContextProvider`, `Neo4jChatMessageStore`, and `Neo4jMicrosoftMemoryFacade` but not `AgentTraceRecorder` or `MemoryToolFactory`. Callers must remember to add these manually, which is a discoverability problem. The current approach splits "MAF adapter" into two categories without documentation in the extension method.  
- **Fix:** Register both in `AddAgentMemoryFramework`, optionally guarded by a flag:
  ```csharp
  services.TryAddScoped<AgentTraceRecorder>();
  services.TryAddScoped<MemoryToolFactory>();
  ```

**Issue 13** — `TryAddScoped` for `Neo4jMemoryContextProvider` may conflict with singleton agent lifetime  
- **Lines:** 36  
- **Severity:** 🟡 Medium  
- **Description:** If an `AIAgent` is registered as a singleton (common in long-lived services), and it resolves `Neo4jMemoryContextProvider` from DI, it will hold a stale scoped instance after the original scope ends. This is the classic "captive dependency" problem. The migration guide's pattern creates provider instances directly (not via DI scope), which sidesteps this issue.  
- **Fix:** Document this explicitly with a comment, or provide a factory extension that creates the provider instance outside of DI scope:
  ```csharp
  /// <remarks>
  /// <see cref="Neo4jMemoryContextProvider"/> is Scoped. If your agent is a Singleton,
  /// resolve it from a fresh scope or use the factory overload.
  /// </remarks>
  ```

---

#### `src/Neo4j.AgentMemory.AgentFramework/Mapping/MafTypeMapper.cs`

**Issue 14** — Potential duplicate messages in `ToContextMessages`  
- **Lines:** 53–57  
- **Severity:** 🟡 Medium  
- **Description:** Both `context.RecentMessages.Items` and `context.RelevantMessages.Items` are added unconditionally. If the same message appears in both collections (a recent message that also matched semantically), it will be added twice to the output. The `MaxContextMessages` limit will then silently drop other items to compensate.  
- **Fix:** Deduplicate by `MessageId` before rendering:
  ```csharp
  var allMessages = context.RecentMessages.Items
      .Concat(context.RelevantMessages.Items)
      .DistinctBy(m => m.MessageId)
      .ToList();
  foreach (var m in allMessages)
      messages.Add(ToChatMessage(m));
  ```

---

#### `src/Neo4j.AgentMemory.AgentFramework/Tools/MemoryTool.cs` and `MemoryToolFactory.cs`

**Issue 15** — Custom `MemoryTool` type is not a MAF `AIFunction`  
- **Lines:** `MemoryTool.cs:10`, `MemoryToolFactory.cs:37`  
- **Severity:** 🔴 Critical  
- **Description:** MAF 1.1.0 requires tools to be `AIFunction` (or `AITool`) instances, created via `AIFunctionFactory.Create`. The current `MemoryTool` type has a custom `Func<MemoryToolRequest, CancellationToken, Task<MemoryToolResponse>>` signature. It cannot be passed to `ChatClientAgentOptions.ChatOptions.Tools`, `.AsAIAgent(tools: [...])`, or any MAF-compatible tool slot. This makes `MemoryToolFactory` effectively non-functional for MAF integration despite being the primary tool integration point.  
- **Fix:** Replace `MemoryTool` / `MemoryToolFactory` with `AIFunction` creation:
  ```csharp
  public IReadOnlyList<AIFunction> CreateAIFunctions() =>
  [
      AIFunctionFactory.Create(
          ([Description("Semantic search query")] string query, CancellationToken ct) =>
              SearchMemoryAsync(query, ct),
          name: "search_memory",
          description: "Semantic search across all memory layers (entities, facts, preferences)."),
      // ... other tools
  ];
  ```
  Keep the existing `MemoryTool` type and `CreateTools()` method for backward compatibility if needed, but add a `CreateAIFunctions()` method that returns MAF-compatible `AIFunction` instances.

---

#### `src/Neo4j.AgentMemory.AgentFramework/ContextFormatOptions.cs`

**Issue 16** — `MaxContextMessages = 10` includes the prefix message  
- **Lines:** 13  
- **Severity:** 🟢 Low  
- **Description:** The `MaxContextMessages` limit is applied to the full list including the context prefix message (added at line 51 of `MafTypeMapper.cs`). So the effective limit for actual memory items is 9, not 10. This is a minor off-by-one in the semantic intent of the setting.  
- **Fix:** Apply the limit only to memory items, not the prefix:
  ```csharp
  // In MafTypeMapper.ToContextMessages:
  var itemMessages = messages.Skip(string.IsNullOrWhiteSpace(options.ContextPrefix) ? 0 : 1);
  return itemMessages.Take(options.MaxContextMessages)
      .Prepend(messages[0]) // re-add prefix
      .ToList();
  ```
  OR document the intent clearly: "MaxContextMessages includes the prefix system message."

---

### 2.2 Cross-Cutting Issues

**Issue 17** — `AgentTraceRecorder` and `MemoryToolFactory` have no XML doc comments on public methods  
- **Severity:** 🟢 Low  
- **Files:** `AgentTraceRecorder.cs` lines 34, 46, 75, 89; `MemoryToolFactory.cs` line 37  
- **Fix:** Add `/// <summary>` comments matching the existing style in `Neo4jMemoryContextProvider`.

**Issue 18** — `AgentFrameworkOptions` and `ContextFormatOptions` public properties have no XML doc comments  
- **Severity:** 🟢 Low  
- **Files:** `AgentFrameworkOptions.cs`, `ContextFormatOptions.cs`  
- **Fix:** Add `/// <summary>` to each property explaining valid values and effects.

---

## 3. Architecture & Boundary Analysis

### 3.1 Adapter Boundary Integrity

✅ **No MAF types leak into Core.** `InvokingContext`, `InvokedContext`, `AIContext`, `AIContextProvider`, `AgentSession`, and `AIAgent` are used only in the `AgentFramework` package.

✅ **No Core domain types leak through MAF APIs.** `Neo4jMicrosoftMemoryFacade`'s public API uses only `ChatMessage` (MEAI) and primitive types. The `StoreMessageAsync` return type of `Message` (internal domain type) is the one exception — it leaks a domain type into the public surface. This is acceptable if the facade is considered internal-to-adapter, but worth reviewing.

⚠️ **`MafTypeMapper` is `internal static`** — correct. No leakage risk.

### 3.2 Dependency Direction

```
AgentFramework
  → Abstractions (IMemoryService, ILongTermMemoryService, etc.)
  → Core (via ServiceCollectionExtensions calling AddAgentMemoryCore-provided types)
  → Microsoft.Agents.AI.Abstractions (MAF)
  → Microsoft.Extensions.AI.Abstractions (MEAI)
```

Dependency direction is correct. Core has no reference to AgentFramework.

⚠️ **However**: The `ServiceCollectionExtensions` `AddAgentMemoryFramework` method calls:
```csharp
services.Configure<ContextFormatOptions>()
    .Configure<IOptions<AgentFrameworkOptions>>((ctx, af) => { ... })
```
This copies `AgentFrameworkOptions.ContextFormat` into a separate `ContextFormatOptions` registration. This is fine but creates two sources of truth for the same settings. Consider simplifying by injecting `IOptions<AgentFrameworkOptions>` directly into `Neo4jMemoryContextProvider` (already done) and removing the separate `ContextFormatOptions` registration.

### 3.3 DI Registration Quality

| Service | Lifetime | Issue |
|---------|----------|-------|
| `Neo4jMemoryContextProvider` | `Scoped` | ⚠️ Risky if agent is singleton |
| `Neo4jChatMessageStore` | `Scoped` | ✅ Correct |
| `Neo4jMicrosoftMemoryFacade` | `Scoped` | ✅ Correct |
| `AgentTraceRecorder` | `Scoped` (manual) | ✅ Correct |
| `MemoryToolFactory` | `Scoped` (manual) | ⚠️ Should be `Transient` (factory creates new tools each call) |

---

## 4. Sample Quality

### 4.1 MinimalAgent Sample

**Strengths:**
- Clean DI setup pattern
- Graceful fallback when Neo4j is unavailable
- Shows all 5 integration steps clearly

**Issues:**

**Issue 19** — No actual MAF `AIAgent` constructed  
- **Severity:** 🔴 Critical  
- **File:** `samples/Neo4j.AgentMemory.Sample.MinimalAgent/Program.cs`, lines 105–110  
- **Description:** The "agent turn" is a hardcoded `List<ChatMessage>`. The sample doesn't show how to register `Neo4jMemoryContextProvider` with a real agent, or how to pass it to `ChatClientAgentOptions.AIContextProviders`. This defeats the purpose of the MAF sample.
- **Fix:** Add a section showing agent creation:
  ```csharp
  var contextProvider = sp.GetRequiredService<Neo4jMemoryContextProvider>();
  var agent = chatClient.AsAIAgent(new ChatClientAgentOptions
  {
      Name = "MemoryAgent",
      ChatOptions = new() { Instructions = "You are a helpful assistant with memory." },
      AIContextProviders = [contextProvider],
  });
  var response = await agent.RunAsync("My name is Alice and I prefer dark mode.", session);
  ```

**Issue 20** — README.md references stale `IEmbeddingProvider`/`StubEmbeddingProvider`  
- **Severity:** 🟡 Medium  
- **File:** `samples/Neo4j.AgentMemory.Sample.MinimalAgent/README.md`, line 97  
- **Description:** The "Key DI registration pattern" in the README still shows:
  ```csharp
  services.AddSingleton<IEmbeddingProvider, StubEmbeddingProvider>(); // swap for real provider
  ```
  This was replaced in Wave 1 (MEAI migration). The correct registration is:
  ```csharp
  services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>, StubEmbeddingGenerator>();
  ```
- **Fix:** Update README.md line 97 to match the actual `Program.cs` line 53.

**Issue 21** — Sample uses floating `Version="*"` for `Microsoft.Extensions.Hosting`  
- **Severity:** 🟢 Low  
- **File:** `samples/Neo4j.AgentMemory.Sample.MinimalAgent/Neo4j.AgentMemory.Sample.MinimalAgent.csproj`, line 10  
- **Description:** `Version="*"` is unpredictable in reproducible builds. Pin to a specific version.
- **Fix:** `<PackageReference Include="Microsoft.Extensions.Hosting" Version="9.0.0" />`

### 4.2 BlendedAgent Sample

**Strengths:**
- Shows all three retrieval modes (MemoryOnly, GraphRagOnly, Blended)
- OpenTelemetry integration demonstrated
- Clear separation of GraphRAG and memory paths

**Issues:**

**Issue 22** — Duplicate `StubEmbeddingGenerator` class shadows `Core.Stubs` import  
- **Severity:** 🟡 Medium  
- **File:** `samples/Neo4j.AgentMemory.Sample.BlendedAgent/Program.cs`, lines 23, 261–293  
- **Description:** The file both `using Neo4j.AgentMemory.Core.Stubs;` (line 23) and defines a local `internal sealed class StubEmbeddingGenerator` (lines 261–293). The local class shadows the imported namespace's class. The `using` directive is therefore either unused (causing a warning or error with `TreatWarningsAsErrors`) or causes an ambiguity. The `StubEmbeddingGenerator` from `Core.Stubs` should be used instead.
- **Fix:** Remove the local `StubEmbeddingGenerator` class (lines 261–293). The `Core.Stubs` import already provides it.

**Issue 23** — README.md shows both `IEmbeddingProvider` and `IEmbeddingGenerator` as separate registrations  
- **Severity:** 🟡 Medium  
- **File:** `samples/Neo4j.AgentMemory.Sample.BlendedAgent/README.md`, lines 140–141  
- **Description:**
  ```csharp
  services.AddSingleton<IEmbeddingProvider, StubEmbeddingProvider>();         // swap for real provider
  services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>, ...>(); // swap for real generator
  ```
  Post-MEAI migration, `IEmbeddingProvider` no longer exists. Only `IEmbeddingGenerator<string, Embedding<float>>` is needed.
- **Fix:** Remove the `IEmbeddingProvider` line. Update the comment:
  ```csharp
  services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>, StubEmbeddingGenerator>(); // swap for real generator
  ```

**Issue 24** — No actual MAF `AIAgent` constructed (same as MinimalAgent Issue 19)  
- **Severity:** 🔴 Critical  
- **File:** `samples/Neo4j.AgentMemory.Sample.BlendedAgent/Program.cs`

### 4.3 Missing Samples

| Sample | Priority | Description |
|--------|----------|-------------|
| **RealAgent** | 🔴 High | A sample that creates an actual `AIAgent`, registers `Neo4jMemoryContextProvider`, and runs multi-turn conversations with a real or mock LLM |
| **ChatHistoryProvider** | 🟡 Medium | Shows `Neo4jChatHistoryProvider` (once built) wired into `ChatClientAgentOptions.ChatHistoryProvider` |
| **MemoryToolsAgent** | 🟡 Medium | An agent with `MemoryToolFactory.CreateAIFunctions()` tools, demonstrating search_memory and remember_preference in action |
| **WorkflowMemory** | 🟢 Low | Multi-agent workflow where memory context provider persists across workflow steps |

---

## 5. Test Coverage

### 5.1 Existing Tests

| File | Tests | Coverage |
|------|-------|----------|
| `AgentTraceRecorderTests.cs` | 8 | StartTrace, RecordStep, RecordToolCall, CompleteTrace, step numbering, warn on unknown trace |
| `MemoryToolFactoryTests.cs` | ~12 | All 6 tools, empty query validation, error handling |
| `MafTypeMapperTests.cs` | ~10 | Role mapping, message conversion, context assembly |
| `Neo4jMemoryContextProviderTests.cs` | 9 | BuildContextAsync (5 cases), PerformStoreAsync (4 cases) |
| `Neo4jChatMessageStoreTests.cs` | ~8 | AddMessageAsync, GetMessagesAsync, ClearSession, error handling |
| `Neo4jMicrosoftMemoryFacadeTests.cs` | 7 | GetContext, Persist, StoreMessage, ClearSession, error propagation |

Total: ~54 unit tests covering the AgentFramework layer. Good breadth.

**Missing coverage:**
- `ProvideAIContextAsync` and `StoreAIContextAsync` (the protected `override` methods) — only `BuildContextAsync` and `PerformStoreAsync` (internal helpers) are tested
- `ServiceCollectionExtensions` — no tests verify DI registration correctness
- `AgentFrameworkOptions` / `ContextFormatOptions` binding from configuration
- `ToContextMessages` with `MaxContextMessages` limit applied
- `ToContextMessages` with duplicate message deduplication (once fixed)

### 5.2 Missing Tests

| Test | Description | Priority |
|------|-------------|----------|
| `ServiceCollectionExtensionsTests` | Verify all expected services are registered, correct lifetimes | 🟡 Medium |
| `Neo4jMemoryContextProvider_ProtectedMethods` | Test `ProvideAIContextAsync` / `StoreAIContextAsync` via a subclass or reflection | 🟡 Medium |
| `AgentFrameworkOptionsBindingTests` | Verify options bind correctly from IConfiguration | 🟢 Low |
| `MemoryToolFactory_AIFunctionCompatibility` | Once `CreateAIFunctions()` is added, verify returned types are `AIFunction` | 🔴 High |
| `Neo4jChatHistoryProvider` integration test | Once `ChatHistoryProvider` is implemented | 🔴 High |
| `ToContextMessages_MaxLimit_ExcludesDuplicates` | Verify duplicate message handling and limit enforcement | 🟡 Medium |

---

## 6. Improvement Plan

### Priority 1 — Critical Fixes (must fix now)

1. **[P1-1] ✅ Done — Add `CreateAIFunctions()` to `MemoryToolFactory`**  
   Added `CreateAIFunctions()` returning `IReadOnlyList<AIFunction>` using `AIFunctionFactory.Create(delegate, name, description)`. Each tool method has `[Description]` attributes on parameters for schema generation. `CreateTools()` marked `[Obsolete]`. Tests added.

2. **[P1-2] ✅ Done — Create `Neo4jChatHistoryProvider : ChatHistoryProvider`**  
   Created `Neo4jChatHistoryProvider` subclassing MAF 1.1.0 `ChatHistoryProvider`, overriding `ProvideChatHistoryAsync` (`ValueTask<IEnumerable<ChatMessage>>`) and `StoreChatHistoryAsync` (`ValueTask`). Registered in `AddAgentMemoryFramework` via `TryAddScoped`. Tests added (constructor null guards, StateKeys, IsAssignableTo).

3. **[P1-3] ✅ Done — Update MinimalAgent and BlendedAgent samples**  
   Both samples now call `CreateAIFunctions()` instead of `CreateTools()`. BlendedAgent's duplicate local `StubEmbeddingGenerator` class removed. Both READMEs updated to replace `IEmbeddingProvider`/`StubEmbeddingProvider` with `IEmbeddingGenerator<string, Embedding<float>>`/`StubEmbeddingGenerator`.

4. **[P1-4] ✅ Done — Add null guards and ConfigureAwait(false) to `AgentTraceRecorder`**  
   All four constructor parameters now have `?? throw ArgumentNullException`. `ConfigureAwait(false)` added to all awaits. `RecordStepAsync`, `RecordToolCallAsync`, and `CompleteTraceAsync` also add null guards on string parameters.

5. **[P1-5] ✅ Done (merged into P1-3) — Remove duplicate `StubEmbeddingGenerator` from BlendedAgent**

### Priority 2 — Important Improvements

6. **[P2-1] ✅ Done (merged into P1-4) — Add null guards to `AgentTraceRecorder` constructor**

7. **[P2-2] Rename `DefaultSessionIdHeader`/`DefaultConversationIdHeader` → `DefaultSessionIdKey`/`DefaultConversationIdKey`**
   Update defaults to `"session_id"` / `"conversation_id"` to reflect StateBag semantics. This is a breaking change in options API — bump version comment or document it.

8. **[P2-3] Fix `GetContextForRunAsync` dead `messages` parameter**  
   Either remove or use for query embedding, with documentation.

9. **[P2-4] Fix `conversationId` fallback to use `sessionId` instead of a new GUID**  
   Prevents memory isolation between turns when StateBag isn't populated.

10. **[P2-5] ✅ Done (merged into P1-4) — Add `ConfigureAwait(false)` to `AgentTraceRecorder`**

11. **[P2-6] Add `AgentTraceRecorder` + `MemoryToolFactory` to `AddAgentMemoryFramework`**  
    Reduce consumer confusion about what's auto-registered.

12. **[P2-7] Deduplicate messages in `MafTypeMapper.ToContextMessages`**  
    Use `DistinctBy(m => m.MessageId)` before adding to output list.

13. **[P2-8] Add `StateKey` override to `Neo4jMemoryContextProvider`**  
    Defensive identity for pipeline introspection.

### Priority 3 — Nice-to-Have Enhancements

14. **[P3-1] Add XML doc comments to all public APIs in `AgentTraceRecorder`, `ContextFormatOptions`, `AgentFrameworkOptions`**

15. **[P3-2] Add `ServiceCollectionExtensions` registration tests**  
    Verify registered types, lifetimes, and that all public extension methods compile with correct type constraints.

16. **[P3-3] Document `base(null, null, null)` in `Neo4jMemoryContextProvider` constructor**  
    Add inline comment explaining which base-class constructor signature this calls.

17. **[P3-4] Clarify `MaxContextMessages` counts the prefix message**  
    Add XML doc: "Includes the prefix system message. Effective memory item limit is MaxContextMessages - 1 when ContextPrefix is non-empty."

18. **[P3-5] Add a `RealAgent` sample**  
    Demonstrates the full MAF pipeline: session creation, context provider, multi-turn memory, tool registration.

19. **[P3-6] Demonstrate `UseOpenTelemetry()` on the MAF agent in the BlendedAgent sample**  
    Show MAF's native OTel alongside our memory OTel for complete observability.

20. **[P3-7] Add `TreatWarningsAsErrors` check for BlendedAgent sample**  
    The duplicate `StubEmbeddingGenerator` may generate CS0436 or ambiguity errors; verify the build passes cleanly after fix.

---

## 7. MAF 1.1.0 Best Practices Checklist

| # | Best Practice | Status | Notes |
|---|--------------|--------|-------|
| 1 | Use `AsAIAgent()` extensions over manual construction | ❌ | Not demonstrated in any sample |
| 2 | Use `ManagedIdentityCredential` in production | 🟡 N/A | No credential setup in our code; samples use no LLM |
| 3 | Always use sessions for multi-turn conversations | ❌ | Samples don't create `AgentSession` |
| 4 | Implement `ChatHistoryProvider` for conversation storage | ❌ | `Neo4jChatMessageStore` is not a `ChatHistoryProvider` |
| 5 | Use pipeline architecture, place logic at the right layer | ✅ | `Neo4jMemoryContextProvider` is correctly at the `AIContextProvider` layer |
| 6 | Enable OpenTelemetry observability | ⚠️ | Our memory OTel exists; MAF's `UseOpenTelemetry()` not demonstrated |
| 7 | Use structured output (`RunAsync<T>`) for type-safe responses | 🟡 N/A | Not applicable to our adapter layer |
| 8 | Wrap sensitive tools with `ApprovalRequiredAIFunction` | 🟡 N/A | Write tools don't modify external state dangerously |
| 9 | Use compaction for long-running conversations | ⚠️ | Not integrated; our Neo4j backend manages retention |
| 10 | Provide both streaming and non-streaming middleware | ❌ | No middleware examples in samples |
| - | `ProviderSessionState<T>` for per-session provider state | ✅ | Not needed (stateless delegation); future if caching added |
| - | `InternalsVisibleTo` for testability | ✅ | Set correctly in `AssemblyInfo.cs` |
| - | No MAF types leaking into Core | ✅ | Boundary clean |
| - | ConfigureAwait(false) on async methods | ⚠️ | Missing in `AgentTraceRecorder` only |
| - | Null guards on constructor parameters | ⚠️ | Missing in `AgentTraceRecorder` and `MemoryToolFactory` |
| - | Error isolation (swallow, log, never rethrow) | ✅ | Applied consistently across all providers |
| - | Package version: `Microsoft.Agents.AI 1.1.0` | ⚠️ | Using `.Abstractions` variant — verify completeness |
