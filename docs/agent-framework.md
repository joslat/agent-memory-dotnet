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
- **Multi-tenant capable.** Recall and persistence are scoped by owner and application once the host
  establishes an owner scope for the run (see [Owner isolation](getting-started.md#owner-isolation));
  unscoped operations remain global (shared/admin) by default.

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
| `StoreAIContextAsync(InvokedContext)` | **After** the response | Persists the response messages and (optionally) runs extraction over the complete turn (request + response) into the graph. Skipped if the run threw (`context.InvokeException`). | `after_run` |

This is the same **bidirectional** behavior the official provider describes ("auto-retrieve before
invocation, auto-save after responses") — recall is passive and automatic; you never call it by hand.

Native recall via `Neo4jMemoryContextProvider` respects your configured `MemoryOptions.Recall` (limits,
`MinSimilarityScore`, `BlendMode`, etc.) — the same options that shape a direct
`IMemoryService.RecallAsync(...)` call (#87). The one exception is `RecallOptions.Scope`: native recall
always derives scope from the invocation's authenticated owner (via #100's isolation policy), never from a
statically configured `Scope`, so a global config value can't silently override the real, per-invocation
owner. Automatic extraction (`AutoExtractOnPersist`) considers the **complete turn** — both what the user asked
and what the assistant answered (#89), filtered to user-role content so a system prompt or other non-user
text isn't minted into spurious entities/facts/preferences every turn — so a preference or fact the user
states is captured even if the assistant never repeats it back. `Neo4jChatHistoryProvider` persists both
request and response messages as real `:Message` nodes (unchanged), so extraction there has full
provenance. `Neo4jMemoryContextProvider` deliberately does **not** persist request messages as new nodes
— only response messages are — because a caller-constructed request `ChatMessage` essentially never
carries a stable identity that another persisting component would also see, so request-message persistence
ownership intentionally stays solely with `Neo4jChatHistoryProvider`. The practical effect: a
fact/preference extracted from the user's own request is still created and recallable, but both its
`EXTRACTED_FROM` provenance edge and its own `source_message_ids` property will reference a message id that
was never persisted, unless another component also persisted that exact message.

**Duplicate message persistence across components (#89)**: `Neo4jChatHistoryProvider` narrows MAF's
default request-message filter to `AgentRequestMessageSourceType.External` only, so it never re-persists
another configured `AIContextProvider`'s (e.g. `Neo4jMemoryContextProvider`'s) injected recalled-memory
messages as new nodes every turn. On the **response** side, message persistence is idempotent by id:
`Neo4jMemoryContextProvider`, `Neo4jChatHistoryProvider`, and `Neo4jChatMessageStore` all persist a response
message under a deterministic id derived from the underlying `ChatMessage.MessageId` when the `IChatClient`
populates one (true for many production clients, e.g. those backed by the OpenAI Responses API) — so if
more than one of these components observes the same response message, they converge on the same
`:Message` node instead of creating a duplicate. When the underlying client does **not** populate
`MessageId`, each component still falls back to today's behavior (a fresh id per call), so combining more
than one message-persisting component on the same agent can still duplicate that response message — this
is a known, disclosed limitation of relying on provider-native identity rather than a cross-component
idempotency protocol; there is no plan to add content-hash-based deduplication, since that would risk
silently collapsing two genuinely distinct occurrences of identical text (e.g. the assistant saying
"Understood." twice in different turns) into one node.

**Non-text content policy**: a `ChatMessage` whose content is exclusively non-`TextContent` (e.g. a
function/tool call, a function/tool result, or a reasoning trace) has an empty `.Text`, and `Neo4jMemoryContextProvider`/`Neo4jChatHistoryProvider` exclude it from both persistence and automatic
extraction — not just "tool messages," but any message carrying no literal text for a human or the
extraction pipeline to act on. This guard is specific to those two providers; the lower-level
`Neo4jChatMessageStore`/`Neo4jMicrosoftMemoryFacade` path persists every message it's given regardless of
text content, so a host driving that path directly is responsible for filtering non-text messages itself
if it wants the same behavior.

`Neo4jMicrosoftMemoryFacade` (the lower-level, manually-driven alternative to the context provider)
does not yet wire configured `RecallOptions` into its own recall call — a known gap for a future pass, not
covered by this fix.

Alongside the passive provider, AgentMemory exposes **active memory tools** the model can call
explicitly (search memory, remember a preference, find entity connections) via
`MemoryToolFactory.CreateAIFunctions()` — the counterpart of the Python provider's
`create_memory_tools(memory)`.

### Optional: surface the memory tools from the context provider itself

Wiring both pieces normally takes two lines:

```csharp
var memoryProvider = sp.GetRequiredService<Neo4jMemoryContextProvider>();
var memoryTools    = sp.GetRequiredService<MemoryToolFactory>().CreateAIFunctions();

AIAgent agent = chatClient.AsAIAgent(new ChatClientAgentOptions
{
    ChatOptions        = new ChatOptions { Tools = [.. memoryTools] },
    AIContextProviders = [memoryProvider],
});
```

Set `AgentFrameworkOptions.ExposeMemoryToolsFromContextProvider = true` and `Neo4jMemoryContextProvider`
surfaces the same six tools itself via [`AIContext.Tools`](https://learn.microsoft.com/en-us/agent-framework/agents/conversations/context-providers) —
so `AIContextProviders = [memoryProvider]` alone is enough:

```csharp
services.AddAgentMemoryFramework(options =>
{
    options.ExposeMemoryToolsFromContextProvider = true;
});
```

**Defaults to `false`.** `AddAgentMemoryFramework` registers `MemoryToolFactory` unconditionally, and the
tools it creates include write-capable ones (`remember_fact`, `remember_preference`) — so this stays
opt-in rather than firing just because the factory happens to be in DI. When enabled, every recall
outcome (a hit, an empty recall, a recall failure, or no user message at all) still surfaces the tools —
only `Messages` varies by outcome, so a quiet turn never silently loses tool availability.

---

## Prerequisites

- **.NET 8, 9, or 10** SDK.
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
using AgentMemory.Abstractions.Services; // ISchemaBootstrapper
using AgentMemory.AgentFramework;        // AddAgentMemoryFramework, Neo4jMemoryContextProvider, WithMemoryIdentity, WithMemoryOwnerScoping
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
}).WithMemoryOwnerScoping(sp);
// ^ guarantees the owner scope spans the COMPLETE invocation -- recall, tool calls, and persistence --
// not just the portion inside the context-provider hook. Passing the IServiceProvider (rather than an
// IWritableMemoryOwnerContext instance directly) resolves it from the SAME container the provider uses,
// so it can never read a session's identity under different StateBag keys than the provider if you ever
// customize AgentFrameworkOptions.Default*Key. See "Identity and scoping" below.

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

- **owner** (`userId`) — the multi-tenant isolation boundary when set; null means shared/global (no
  isolation) — always pass it from an authenticated user/tenant context, never from LLM/client input.
- **application** (`applicationId`) — routes the memory store (shared DB by default; optionally a
  database per application).
- **session** / **conversation** — short-term ordering and per-run context.

**Wrap the agent once with `.WithMemoryOwnerScoping(sp)`** (shown above) so the owner scope guaranteed
spans the *complete* invocation — passive recall, the model call, the full tool-calling loop (so
`search_memory`/`remember_*` etc. see the same owner), and automatic persistence — as one unbroken async
chain. This matters because `Neo4jMemoryContextProvider`'s own pre-run hook (`ProvideAIContextAsync`)
cannot guarantee this on its own: it suspends on real I/O (embedding + recall), so by the time MAF's
tool-calling loop runs — *after* that hook returns — a value it set on the `AsyncLocal`-backed owner
context is no longer reliably visible. `WithMemoryOwnerScoping` closes that gap by bracketing the entire
`RunAsync`/`RunStreamingAsync` call instead of just the hook. Apply it once at agent-construction time; you
don't need to manually wrap every `RunAsync` call in `ownerContext.BeginOwnerScope(userId)` — the
lower-level mechanism the wrapper uses internally, still available for hosts that need finer-grained
control over an unwrapped agent.

Prefer the `IServiceProvider` overload (`.WithMemoryOwnerScoping(sp)`) over the one that takes an
`IWritableMemoryOwnerContext` directly: it also resolves the registered `AgentFrameworkOptions` from the
same container `Neo4jMemoryContextProvider` uses. If you ever customize
`AgentFrameworkOptions.Default*Key` (e.g. `DefaultUserIdKey`), the two must read the session's identity
under the *same* StateBag keys — passing the `IServiceProvider` guarantees that; constructing the options
by hand (or omitting them) risks the wrapper silently reading a different key than the provider wrote,
which unscopes the whole invocation with no error.

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
- `ContextFormat.MaxChatHistoryMessages` — caps ONLY recalled chat history (`RecentMessages`/
  `RelevantMessages`); it does not cap the complete context. The prefix and every memory-derived block
  (entities/facts/preferences/reasoning traces/GraphRAG) are durable long-term memory and are always
  included on top when their `Include*` flag (or `ContextPrefix`) is set — they are never truncated to
  make room for chat history (#91). Zero means no recalled chat history, but memory blocks may still be
  included. For a hard cap on total prompt size, use `ContextBudget.MaxTokens`/`MaxCharacters` instead.
  `MaxContextMessages` is a `[Obsolete]` compatibility alias for the same value.
- `ContextFormat.ContextPrefix` — the untrusted-reference-data framing prepended to the context block.

**Trust boundary (#92 Phase 1).** Recalled entities/facts/preferences/reasoning traces/GraphRAG content
may originate from users, external documents, tool results, or the model itself — it is not injected as a
raw, unrestricted system instruction. Each block is delimited and angle-bracket-escaped
(`<recalled_memory category="...">...</recalled_memory>`, with `<`/`>` in the content escaped so it can't
forge or prematurely close its own boundary), and the default `ContextPrefix` explicitly tells the model
this content is untrusted reference data, not instructions to follow.

Be clear about what this does and doesn't cover:
- The escaping defeats *boundary forgery* specifically (a recalled value can't fake or close the
  `<recalled_memory>` tag). It does **not** detect or block injection techniques that don't rely on that
  tag — role-header conventions, code fences, or a plain-language instruction like "ignore previous
  instructions" all pass through unescaped inside the block. The `ContextPrefix` instruction is what
  actually asks the model not to follow them; the delimiter only guarantees the model can tell where the
  untrusted block starts and ends. An instruction-like-content detector is explicit future work (#92).
- **Recalled conversation history (`RelevantMessages` — messages resurfaced by semantic search, not the
  current turn's recent chat) is not wrapped by this mitigation and keeps its originally-persisted role.**
  If a prior turn's history ever contained a `system`-role message, recalling it replays it with full
  system authority. This is a known, disclosed gap, not silently dropped — the Phase-1 slice covers
  exactly the categories named in its scope (entities/facts/preferences/traces/GraphRAG); recalled message
  history is a separate surface for a future pass.
- Setting `ContextPrefix` to `string.Empty` — or having already customized it before this default changed
  — removes the trust framing (the content stays wrapped in `<recalled_memory>` tags, but nothing tells
  the model what those mean). A host with a pre-existing custom `ContextPrefix` should add equivalent
  framing to it; the new default only helps hosts using the out-of-the-box value.

This is a first, intentionally small slice of the full trust model proposed in #92 (trust metadata, an
admission policy, configurable message roles, and instruction-like-content detection remain open work).

## Real providers vs. offline defaults

AgentMemory ships a deterministic **stub** embedding provider (`StubEmbeddingGenerator`) for unit
tests, where determinism (not accuracy) is what matters. None of the samples use it anymore: the
agent samples that drive a model (AgentWithMemory, RealAgent, MemoryToolsAgent, ChatHistoryProvider,
ShoppingAssistant) call a **real** Azure OpenAI chat model and a **real** Azure OpenAI embedding
model — there is no mock `IChatClient`; the facade-only samples (BlendedAgent, MinimalAgent,
McpHost) never drive a chat model but still use a **real** Azure OpenAI embedding model. See
`samples/AgentMemory.Samples.Shared` for the shared wiring (`RealAzureOpenAI`). `StubEmbeddingGenerator`
logs a warning when used and is **not** production embeddings. **The memory wiring is identical**
regardless of provider — you only swap the `IChatClient` and `IEmbeddingGenerator` registrations.
Match the embedding dimensions to the Neo4j vector-index dimensions (see
[getting-started § Embedding Providers](getting-started.md#7-embedding-providers)).

---

## Resources

- [Getting Started](getting-started.md) — install, configure, multi-tenant setup.
- [`samples/AgentMemory.Sample.AgentWithMemory`](../samples/AgentMemory.Sample.AgentWithMemory/) — the flagship MAF golden path (runs offline).
- [`samples/README.md`](../samples/README.md) — all samples + the official-Python ↔ .NET API mapping.
- [MAF context providers (Learn)](https://learn.microsoft.com/en-us/agent-framework/agents/conversations/context-providers?pivots=programming-language-csharp) — the `AIContextProvider` contract.
- [Neo4j Memory Provider for Agent Framework (Learn)](https://learn.microsoft.com/en-us/agent-framework/integrations/neo4j-memory) — the official (Python) provider this mirrors.
- [`neo4j-labs/agent-memory`](https://github.com/neo4j-labs/agent-memory) — the upstream Python implementation.
