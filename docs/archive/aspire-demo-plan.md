# Aspire Demo — Implementation Plan

**Author:** Deckard  
**Date:** 2026-04-30  
**Status:** Complete — merged to mainline 2026-06-05 (`samples/AspireDemo`, wired into `AgentMemory.slnx`). Both projects build against current src and the DemoApp was verified end-to-end against live Neo4j (schema bootstrap → seed → scripted recall).  
**Branch:** loop/aspire-demo (superseded; demo now on main line)  

---

## Overview

The Aspire Demo makes Agent Memory for .NET tangible to evaluating developers. Rather than reading API docs alone, a developer can clone the repo, run a single command, and see a live memory graph in Neo4j Browser — complete with seeded episodic, semantic, and decayed memories from three fictional agents.

The demo wires a `.NET Aspire AppHost` to a Neo4j 5 container (ports 7474 and 7687), seeds it with realistic agent data, and runs a console `DemoApp` that exercises the full stack: store, retrieve (hybrid), assemble context, decay. Two modes keep the demo versatile: `--scripted` (default) is deterministic and CI-friendly; `--interactive` opens a REPL for exploratory use.

This is not a production setup. It is a developer onboarding artefact. Security corners are cut deliberately (hardcoded password, community edition) to keep the setup minimal and Docker-friendly.

---

## Prerequisites

- Docker Desktop running (for the Neo4j container)
- .NET 9 SDK
- .NET Aspire workload: `dotnet workload install aspire`
- (Optional) Neo4j Browser: opens automatically at `http://localhost:7474`

---

## Solution Structure

`samples/samples.sln` is a **separate solution** — it is **NOT added to `agent-memory.sln`**.

**Rationale:** Samples are not shipping packages. Keeping them in a separate solution ensures:
- `dotnet test` on the main solution never picks up demo code.
- CI for the library stays fast and noise-free.
- NuGet publish workflow never accidentally packages demo assemblies.
- Developers can open `samples.sln` independently without loading the full library.

```
samples/
  samples.sln
  AspireDemo/
    AspireDemo.AppHost/           -- Aspire orchestrator (entry point: dotnet run)
    AspireDemo.ServiceDefaults/   -- shared Aspire telemetry/health defaults
    AspireDemo.DemoApp/           -- console agent demo client
```

Existing samples (`AgentMemory.Sample.BlendedAgent`, `AgentMemory.Sample.McpHost`, `AgentMemory.Sample.MinimalAgent`) are **not touched** and are not added to `samples.sln`.

---

## Implementation Steps

### Step 1 — Aspire solution scaffold

Roy runs these commands from the repo root:

```powershell
# Create directory structure
mkdir samples\AspireDemo
cd samples\AspireDemo

# Create the three projects
dotnet new aspire-apphost  -n AspireDemo.AppHost        -o AspireDemo.AppHost
dotnet new aspire-servicedefaults -n AspireDemo.ServiceDefaults -o AspireDemo.ServiceDefaults
dotnet new console         -n AspireDemo.DemoApp         -o AspireDemo.DemoApp

# Create solution and add projects
cd ..\..
dotnet new sln -n samples -o samples
dotnet sln samples\samples.sln add samples\AspireDemo\AspireDemo.AppHost\AspireDemo.AppHost.csproj
dotnet sln samples\samples.sln add samples\AspireDemo\AspireDemo.ServiceDefaults\AspireDemo.ServiceDefaults.csproj
dotnet sln samples\samples.sln add samples\AspireDemo\AspireDemo.DemoApp\AspireDemo.DemoApp.csproj
```

Add project references:

```powershell
# AppHost references ServiceDefaults
dotnet add samples\AspireDemo\AspireDemo.AppHost\AspireDemo.AppHost.csproj reference `
  samples\AspireDemo\AspireDemo.ServiceDefaults\AspireDemo.ServiceDefaults.csproj

# DemoApp references ServiceDefaults
dotnet add samples\AspireDemo\AspireDemo.DemoApp\AspireDemo.DemoApp.csproj reference `
  samples\AspireDemo\AspireDemo.ServiceDefaults\AspireDemo.ServiceDefaults.csproj
```

Add AgentMemory project references to DemoApp (local, not NuGet):

```powershell
dotnet add samples\AspireDemo\AspireDemo.DemoApp\AspireDemo.DemoApp.csproj reference `
  src\AgentMemory.Abstractions\AgentMemory.Abstractions.csproj
dotnet add samples\AspireDemo\AspireDemo.DemoApp\AspireDemo.DemoApp.csproj reference `
  src\AgentMemory.Core\AgentMemory.Core.csproj
dotnet add samples\AspireDemo\AspireDemo.DemoApp\AspireDemo.DemoApp.csproj reference `
  src\AgentMemory.Neo4j\AgentMemory.Neo4j.csproj
dotnet add samples\AspireDemo\AspireDemo.DemoApp\AspireDemo.DemoApp.csproj reference `
  src\AgentMemory\AgentMemory.csproj
```

---

### Step 2 — AppHost: Neo4j resource configuration

`samples/AspireDemo/AspireDemo.AppHost/Program.cs`:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var neo4j = builder
    .AddContainer("neo4j", "neo4j", "5")
    .WithHttpEndpoint(port: 7474, targetPort: 7474, name: "browser")
    .WithEndpoint(port: 7687, targetPort: 7687, name: "bolt")
    .WithEnvironment("NEO4J_AUTH", "neo4j/password")
    .WithEnvironment("NEO4J_PLUGINS", "[]")   // no APOC/GDS — community edition
    .WithLifetime(ContainerLifetime.Persistent);

var neo4jBoltUrl = ReferenceExpression.Create(
    $"bolt://localhost:7687");

builder.AddProject<Projects.AspireDemo_DemoApp>("demoapp")
       .WithEnvironment("NEO4J_BOLT_URL", neo4jBoltUrl)
       .WaitFor(neo4j);

builder.Build().Run();
```

> **Note:** Aspire does not ship a first-class Neo4j integration package. We wire the container manually with `AddContainer` and pass the connection string as an environment variable. This is the correct pattern for community-edition Neo4j with no Aspire component library available.

> **Security note (demo only):** `NEO4J_AUTH=neo4j/password` is acceptable for a local demo container. Never use this in production.

---

### Step 3 — ServiceDefaults

`samples/AspireDemo/AspireDemo.ServiceDefaults/Extensions.cs` — standard generated Aspire defaults. Keep the generated code as-is from the template. It wires:
- OpenTelemetry traces + metrics
- Health check endpoints (`/health`, `/alive`)
- Service discovery

No custom code needed here — the template output is correct.

---

### Step 4 — DemoApp: DI wiring

`samples/AspireDemo/AspireDemo.DemoApp/Program.cs`:

```csharp
using AgentMemory.Extensions;                 // AddAgentMemory()
using AspireDemo.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

// Aspire service defaults (telemetry, health)
builder.AddServiceDefaults();

// Read bolt URL injected by AppHost
var boltUrl = builder.Configuration["NEO4J_BOLT_URL"]
    ?? "bolt://localhost:7687";

// Wire AgentMemory stack
builder.Services.AddAgentMemory(options =>
{
    options.AddNeo4jStorage(boltUrl, "neo4j", "password");
    options.AddLlmExtraction();          // uses Azure OpenAI or OPENAI_API_KEY env var
    options.BootstrapSchema = true;      // idempotent schema init on startup
});

// Register demo services
builder.Services.AddSingleton<DemoDataSeeder>();
builder.Services.AddSingleton<ScriptedDemo>();
builder.Services.AddSingleton<InteractiveDemo>();

var app = builder.Build();

// Seed database
await app.Services.GetRequiredService<DemoDataSeeder>().SeedAsync();

// Choose mode
var mode = args.Contains("--interactive") ? "interactive" : "scripted";
if (mode == "interactive")
    await app.Services.GetRequiredService<InteractiveDemo>().RunAsync();
else
    await app.Services.GetRequiredService<ScriptedDemo>().RunAsync();
```

---

### Step 5 — Database seeding

`samples/AspireDemo/AspireDemo.DemoApp/DemoDataSeeder.cs`:

```csharp
using AgentMemory.Abstractions;
using Microsoft.Extensions.Logging;

public sealed class DemoDataSeeder(
    IMemoryStore store,
    ILogger<DemoDataSeeder> logger)
{
    public async Task SeedAsync(CancellationToken ct = default)
    {
        // Idempotency check — skip if memories already exist
        var existingCount = await store.CountMemoriesAsync(ct);
        if (existingCount > 0)
        {
            logger.LogInformation("Database already seeded ({Count} memories). Skipping.", existingCount);
            return;
        }

        logger.LogInformation("Seeding demo data...");

        // --- Agent: Hal (assistant) — 5 episodic memories about a software project ---
        var halSession = "hal-session-001";
        await store.StoreMemoryAsync(new MemoryRecord
        {
            AgentId    = "hal",
            SessionId  = halSession,
            Type       = MemoryType.Episodic,
            Content    = "User asked me to review the authentication module. I identified three issues: missing rate limiting, no refresh token rotation, and JWT secrets stored in plain config.",
            CreatedAt  = DateTimeOffset.UtcNow.AddDays(-10)
        }, ct);
        await store.StoreMemoryAsync(new MemoryRecord
        {
            AgentId    = "hal",
            SessionId  = halSession,
            Type       = MemoryType.Episodic,
            Content    = "Proposed fix: use Azure Key Vault for secrets, add sliding window rate limiter, implement PKCE flow for refresh tokens.",
            CreatedAt  = DateTimeOffset.UtcNow.AddDays(-9)
        }, ct);
        await store.StoreMemoryAsync(new MemoryRecord
        {
            AgentId    = "hal",
            SessionId  = halSession,
            Type       = MemoryType.Episodic,
            Content    = "User accepted the Key Vault proposal. Declined PKCE — their client app cannot support it. Compromised on opaque token rotation.",
            CreatedAt  = DateTimeOffset.UtcNow.AddDays(-8)
        }, ct);
        await store.StoreMemoryAsync(new MemoryRecord
        {
            AgentId    = "hal",
            SessionId  = halSession,
            Type       = MemoryType.Episodic,
            Content    = "Reviewed pull request #42 — rate limiter implementation. Left comments on thread-safety of the sliding window dictionary.",
            CreatedAt  = DateTimeOffset.UtcNow.AddDays(-5)
        }, ct);
        await store.StoreMemoryAsync(new MemoryRecord
        {
            AgentId    = "hal",
            SessionId  = halSession,
            Type       = MemoryType.Episodic,
            Content    = "PR #42 merged after addressing thread-safety. Authentication module now considered stable for v1.",
            CreatedAt  = DateTimeOffset.UtcNow.AddDays(-3)
        }, ct);

        // --- Agent: Sam (analyst) — 3 semantic memories about architecture patterns ---
        var samSession = "sam-session-001";
        await store.StoreMemoryAsync(new MemoryRecord
        {
            AgentId    = "sam",
            SessionId  = samSession,
            Type       = MemoryType.Semantic,
            Content    = "Ports-and-adapters (hexagonal) architecture: core domain has no outbound dependencies. Adapters implement ports. Dependency direction always inward.",
            CreatedAt  = DateTimeOffset.UtcNow.AddDays(-20)
        }, ct);
        await store.StoreMemoryAsync(new MemoryRecord
        {
            AgentId    = "sam",
            SessionId  = samSession,
            Type       = MemoryType.Semantic,
            Content    = "Clean architecture ring model: Entities → Use Cases → Interface Adapters → Frameworks. Inner rings must not reference outer rings.",
            CreatedAt  = DateTimeOffset.UtcNow.AddDays(-18)
        }, ct);
        await store.StoreMemoryAsync(new MemoryRecord
        {
            AgentId    = "sam",
            SessionId  = samSession,
            Type       = MemoryType.Semantic,
            Content    = "GraphRAG pattern: augment LLM retrieval with entity-relationship traversal from a knowledge graph. Blended retrieval combines vector similarity with graph neighbourhood expansion.",
            CreatedAt  = DateTimeOffset.UtcNow.AddDays(-15)
        }, ct);

        // --- Agent: Eve (researcher) — 4 memories with past decay timestamps ---
        var eveSession = "eve-session-001";
        var decayBase = DateTimeOffset.UtcNow.AddDays(-60);
        await store.StoreMemoryAsync(new MemoryRecord
        {
            AgentId    = "eve",
            SessionId  = eveSession,
            Type       = MemoryType.Episodic,
            Content    = "Researched Neo4j vector index performance. HNSW index at M=16, efConstruction=100 gives good recall/speed trade-off for <10M vectors.",
            CreatedAt  = decayBase.AddDays(-4),
            ExpiresAt  = decayBase.AddDays(-1)   // already expired — good decay demo
        }, ct);
        await store.StoreMemoryAsync(new MemoryRecord
        {
            AgentId    = "eve",
            SessionId  = eveSession,
            Type       = MemoryType.Episodic,
            Content    = "Compared BM25 fulltext search vs embedding cosine similarity for code retrieval. Hybrid (BM25 + vector) outperforms either alone by ~12% MRR on the test corpus.",
            CreatedAt  = decayBase.AddDays(-3),
            ExpiresAt  = decayBase.AddDays(-1)
        }, ct);
        await store.StoreMemoryAsync(new MemoryRecord
        {
            AgentId    = "eve",
            SessionId  = eveSession,
            Type       = MemoryType.Episodic,
            Content    = "Evaluated chunking strategies. Recursive character splitter with 512-token chunks + 64-token overlap gives best context coherence for API documentation.",
            CreatedAt  = decayBase.AddDays(-2),
            ExpiresAt  = decayBase        // expired exactly at decayBase
        }, ct);
        await store.StoreMemoryAsync(new MemoryRecord
        {
            AgentId    = "eve",
            SessionId  = eveSession,
            Type       = MemoryType.Semantic,
            Content    = "Embedding models: text-embedding-3-small (1536d) is sufficient for most agent memory tasks. text-embedding-3-large adds marginal benefit at 3× cost.",
            CreatedAt  = decayBase.AddDays(-1),
            ExpiresAt  = null   // no expiry — survives decay pass
        }, ct);

        logger.LogInformation("Seeding complete. {Count} memories created.", 12);
    }
}
```

**Idempotency guarantee:** `CountMemoriesAsync` maps to `MATCH (m:Memory) RETURN count(m)`. If >0, seeding is skipped entirely. Safe to restart DemoApp without duplicate data.

---

### Step 6 — Demo scenarios

#### ScriptedDemo.cs

```csharp
using AgentMemory.Abstractions;
using Microsoft.Extensions.Logging;

public sealed class ScriptedDemo(
    IMemoryStore store,
    IMemoryRetriever retriever,
    IMemoryContextAssembler assembler,
    IMemoryDecayService decay,
    ILogger<ScriptedDemo> logger)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        Console.WriteLine("=== Agent Memory for .NET — Scripted Demo ===\n");

        // 1. Graph stats
        var count = await store.CountMemoriesAsync(ct);
        Console.WriteLine($"[1] Initial graph: {count} memories across 3 agents (Hal, Sam, Eve)\n");

        // 2. Store a new memory for Hal
        var newMemory = new MemoryRecord
        {
            AgentId   = "hal",
            SessionId = "hal-session-002",
            Type      = MemoryType.Episodic,
            Content   = "User requested a code review checklist for security. Generated 8-point checklist covering OWASP Top 10 basics.",
            CreatedAt = DateTimeOffset.UtcNow
        };
        await store.StoreMemoryAsync(newMemory, ct);
        Console.WriteLine($"[2] Stored new memory for agent 'hal': \"{newMemory.Content[..60]}...\"\n");

        // 3. Hybrid retrieval
        var results = await retriever.RetrieveAsync(new RetrievalQuery
        {
            AgentId = "sam",
            Text    = "software architecture clean design patterns",
            TopK    = 3
        }, ct);
        Console.WriteLine($"[3] Retrieved {results.Count} memories for 'Sam' relevant to \"software architecture\":");
        foreach (var r in results)
            Console.WriteLine($"    • [{r.Score:F3}] {r.Content[..Math.Min(80, r.Content.Length)]}...");
        Console.WriteLine();

        // 4. Assemble memory context for a prompt
        var context = await assembler.AssembleAsync(new ContextAssemblyRequest
        {
            AgentId  = "sam",
            MaxTokens = 1024
        }, ct);
        Console.WriteLine($"[4] Assembled memory context ({context.EstimatedTokens} tokens):");
        Console.WriteLine($"    {context.Text[..Math.Min(200, context.Text.Length)]}...\n");

        // 5. Decay pass
        var beforeDecay = await store.CountMemoriesAsync(ct);
        var decayed = await decay.RunDecayPassAsync(ct);
        var afterDecay = await store.CountMemoriesAsync(ct);
        Console.WriteLine($"[5] Decay pass: {decayed} expired memories removed. Graph: {beforeDecay} → {afterDecay} memories\n");

        Console.WriteLine("=== Demo complete ===");
        Console.WriteLine("Browse the memory graph at http://localhost:7474 (neo4j / password)");
        Console.WriteLine("Aspire dashboard:      http://localhost:15888");
    }
}
```

#### InteractiveDemo.cs

```csharp
using AgentMemory.Abstractions;

public sealed class InteractiveDemo(
    IMemoryStore store,
    IMemoryRetriever retriever)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        Console.WriteLine("=== Agent Memory REPL ===");
        Console.WriteLine("Type a message to store + retrieve memories. Type 'exit' to quit.\n");

        var session = $"interactive-{Guid.NewGuid():N}";
        var turn = 0;

        while (!ct.IsCancellationRequested)
        {
            Console.Write($"[{++turn}] You: ");
            var input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input) || input.Trim().ToLower() == "exit")
                break;

            // Store what the user said
            await store.StoreMemoryAsync(new MemoryRecord
            {
                AgentId   = "demo-user",
                SessionId = session,
                Type      = MemoryType.Episodic,
                Content   = input,
                CreatedAt = DateTimeOffset.UtcNow
            }, ct);

            // Retrieve related memories
            var results = await retriever.RetrieveAsync(new RetrievalQuery
            {
                AgentId = "demo-user",
                Text    = input,
                TopK    = 3
            }, ct);

            Console.WriteLine($"Agent remembers ({results.Count} relevant memories):");
            if (results.Count == 0)
                Console.WriteLine("  (nothing yet — keep talking)");
            foreach (var r in results)
                Console.WriteLine($"  • [{r.Score:F3}] {r.Content[..Math.Min(100, r.Content.Length)]}");
            Console.WriteLine();
        }

        Console.WriteLine("Bye. Browse the graph at http://localhost:7474");
    }
}
```

---

### Step 7 — Run instructions

```powershell
# From repo root
cd samples\AspireDemo\AspireDemo.AppHost
dotnet run
```

Aspire starts both the Neo4j container and the DemoApp. Observe:

| URL | What |
|-----|------|
| `http://localhost:15888` | Aspire dashboard — logs, traces, resource status |
| `http://localhost:7474`  | Neo4j Browser — connect with bolt://localhost:7687, user: neo4j, password: password |
| Console / Aspire logs    | DemoApp scripted output |

To run interactive mode instead, set launch args in `AppHost` or run DemoApp directly:

```powershell
cd samples\AspireDemo\AspireDemo.DemoApp
dotnet run -- --interactive
```

> **First run:** Neo4j container starts cold — allow ~15 seconds before the DemoApp bolt connection succeeds. Aspire's `WaitFor(neo4j)` handles readiness automatically.

---

## Review Gates (for Roy)

Before marking implementation done (60%), all of the following must be green:

- [ ] `dotnet build samples\samples.sln` compiles with **0 errors, 0 warnings**
- [ ] `dotnet run` from `AspireDemo.AppHost` starts Aspire dashboard + Neo4j container + DemoApp console
- [ ] Neo4j Browser accessible at `http://localhost:7474` with seed data visible (12 nodes in `:Memory` label)
- [ ] Scripted demo prints all 6 steps and exits cleanly (`exit code 0`)
- [ ] `--interactive` mode accepts input, stores memory, retrieves memories, and exits on `exit`
- [ ] **No modifications** to `agent-memory.sln` or any project under `src/`
- [ ] **No test regressions:** `dotnet test` on the main solution still passes
- [ ] `samples.sln` does **not** appear in `agent-memory.sln`

---

## Out of Scope

- Authentication / production security (hardcoded password is intentional for demo)
- Cloud hosting or remote Neo4j (local Docker only)
- APOC or GDS plugins (community edition, no plugins, `NEO4J_PLUGINS=[]`)
- Full SK/MAF chat loop (demo shows the memory system, not a conversational agent)
- Published NuGet packages — DemoApp references **local project refs only**
- Adding existing samples (`BlendedAgent`, `McpHost`, `MinimalAgent`) to `samples.sln`
- Any changes to `.github/` CI workflows for the main solution

---

## Revision 2 — live repo and environment corrections

**Recorded:** 2026-04-30T23:44:48.278+02:00
**Status:** Supersedes any conflicting instruction above

### 1. Overview

Use a **smaller first cut**: an Aspire AppHost plus one console DemoApp, seeded directly against the live `AgentMemory.*` APIs. On this machine `dotnet new list aspire` returns no templates, so the executable runbook is **manual scaffolding**, not workload-driven template generation.

The goal of Task 3 is now: bring up Neo4j through Aspire, bootstrap schema, seed deterministic data, and print a scripted recall/context demo. Do **not** expand scope to LLM extraction, GraphRAG, Semantic Kernel, or a REPL in this pass.

### 2. Prerequisites

- Docker Desktop running
- .NET 9 SDK
- NuGet restore access for Aspire packages / SDK resolution
- Expect `dotnet new list aspire` to return **no templates** on this machine; treat that as a signal to follow the manual scaffold below, not as a blocker

Do **not** make `dotnet workload install aspire` part of the runbook. That step is stale for this branch and unnecessary for the minimal executable path.

### 3. Solution structure

Create only these new artifacts:

```text
samples\
  samples.sln
  AspireDemo\
    AspireDemo.AppHost\
      AspireDemo.AppHost.csproj
      Program.cs
      appsettings.json
      appsettings.Development.json
      aspire.config.json
    AspireDemo.DemoApp\
      AspireDemo.DemoApp.csproj
      Program.cs
      DemoDataSeeder.cs
      ScriptedDemo.cs
      appsettings.json
```

**Revision 2 decision:** do **not** create `AspireDemo.ServiceDefaults` in the first implementation. Without installed Aspire templates it adds extra manual package/version churn and does not change the core demo outcome for a console app.

### 4. Implementation steps

1. **Create the solution and DemoApp with standard SDK tooling only**

   ```powershell
   dotnet new sln -n samples -o samples
   dotnet new console -n AspireDemo.DemoApp -o samples\AspireDemo\AspireDemo.DemoApp
   ```

2. **Create `samples\AspireDemo\AspireDemo.AppHost\AspireDemo.AppHost.csproj` manually** with:
   - `Sdk="Aspire.AppHost.Sdk/9.5.2"`
   - `TargetFramework` = `net9.0`
   - a `ProjectReference` to `..\AspireDemo.DemoApp\AspireDemo.DemoApp.csproj`

   Manual scaffolding is explicit because the local machine has no Aspire templates. `9.5.2` is the latest stable 9.x AppHost SDK and matches the repo's .NET 9 target better than jumping to 13.x.

3. **Update `samples\AspireDemo\AspireDemo.DemoApp\AspireDemo.DemoApp.csproj` manually**:
   - keep `Sdk="Microsoft.NET.Sdk"`
   - target `net9.0`
   - add `PackageReference Include="Microsoft.Extensions.Hosting" Version="10.0.5"`
   - add `ProjectReference` entries to:
     - `..\..\..\src\AgentMemory.Core\AgentMemory.Core.csproj`
     - `..\..\..\src\AgentMemory.Neo4j\AgentMemory.Neo4j.csproj`

   Do **not** reference `src\AgentMemory\AgentMemory.csproj` in this demo. The live convenience method there is `AddNeo4jAgentMemory(Action<MemoryOptions>, Action<Neo4jOptions>, ...)`, which is not the smallest working path for this sample.

4. **Add both projects to `samples\samples.sln`**:

   ```powershell
   dotnet sln samples\samples.sln add samples\AspireDemo\AspireDemo.AppHost\AspireDemo.AppHost.csproj
   dotnet sln samples\samples.sln add samples\AspireDemo\AspireDemo.DemoApp\AspireDemo.DemoApp.csproj
   ```

5. **Implement `samples\AspireDemo\AspireDemo.AppHost\Program.cs` manually** using `DistributedApplication.CreateBuilder(args)` and a raw Neo4j container resource. Pass connection settings into DemoApp through environment variables:
   - `Neo4j__Uri=bolt://localhost:7687`
   - `Neo4j__Username=neo4j`
   - `Neo4j__Password=password`

   Keep the browser endpoint pinned to `http://localhost:7474`. Do **not** hard-code an Aspire dashboard port; consume the URL that AppHost prints at runtime.

6. **Implement `samples\AspireDemo\AspireDemo.DemoApp\Program.cs` by following the live wiring pattern from `samples\AgentMemory.Sample.MinimalAgent\Program.cs`**:
   - `using AgentMemory.Core;`
   - `using AgentMemory.Core.Stubs;`
   - `using AgentMemory.Neo4j.Infrastructure;`
   - `builder.Services.AddNeo4jAgentMemory(options => { options.Uri = ...; options.Username = ...; options.Password = ...; });`
   - `builder.Services.AddAgentMemoryCore(_ => { });`
   - `builder.Services.AddSingleton<IClock, SystemClock>();`
   - `builder.Services.AddSingleton<IIdGenerator, GuidIdGenerator>();`
   - `builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>, StubEmbeddingGenerator>();`

   Then resolve and run:
   - `ISchemaBootstrapper.BootstrapAsync()`
   - `DemoDataSeeder.SeedAsync()`
   - `ScriptedDemo.RunAsync()`

   **Do not use stale names from Revision 1** such as `AddAgentMemory()`, `IMemoryStore`, `MemoryRecord`, `RetrievalQuery`, `ContextAssemblyRequest`, or `RunDecayPassAsync()` — those do not match the live codebase.

7. **Implement `DemoDataSeeder.cs` against live services, not fictional abstractions**:
   - use `IShortTermMemoryService` to create a fixed conversation and add deterministic messages
   - use `ILongTermMemoryService` to upsert deterministic `Entity`, `Fact`, `Preference`, and `Relationship` records with fixed IDs
   - use one fixed session/conversation (`aspire-demo-session`, `aspire-demo-conversation`) so reruns are deterministic
   - clear/reseed only the demo session before inserting

8. **Implement `ScriptedDemo.cs` against live recall/context APIs**:
   - call `IMemoryService.RecallAsync(new RecallRequest { SessionId = "...", Query = "..." })`
   - call `IMemoryContextAssembler.AssembleContextAsync(...)`
   - print counts and a few representative items

   Do **not** promise a visible decay demo in Task 3. In the live repo `IMemoryDecayService.PruneExpiredMemoriesAsync` is currently a placeholder/no-op and should not be an acceptance gate for this branch.

### 5. Run instructions

Run from the repo root:

```powershell
dotnet restore samples\samples.sln
dotnet build samples\samples.sln
dotnet run --project samples\AspireDemo\AspireDemo.AppHost\AspireDemo.AppHost.csproj
```

Expected runtime signals:

- AppHost prints an Aspire dashboard URL (port may vary — do not hard-code `15888`)
- Neo4j Browser is reachable at `http://localhost:7474`
- DemoApp logs schema bootstrap, seeding, and scripted recall output

Optional direct DemoApp run after Neo4j is already available:

```powershell
dotnet run --project samples\AspireDemo\AspireDemo.DemoApp\AspireDemo.DemoApp.csproj
```

### 6. Review gates

Before claiming Task 3 complete, require all of these:

- [ ] `dotnet restore samples\samples.sln`
- [ ] `dotnet build samples\samples.sln`
- [ ] `dotnet run --project samples\AspireDemo\AspireDemo.AppHost\AspireDemo.AppHost.csproj` brings up Neo4j + DemoApp
- [ ] Neo4j Browser reachable at `http://localhost:7474`
- [ ] DemoApp performs schema bootstrap and deterministic seed on startup
- [ ] DemoApp prints a scripted recall/context result using `IMemoryService` / `IMemoryContextAssembler`
- [ ] `dotnet build AgentMemory.slnx`
- [ ] `dotnet test AgentMemory.slnx --filter "FullyQualifiedName!~AgentMemory.Tests.Integration"` passes even if Docker-backed integration tests are unavailable
- [ ] `dotnet test AgentMemory.slnx` passes when Docker is available
- [ ] No modifications to `AgentMemory.slnx`, `src\`, or existing sample projects

### 7. Out of scope

Revision 2 explicitly keeps these out of Task 3:

- `AspireDemo.ServiceDefaults`
- `AddLlmExtraction()` / any real `IChatClient` wiring
- `src\AgentMemory\ServiceCollectionExtensions.cs` meta-package registration
- GraphRAG setup (`AddGraphRagAdapter`)
- Semantic Kernel / Agent Framework integration in the new demo
- Interactive mode / REPL
- Decay metrics as a pass/fail requirement

### 8. Risks and rollback

**Risks**

- **NuGet restore blocked:** if `Aspire.AppHost.Sdk/9.5.2` cannot restore, the task becomes environment-blocked even though the workload issue is bypassed
- **Docker unavailable:** AppHost build can still succeed, but runtime verification and full integration tests will fail
- **Stale port assumptions:** the Aspire dashboard port is not stable; only Neo4j Browser should be hard-coded
- **Over-scoping:** adding LLM extraction or GraphRAG drags in extra dependencies not needed for the first executable demo

**Rollback**

- Remove only `samples\AspireDemo\` and the corresponding entries from `samples\samples.sln`
- Leave `AgentMemory.slnx`, `src\`, and the existing sample projects untouched
- If restore/runtime is irreducibly blocked, record that blocker under `.squad\decisions\inbox\` instead of inventing a fake non-Aspire substitute

## Revision 3 — AppHost package correction

**Recorded:** 2026-04-30T23:44:48.278+02:00
**Status:** Supplements Revision 2 only where noted

### 1. Mismatch found while coding

Building `samples\AspireDemo\AspireDemo.AppHost\AspireDemo.AppHost.csproj` with only `Sdk="Aspire.AppHost.Sdk/9.5.2"` produced `ASPIRE002`: the AppHost SDK alone is not enough on this machine; the project also needs the `Aspire.Hosting.AppHost` package to participate correctly in restore/build/run.

### 2. Correction

Update Revision 2 Step 4.2 so the manually created AppHost project includes:

- `Sdk="Microsoft.NET.Sdk"` plus `<Sdk Name="Aspire.AppHost.Sdk" Version="9.5.2" />`
- `TargetFramework` = `net9.0`
- `IsAspireHost` = `true`
- `PackageReference Include="Aspire.Hosting.AppHost" Version="9.5.2"`
- a `ProjectReference` to `..\AspireDemo.DemoApp\AspireDemo.DemoApp.csproj`

This is still within the same sample-only scope and does not require any `src\` changes.

## Revision 4 — AppHost launch profile requirement

**Recorded:** 2026-04-30T23:44:48.278+02:00
**Status:** Supplements Revision 2 and Revision 3 only where noted

### 1. Mismatch found while coding

Running `dotnet run --project samples\AspireDemo\AspireDemo.AppHost\AspireDemo.AppHost.csproj` failed before startup because the AppHost dashboard/resource-service endpoints were not populated by any launch profile on this machine. A plain AppHost project with no `Properties\launchSettings.json` is not enough for the exact run command in the review gate.

### 2. Correction

Add `samples\AspireDemo\AspireDemo.AppHost\Properties\launchSettings.json` to the sample. The default profile must populate the AppHost runtime values needed by `dotnet run`, including:

- `applicationUrl` / `ASPNETCORE_URLS`
- `ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL`
- `ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL`
- `ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL`
- `ASPIRE_ALLOW_UNSECURED_TRANSPORT=true`

This keeps the exact `dotnet run --project ...AppHost.csproj` command working without introducing any `src\` changes.

## Revision 5 — no-op GraphRAG registration for Core DI

**Recorded:** 2026-04-30T23:44:48.278+02:00
**Status:** Supplements Revision 2 only where noted

### 1. Mismatch found while coding

With Revision 2's minimal service wiring (`AddNeo4jAgentMemory`, `AddAgentMemoryCore`, stub embeddings, no GraphRAG adapter), `MemoryContextAssembler` still required an `IGraphRagContextSource` registration for DI activation even though `EnableGraphRag` remains false.

### 2. Correction

Keep GraphRAG out of scope, but register a **sample-local no-op `IGraphRagContextSource`** in `AspireDemo.DemoApp` so `IMemoryService` and `IMemoryContextAssembler` can resolve successfully. Do not add `AddGraphRagAdapter()` or any Neo4j vector/fulltext GraphRAG configuration in Task 3.

## Revision 6 — no-op extraction pipeline registration for IMemoryService

**Recorded:** 2026-04-30T23:44:48.278+02:00
**Status:** Supplements Revision 2 only where noted

### 1. Mismatch found while coding

Resolving `IMemoryService` from the Revision 2 minimal sample failed because the default `IMemoryExtractionPipeline` implementation in Core is registered by `AddAgentMemoryCore` but is not activatable from the sample app under this wiring. Task 3 does not call extraction, but `IMemoryService` still depends on the pipeline during construction.

### 2. Correction

Register a **sample-local no-op `IMemoryExtractionPipeline`** in `AspireDemo.DemoApp` so the live `IMemoryService` can be resolved for recall-only scripted scenarios. Keep LLM extraction and `AddLlmExtraction()` out of scope for Task 3.

## Revision 7 — fixed host port exposure for Neo4j endpoints

**Recorded:** 2026-05-01T00:49:46.110+02:00
**Status:** Supplements Revision 2 only where noted

### 1. Mismatch found during runtime verification

With the current AppHost wiring, Neo4j starts behind Aspire's default proxy behavior, so Docker publishes random host ports instead of the planned fixed `localhost:7474` / `localhost:7687` endpoints. That makes Revision 2 Step 5's hard-coded DemoApp connection string incorrect at runtime and explains the observed `SocketException (10061)` against `bolt://localhost:7687`.

### 2. Correction

Update the AppHost container endpoint configuration so the Neo4j browser and bolt endpoints are created with **`isProxied: false`**. This preserves the plan's fixed host ports and keeps the sample-local `Neo4j__Uri=bolt://localhost:7687` contract valid without expanding scope beyond the sample projects.
