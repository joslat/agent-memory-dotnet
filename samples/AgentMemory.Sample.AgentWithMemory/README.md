# AgentWithMemory Golden Path

This is the flagship Microsoft Agent Framework sample for Agent Memory for .NET. It mirrors the official MAF memory shape while using durable Neo4j-backed memory:

- `Neo4jMemoryContextProvider` injects memory context before each agent run and persists messages after the run.
- `MemoryToolFactory.CreateAIFunctions()` exposes model-callable memory tools.
- `WithMemoryIdentity(...)` stamps application, owner/user, session, and conversation identity into the `AgentSession` state bag.
- `IWritableMemoryOwnerContext.BeginOwnerScope(userId)` wraps every agent run so tool writes/searches inherit trusted host identity.
- Session serialize/restore and a second session demonstrate memory continuity beyond one in-memory run.
- `MemoryOptions.Isolation.Mode = MemoryIsolationMode.StrictMultiTenant` is enabled, and a final step shows what happens when a call forgets `BeginOwnerScope`: it fails closed with `MemoryOwnerScopeRequiredException` instead of silently falling back to global/shared memory.

## Offline Default

The sample registers deterministic offline defaults with `TryAddSingleton`:

- `EchoChatClient` for `IChatClient`.
- `StubEmbeddingGenerator` for `IEmbeddingGenerator<string, Embedding<float>>`.

That keeps the sample runnable without API keys. If Neo4j is unavailable, the sample reports the connection failure and exits cleanly.

## Production Replacement Seam

Production hosts should register real providers before or instead of these defaults:

```csharp
builder.Services.AddSingleton<IChatClient>(sp => /* OpenAI, Azure OpenAI, or Foundry chat client */);
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp => /* MEAI embedding generator */);
```

Do not change the memory wiring when swapping providers. The important production pattern is the identity wrapper around each run:

```csharp
using (ownerContext.BeginOwnerScope(userId))
{
    await agent.RunAsync(message, session);
}
```

The session state (`WithMemoryIdentity`) and ambient owner scope must agree. The state bag lets the provider scope recall/persistence by application, owner, session, and conversation; the ambient owner scope protects model-invoked tools from trusting user identity supplied by the model.

## Multi-Tenant Isolation Mode

```csharp
builder.Services.AddAgentMemoryCore(o => o.Isolation.Mode = MemoryIsolationMode.StrictMultiTenant);
```

This is the recommended setting for any deployment where more than one tenant's data lives in the same
Neo4j store. It doesn't change behavior for code that already scopes every call correctly (this sample's
golden path is unaffected) — it only changes what happens when a call *forgets* to: instead of quietly
resolving to global/shared memory, the operation throws `MemoryOwnerScopeRequiredException` before Neo4j
is ever touched. See [Isolation modes](../../docs/getting-started.md#isolation-modes) for the full mode
list and `docs/security/threat-model.md` (TT-01/TT-02) for the threat this closes.

## Related Checks

The live Neo4j integration shakedown includes coverage for this identity pattern: a facade/tool-style write under `BeginOwnerScope("alice")` is owner-stamped and remains invisible to `bob`.

## VS Code Run Task

Use the `AgentMemory: golden path sample (local Neo4j)` task to run this sample against a local Neo4j instance. The task prompts for Neo4j connection settings and keeps provider replacement in host DI; real chat or embedding providers should be registered by the host before the offline `TryAddSingleton` defaults.
