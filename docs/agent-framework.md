# Using AgentMemory with the Microsoft Agent Framework

AgentMemory gives a [Microsoft Agent Framework](https://learn.microsoft.com/en-us/agent-framework/)
(MAF) agent **durable, graph-native long-term memory** backed by Neo4j: the agent automatically
recalls relevant prior knowledge *before* each run and persists new knowledge *after* each response,
across sessions and process restarts.

> **This is the .NET equivalent of the official Neo4j Memory Provider for Agent Framework.**
> Microsoft's official provider ([`neo4j-agent-memory`](https://github.com/neo4j-labs/agent-memory),
> [Learn docs](https://learn.microsoft.com/en-us/agent-framework/integrations/neo4j-memory)) is
> currently **Python-only** — the Learn page states it is *"not yet available for C#."* AgentMemory
> implements the same three-layer memory model and the same MAF integration surface (a context
> provider plus memory tools) natively in .NET.

It plugs into MAF through the framework's own extension point — a custom
[`AIContextProvider`](https://learn.microsoft.com/en-us/agent-framework/agents/conversations/context-providers?pivots=programming-language-csharp) —
so no bespoke agent loop is required: you register the provider on the agent and MAF invokes it.

---

## Why a knowledge graph for agent memory?

- **Durable, structured persistence.** Memory lives in Neo4j as an owned knowledge graph, not in the
  session — it survives serialization, new sessions, and process restarts.
- **Automatic entity extraction.** As conversations happen, an extraction pipeline distills entities,
  facts, preferences, and relationships into the graph (with a real LLM configured).
- **Cross-session recall.** A brand-new session for the same owner/application recalls prior memory,
  because the knowledge lives in the graph rather than the MAF session state.
- **Multi-tenant isolation.** Recall and persistence are scoped by owner and application, so one
  tenant never sees another's memory.

## The three memory types

| Layer | What it stores |
|---|---|
| **Short-term** | Conversations, messages, ordering, sessions, roles, timestamps, embeddings. |
| **Long-term** | Entities, facts, preferences, relationships — with provenance, owner scope, confidence, and temporal state. |
| **Reasoning** | Traces, steps, tool calls, task embeddings, and prior execution patterns. |

---

## How it works — the `AIContextProvider` lifecycle

MAF lets a provider participate in every agent run through two hooks. `Neo4jMemoryContextProvider`
derives from `Microsoft.Agents.AI.AIContextProvider` and implements the framework's **simple path**:

| MAF hook (C#) | When | What AgentMemory does | Python equivalent |
|---|---|---|---|
| `ProvideAIContextAsync(InvokingContext)` → `AIContext` | **Before** the model call | Embeds the user turn, recalls relevant memory, and returns it as `AIContext.Messages` to prepend to the prompt. | `before_run` |
| `StoreAIContextAsync(InvokedContext)` | **After** the response | Persists the response messages and (optionally) runs extraction into the graph. Skipped if the run threw (`context.InvokeException`). | `after_run` |

This is the same **bidirectional** behavior the official provider describes ("auto-retrieve before
invocation, auto-save after responses") — recall is passive and automatic; you never call it by hand.

Alongside the passive provider, AgentMemory exposes **active memory tools** the model can call
explicitly (search memory, remember a preference, find entity connections) via
`MemoryToolFactory.CreateAIFunctions()` — the counterpart of the Python provider's
`create_memory_tools(memory)`.

---

## Prerequisites

- **.NET 8 or 9** SDK.
- A **Neo4j 5.x** instance (self-hosted or AuraDB). Quick local start:
  ```bash
  docker run -d --name neo4j -p 7474:7474 -p 7687:7687 -e NEO4J_AUTH=neo4j/password neo4j:5.26
  ```
- For **real** semantic recall and entity extraction: an embedding provider and a chat model via
  `Microsoft.Extensions.AI` (e.g. OpenAI / Azure OpenAI). AgentMemory ships deterministic **offline
  stubs** so the wiring runs with no API key — see [Real providers vs. offline defaults](#real-providers-vs-offline-defaults).

## Installation

```bash
dotnet add package AgentMemory                 # meta-package (core + Neo4j)
dotnet add package AgentMemory.AgentFramework  # the MAF adapter
```

---

## Usage (the golden path)

Register the memory stack and the MAF adapter, then attach the **context provider** and the **memory
tools** to a MAF agent:

```csharp
using AgentMemory;                       // AddNeo4jAgentMemory
using AgentMemory.AgentFramework;        // AddAgentMemoryFramework, Neo4jMemoryContextProvider, WithMemoryIdentity
using AgentMemory.AgentFramework.Tools;  // MemoryToolFactory
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();

// 1. Memory + Neo4j (this single call registers Core internally — no separate AddAgentMemoryCore needed)
services.AddNeo4jAgentMemory(
    configureMemory: _ => { },
    configureNeo4j: neo4j =>
    {
        neo4j.Uri      = "bolt://localhost:7687";
        neo4j.Username = "neo4j";
        neo4j.Password = "password";
    });

// 2. Your chat + embedding providers (swap the stubs for real MEAI providers in production)
services.AddSingleton<IChatClient>(/* your OpenAI/Azure chat client */);
services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(/* your embedding generator */);

// 3. The MAF adapter — registers the context provider, memory tools, chat store, and trace recorder
services.AddAgentMemoryFramework(options =>
{
    options.AutoExtractOnPersist          = true;   // extract entities/facts/preferences after each run
    options.ContextFormat.IncludeEntities = true;
    options.ContextFormat.IncludeFacts    = true;
    options.ContextFormat.IncludePreferences = true;
});

await using var provider = services.BuildServiceProvider();
await using var scope = provider.CreateAsyncScope();
var sp = scope.ServiceProvider;

// One-time: ensure the Neo4j schema (constraints + indexes) exists
await sp.GetRequiredService<ISchemaBootstrapper>().BootstrapAsync();

// 4. Build the agent — attach the passive context provider AND the active memory tools
var memoryProvider = sp.GetRequiredService<Neo4jMemoryContextProvider>();
var memoryTools    = sp.GetRequiredService<MemoryToolFactory>().CreateAIFunctions();

AIAgent agent = sp.GetRequiredService<IChatClient>().AsAIAgent(new ChatClientAgentOptions
{
    Name        = "MemoryAgent",
    ChatOptions = new ChatOptions
    {
        Instructions = "You are a helpful assistant with durable, graph-backed long-term memory.",
        Tools        = [.. memoryTools],
    },
    AIContextProviders = [memoryProvider],   // <-- the canonical MAF registration point
});

// 5. Run — memory is recalled before each turn and persisted after, automatically
var session = (await agent.CreateSessionAsync())
    .WithMemoryIdentity(userId: "user-123", sessionId: "session-a", applicationId: "my-app");

await agent.RunAsync("Hi, I prefer window seats on flights.", session);

// A brand-new session for the same owner still recalls the durable memory:
var later = (await agent.CreateSessionAsync())
    .WithMemoryIdentity(userId: "user-123", sessionId: "session-b", applicationId: "my-app");
Console.WriteLine(await agent.RunAsync("What do you know about my travel preferences?", later));
```

`AIContextProviders = [memoryProvider]` is the framework's own registration point — the same list the
[MAF context-providers guide](https://learn.microsoft.com/en-us/agent-framework/agents/conversations/context-providers?pivots=programming-language-csharp)
uses. From there, MAF drives the provider's before/after hooks on every `RunAsync`.

### Identity and scoping

`WithMemoryIdentity(userId, sessionId, conversationId?, applicationId?)` stamps the run's identity onto
the MAF session. AgentMemory reads it on every invocation to scope recall and writes:

- **owner** (`userId`) — the multi-tenant isolation boundary; null means shared/global.
- **application** (`applicationId`) — routes the memory store (shared DB by default; optionally a
  database per application).
- **session** / **conversation** — short-term ordering and per-run context.

A full runnable version of this flow is
[`samples/AgentMemory.Sample.AgentWithMemory`](../samples/AgentMemory.Sample.AgentWithMemory/) — the
.NET equivalent of the official MAF [`04_memory`](https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/01-get-started/04_memory)
sample. It also demonstrates `SerializeSessionAsync` / `DeserializeSessionAsync` and durable
cross-session recall.

---

## Design note: per-session state — scoped + `StateBag` vs. singleton + `ProviderSessionState<T>`

The [MAF context-providers guide](https://learn.microsoft.com/en-us/agent-framework/agents/conversations/context-providers?pivots=programming-language-csharp)
shows a **singleton provider + `ProviderSessionState<T>`** pattern. AgentMemory deliberately uses a
**scoped provider + the MAF session `StateBag`** instead. This is an intentional, load-bearing
decision — here is exactly how the two compare and why ours is the correct one for a multi-tenant
memory library.

| | MAF sample idiom | **AgentMemory** |
|---|---|---|
| Provider lifetime | **Singleton** (one instance for the app) | **Scoped** (one per DI scope / request) |
| Per-session state store | `ProviderSessionState<T>` keyed on the session | The MAF session **`StateBag`** (via `WithMemoryIdentity(...)`) |
| Who owns the identity | The provider **mints** it lazily (`state.MemoriesId ??= …`) | The **host supplies** it (owner / application / session) |
| Multi-tenant scope | Not addressed | **Per-request owner + store isolation via scoped services** |
| State held in provider **fields** | None (the point of the pattern) | **None** (only stateless services) |

### Why AgentMemory cannot use the singleton idiom

`Neo4jMemoryContextProvider` depends on `IMemoryService` and `IEmbeddingOrchestrator`, both registered
**`Scoped`**. They are scoped on purpose: they carry the per-request Neo4j transaction context and the
owner/store scope that powers multi-tenant isolation (R1/R1b). A **singleton** provider holding
**scoped** services is a captive-dependency lifetime violation — it fails
`ServiceProvider.BuildServiceProvider(validateScopes/validateOnBuild)` at **startup**, not at runtime.

To make the provider a singleton, you would have to make `IMemoryService` a singleton too — which
collapses the per-request scoping the entire multi-tenant model relies on. So the singleton +
`ProviderSessionState<T>` idiom is **architecturally incompatible** with a scoped, multi-tenant memory
stack. The provider *must* be scoped.

### Why scoped + `StateBag` is correct — and honors the same rule

The real requirement behind the MAF guidance is **"the provider instance is shared, so it must not
hold per-session state in fields."** `ProviderSessionState<T>` is merely *one mechanism* to satisfy
that. AgentMemory satisfies the identical rule by a different, necessary mechanism:

- The provider's fields are **all stateless services** — it holds **zero** per-session state.
- Per-session identity lives in the **session itself** — the MAF `StateBag`, written by
  `WithMemoryIdentity(...)` and read fresh on every invocation, never cached on the provider.
- Scoping additionally threads the **host-supplied owner/application** identity that multi-tenancy
  needs — something the provider-mints-its-own-id pattern does not model at all.

So both approaches obey the same principle (no per-session state in the shared provider); AgentMemory's
mechanism is the one its scoped services require, and it does strictly more (multi-tenant scope).

### It is verified working

- **Session serialize/restore round-trips.** The `AgentWithMemory` sample serializes a live session to
  JSON (`SerializeSessionAsync`) and restores it (`DeserializeSessionAsync`), then continues the
  conversation with full memory intact.
- **Durable cross-session recall works.** A brand-new session for the same owner/application recalls
  prior memory from Neo4j — the exact bidirectional behavior the official provider promises.
- **It boots.** Because the provider is scoped, the container passes `ValidateOnBuild`/scope
  validation; the singleton alternative would not.

> **In short:** consistency with the MAF *sample* would mean a singleton provider, which cannot inject
> our scoped multi-tenant services and would fail at startup. AgentMemory keeps consistency with the
> MAF *rule* (no per-session state in the shared provider) using the mechanism its architecture
> requires — and gains multi-tenant scoping the sample idiom doesn't offer.

---

## The full MAF integration surface

`AddAgentMemoryFramework(...)` registers everything you need to wire into MAF:

| Type | MAF role |
|---|---|
| `Neo4jMemoryContextProvider` | `AIContextProvider` — passive, bidirectional long-term memory (this guide). |
| `MemoryToolFactory` | Builds `AIFunction` memory **tools** the model can call explicitly. |
| `Neo4jChatHistoryProvider` | MAF `ChatHistoryProvider` — per-session conversation history (distinct from long-term memory). |
| `Neo4jChatMessageStore` | MAF `ChatMessageStore` — durable message storage for a thread. |
| `AgentTraceRecorder` | Records reasoning traces / tool-call patterns into the reasoning layer. |
| `Neo4jMicrosoftMemoryFacade` | A lower-level `GetContextForRunAsync` / `PersistAfterRunAsync` facade for hosts that drive the loop manually instead of using the context provider. |

## Configuration

`AgentFrameworkOptions` (via `AddAgentMemoryFramework`):

- `AutoExtractOnPersist` — run entity/fact/preference extraction after each persisted turn.
- `ContextFormat.IncludeEntities` / `IncludeFacts` / `IncludePreferences` / `IncludeReasoningTraces` —
  which memory kinds to inject into the prompt.
- `ContextFormat.MaxContextMessages` / `ContextPrefix` — shape the injected context block.

## Real providers vs. offline defaults

AgentMemory ships deterministic **stub** providers (`StubEmbeddingGenerator`, and the samples use a
mock `IChatClient`) so the full flow runs offline with no API key — useful for tests and first-run.
They log a warning when used and are **not** production embeddings. For real semantic recall and
entity extraction, register real `Microsoft.Extensions.AI` providers; **the memory wiring is
identical** — you only swap the `IChatClient` and `IEmbeddingGenerator` registrations. Match the
embedding dimensions to the Neo4j vector-index dimensions (see
[getting-started § Embedding Providers](getting-started.md#7-embedding-providers)).

---

## Resources

- [Getting Started](getting-started.md) — install, configure, multi-tenant setup.
- [`samples/AgentMemory.Sample.AgentWithMemory`](../samples/AgentMemory.Sample.AgentWithMemory/) — the flagship MAF golden path (runs offline).
- [`samples/README.md`](../samples/README.md) — all samples + the official-Python ↔ .NET API mapping.
- [MAF context providers (Learn)](https://learn.microsoft.com/en-us/agent-framework/agents/conversations/context-providers?pivots=programming-language-csharp) — the `AIContextProvider` contract.
- [Neo4j Memory Provider for Agent Framework (Learn)](https://learn.microsoft.com/en-us/agent-framework/integrations/neo4j-memory) — the official (Python) provider this mirrors.
- [`neo4j-labs/agent-memory`](https://github.com/neo4j-labs/agent-memory) — the upstream Python implementation.
