# Getting Started — Agent Memory for .NET

**Prerequisites covered:** .NET 9, Neo4j, NuGet packages, DI configuration, first memory store.

---

## 1. Prerequisites

| Requirement | Version | Notes |
|-------------|---------|-------|
| .NET SDK | **9.0+** | [Download](https://dotnet.microsoft.com/download) |
| Neo4j | **5.x** | Local, Docker, or [Neo4j Aura](https://neo4j.com/cloud/platform/aura-graph-database/) |
| Embedding provider | Any MEAI-compatible | OpenAI, Azure OpenAI, or another `IEmbeddingGenerator<string, Embedding<float>>` implementation |

### Neo4j via Docker (quickest local setup)

```bash
docker run \
  --name neo4j-memory \
  -p 7474:7474 -p 7687:7687 \
  -e NEO4J_AUTH=neo4j/password \
  neo4j:5
```

The browser UI is available at `http://localhost:7474`.

---

## 2. Installation

### Option A — Meta-package (recommended for most projects)

```bash
dotnet add package AgentMemory
```

This pulls in `Abstractions`, `Core`, `Neo4j`, and `Extraction.Llm` in one reference.

### Option B — Individual packages

Install only what you need:

```bash
dotnet add package AgentMemory.Abstractions
dotnet add package AgentMemory.Core
dotnet add package AgentMemory.Neo4j
dotnet add package AgentMemory.Extraction.Llm     # optional: LLM-based extraction
dotnet add package AgentMemory.AgentFramework     # optional: Microsoft Agent Framework
dotnet add package AgentMemory.SemanticKernel     # optional: Semantic Kernel
dotnet add package AgentMemory.McpServer          # optional: MCP server
dotnet add package AgentMemory.Observability      # optional: OpenTelemetry
```

---

## 3. Configuration

### 3.1 DI registration

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgentMemory.Core;
using AgentMemory.Core.Stubs;
using AgentMemory.Neo4j.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);

// 1. Neo4j infrastructure — reads Uri/Username/Password from config or environment
builder.Services.AddNeo4jAgentMemory(options =>
{
    options.Uri      = "bolt://localhost:7687";
    options.Username = "neo4j";
    options.Password = "password";
});

// 2. Core memory services
builder.Services.AddAgentMemoryCore(_ => { });

// 3. Infrastructure helpers
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IIdGenerator, GuidIdGenerator>();

// 4. Embedding provider — swap StubEmbeddingGenerator for a real provider in production
//    Real example: builder.Services.AddOpenAIEmbeddingGenerator("text-embedding-3-small", apiKey);
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>, StubEmbeddingGenerator>();

var host = builder.Build();
```

### 3.2 Configuration via `appsettings.json`

```json
{
  "Neo4j": {
    "Uri": "bolt://localhost:7687",
    "Username": "neo4j",
    "Password": "your-password-here",
    "Database": "neo4j"
  }
}
```

> **Note:** `Database` defaults to `"neo4j"`. Set it explicitly if your instance uses a different database name — misconfiguring this causes silent connection failures.

Read in DI setup:

```csharp
builder.Services.AddNeo4jAgentMemory(options =>
{
    options.Uri      = builder.Configuration["Neo4j:Uri"]      ?? "bolt://localhost:7687";
    options.Username = builder.Configuration["Neo4j:Username"] ?? "neo4j";
    options.Password = builder.Configuration["Neo4j:Password"] ?? "";
    options.Database = builder.Configuration["Neo4j:Database"] ?? "neo4j";
});
```

### 3.3 Schema bootstrap

Run once on startup (idempotent — safe to call every time):

```csharp
var bootstrapper = host.Services.GetRequiredService<ISchemaBootstrapper>();
await bootstrapper.BootstrapAsync();
```

`ISchemaBootstrapper` is registered by `AddNeo4jAgentMemory()` — resolve it from the DI container rather than instantiating it directly.

### 3.4 Multiple databases & instances (multi-tenant)

The data model is three tiers — **store ⊃ owner ⊃ session**. How you isolate tenants depends on whether you want *logical* or *physical* separation, and whether your tenants share one Neo4j or live on different ones.

#### Default — one database, logical isolation (Neo4j Community)
Out of the box the strategy is `SharedDatabase`: a single database, with tenants isolated by `owner_id` / `MemoryScope`. Pass a `userId` on recall/extraction (or set the ambient owner context) and a tenant only sees their own + shared memories. **No extra setup — this is the recommended starting point** and works on Community Edition.

#### A database per application (Neo4j Enterprise / AuraDB)
For *physical* isolation, switch to `DatabasePerApplication`: each `ApplicationId` routes to its **own** Neo4j database (`<prefix><appId>`, default prefix `mem-`), which the library **creates and schema-bootstraps automatically on first use** — you never run `CREATE DATABASE` by hand. (Requires Enterprise or AuraDB; Community supports a single user database.)

```csharp
using AgentMemory.Neo4j.Infrastructure;

builder.Services.AddNeo4jAgentMemory(
    configureMemory: _ => { },
    configureNeo4j: neo4j =>
    {
        neo4j.Uri = "neo4j+s://xxxx.databases.neo4j.io"; // or bolt://localhost:7687
        neo4j.Username = "neo4j";
        neo4j.Password = "...";
    },
    configureStore: store =>
    {
        store.Strategy       = MemoryStorageStrategy.DatabasePerApplication;
        store.DatabasePrefix = "mem-";   // database name = mem-<sanitized-appId>
        store.AutoProvision  = true;     // CREATE DATABASE + bootstrap on first touch
    });
```

> The meta-package `AddNeo4jAgentMemory(configureMemory, configureNeo4j, configureLlm, configureStore)` forwards `configureStore`; the `AgentMemory.Neo4j` registration `AddNeo4jAgentMemory(configureNeo4j, configureStore)` accepts it directly.

**Route per request** by setting the ambient store context — it's `AsyncLocal`-backed, so concurrent requests don't cross:

```csharp
var storeCtx = sp.GetRequiredService<IWritableMemoryStoreContext>();
storeCtx.ApplicationId = "tenant-acme";   // → database "mem-tenant-acme"; null → the default database
```

The Microsoft Agent Framework providers do this for you automatically from the agent session's `application_id` state key.

#### Separate Neo4j *instances* (different servers/clusters)
The store tier routes across **databases within one instance**, not across instances — a DI container binds one `Neo4jOptions` (one URI/driver). To target genuinely separate servers (e.g. dev vs prod, or a dedicated cluster per large tenant):

- **Run separate host processes / DI containers**, one `AddNeo4jAgentMemory` each — simplest for environment separation or one-Neo4j-per-tenant; or
- **Register your own `INeo4jSessionFactory` / `INeo4jDriverFactory`** (both are `TryAdd`, so yours wins) to pick the driver/URI per `ApplicationId` — for routing a single app across multiple instances.

> Local Enterprise for testing `DatabasePerApplication`: use [`deploy/docker-compose.enterprise.yml`](../deploy/docker-compose.enterprise.yml) (Enterprise image + accepted eval license + APOC & GDS plugins).

---

## 4. First Memory Store

The primary facade is `IMemoryService`. Resolve it from DI:

```csharp
using AgentMemory.Abstractions.Services;
using AgentMemory.Abstractions.Domain;

await using var scope = host.Services.CreateAsyncScope();
var memory = scope.ServiceProvider.GetRequiredService<IMemoryService>();

const string sessionId      = "session-01";
const string conversationId = "conv-01";

// Store a message
var message = await memory.AddMessageAsync(
    sessionId:      sessionId,
    conversationId: conversationId,
    role:           "user",
    content:        "My name is Alice and I prefer dark mode.");

Console.WriteLine($"Stored message: {message.MessageId}");

// Recall context for a follow-up query
var recall = await memory.RecallAsync(new RecallRequest
{
    SessionId = sessionId,
    Query     = "What does Alice prefer?",
});

Console.WriteLine($"Recalled {recall.Context.RecentMessages.Items.Count} message(s), " +
                  $"{recall.Context.RelevantEntities.Items.Count} entity/entities.");
```

### 4.1 Batch messages

```csharp
await memory.AddMessagesAsync(new[]
{
    new Message { MessageId = Guid.NewGuid().ToString("N"),
                  SessionId = sessionId, ConversationId = conversationId,
                  Role = "user",      Content = "Set theme to dark.",
                  TimestampUtc = DateTimeOffset.UtcNow },
    new Message { MessageId = Guid.NewGuid().ToString("N"),
                  SessionId = sessionId, ConversationId = conversationId,
                  Role = "assistant", Content = "Done! Theme set to dark.",
                  TimestampUtc = DateTimeOffset.UtcNow },
});
```

### 4.2 Extract long-term memory

After adding messages, run the extraction pipeline to surface entities, facts, and preferences into long-term memory:

```csharp
await memory.ExtractAndPersistAsync(new ExtractionRequest
{
    SessionId = sessionId,
});
```

### 4.3 Point-in-time recall

```csharp
var snapshot = await memory.RecallAsOfAsync(
    new RecallRequest { SessionId = sessionId, Query = "Alice preferences" },
    asOf: DateTimeOffset.UtcNow.AddDays(-7));
```

---

## 5. Microsoft Agent Framework Integration

```csharp
using AgentMemory.AgentFramework;

builder.Services.AddAgentMemoryFramework(options =>
{
    options.AutoExtractOnPersist           = true;
    options.ContextFormat.IncludeEntities  = true;
    options.ContextFormat.IncludeFacts     = true;
    options.ContextFormat.IncludePreferences = true;
});
builder.Services.AddScoped<AgentTraceRecorder>();
builder.Services.AddScoped<MemoryToolFactory>();
```

Use the facade in an agent pipeline:

```csharp
await using var scope = host.Services.CreateAsyncScope();
var facade = scope.ServiceProvider.GetRequiredService<Neo4jMicrosoftMemoryFacade>();

// Pre-run: inject prior memory context into the agent
var priorMessages = await facade.GetContextForRunAsync([], sessionId, conversationId);

// Post-run: persist the agent's output messages
// newMessages is the list of ChatMessage objects produced by the agent run
IList<ChatMessage> newMessages = agentResult.Messages.ToList();
await facade.PersistAfterRunAsync(newMessages, sessionId, conversationId);
```

> **Optional — GraphRAG retrieval:** If you want `IGraphRagContextSource` for blended retrieval, call
> `builder.Services.AddGraphRagAdapter(opts => { ... });` **after** `AddNeo4jAgentMemory()`. This is
> a separate registration and is not included by default.

---

## 6. Semantic Kernel Integration

```bash
dotnet add package AgentMemory.SemanticKernel
```

```csharp
using AgentMemory.SemanticKernel;

builder.AddNeo4jMemoryPlugin(); // registers as SK plugin
```

After registration the memory plugin is available in `kernel.Plugins`. You can invoke it directly or let the kernel's function-calling loop use it automatically:

```csharp
// Explicit plugin invocation example
var result = await kernel.InvokeAsync("Neo4jMemory", "recall",
    new KernelArguments { ["query"] = "Alice preferences", ["sessionId"] = sessionId });
```

For a full runnable example, see `samples/AgentMemory.Sample.MinimalAgent`.

---

## 7. Embedding Providers

Replace `StubEmbeddingGenerator` with a real provider. Any `IEmbeddingGenerator<string, Embedding<float>>` from `Microsoft.Extensions.AI` works:

```bash
# OpenAI example
dotnet add package Microsoft.Extensions.AI.OpenAI
```

```csharp
using Microsoft.Extensions.AI;
using OpenAI;

var openAiClient = new OpenAIClient(apiKey);
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
    openAiClient.AsEmbeddingGenerator("text-embedding-3-small"));
```

---

## 8. Next Steps

| Resource | Description |
|----------|-------------|
| [`README.md`](../README.md) | Project overview and architecture summary |
| [`docs/architecture.md`](architecture.md) | Full architecture walkthrough — packages, layers, boundaries |
| [`docs/schema.md`](schema.md) | Neo4j graph schema — node types, relationships, indexes |
| [`docs/nextsteps.md`](nextsteps.md) | Active forward-looking backlog |
| [`samples/AgentMemory.Sample.MinimalAgent`](../samples/AgentMemory.Sample.MinimalAgent/) | Runnable MAF sample — best starting point |
| [`samples/AgentMemory.Sample.BlendedAgent`](../samples/AgentMemory.Sample.BlendedAgent/) | Blended GraphRAG + memory sample |
| [`samples/AgentMemory.Sample.McpHost`](../samples/AgentMemory.Sample.McpHost/) | MCP server host sample |
| [`CONTRIBUTING.md`](../CONTRIBUTING.md) | How to build, test, and contribute |
