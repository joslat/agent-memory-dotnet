# AgentWithMemory Golden Path

This is the flagship Microsoft Agent Framework sample for Agent Memory for .NET. It mirrors the official MAF memory shape while using durable Neo4j-backed memory:

- `Neo4jMemoryContextProvider` injects memory context before each agent run and persists messages after the run.
- `MemoryToolFactory.CreateAIFunctions()` exposes model-callable memory tools.
- `WithMemoryIdentity(...)` stamps application, owner/user, session, and conversation identity into the `AgentSession` state bag.
- `.WithMemoryOwnerScoping(sp)` wraps the agent so every invocation — recall, tool calls, and persistence — is guaranteed to run in the same owner scope, automatically (#90).
- Session serialize/restore and a second session demonstrate memory continuity beyond one in-memory run.
- `MemoryOptions.Isolation.Mode = MemoryIsolationMode.StrictMultiTenant` is enabled, and a final step shows what happens when a call forgets `BeginOwnerScope`: it fails closed with `MemoryOwnerScopeRequiredException` instead of silently falling back to global/shared memory.

## Live Providers — No Mocks

This sample calls a **real** Azure OpenAI chat model and a **real** Azure OpenAI embedding model —
there is no mock `IChatClient` and no offline stub fallback. `RealAzureOpenAI.TryCreate` (from the
shared `AgentMemory.Samples.Shared` project) resolves `AZURE_OPENAI_ENDPOINT` / `AZURE_OPENAI_API_KEY`
/ `AZURE_OPENAI_DEPLOYMENT` / `AZURE_OPENAI_EMBEDDING_DEPLOYMENT` from the environment and fails fast
with setup instructions if credentials are missing. The chat client is wrapped in
`MemoryTraceChatClient`, which prints the `<recalled_memory>` blocks the context provider injects
before each live model call, in light blue. If Neo4j is unavailable, the sample reports the connection
failure and exits cleanly.

## Production Replacement Seam

Swap the deployment names / credentials via environment variables, or register a different
`Microsoft.Extensions.AI` provider entirely:

```csharp
builder.Services.AddSingleton<IChatClient>(sp => /* your OpenAI, Azure OpenAI, or Foundry chat client */);
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp => /* your MEAI embedding generator */);
```

Do not change the memory wiring when swapping providers. The important production pattern is wrapping the
agent once, at construction time:

```csharp
AIAgent agent = chatClient.AsAIAgent(agentOptions).WithMemoryOwnerScoping(sp);
```

The session state (`WithMemoryIdentity`) and the owner-scoping wrapper must agree. The state bag lets the
provider scope recall/persistence by application, owner, session, and conversation; the wrapper reads that
same identity and guarantees it encloses the complete invocation — including the tool-calling loop, which
a context-provider hook alone cannot guarantee (#90) — so model-invoked tools can't run against a
different or missing owner than the one the session declares. Passing the `IServiceProvider` (rather than
an `IWritableMemoryOwnerContext` instance directly) resolves the registered `AgentFrameworkOptions` from
the same container the provider uses, so a customized `Default*Key` can't cause the wrapper and the
provider to silently read a session's identity under different keys.

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

Use the `AgentMemory: golden path sample (local Neo4j)` task to run this sample against a local Neo4j instance. The task prompts for Neo4j connection settings; Azure OpenAI credentials (`AZURE_OPENAI_ENDPOINT` / `AZURE_OPENAI_API_KEY`) must already be set in the environment.
