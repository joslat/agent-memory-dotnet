# Neo4j Agent Memory — Minimal MAF Sample

A minimal console application demonstrating how to wire up **Neo4j Agent Memory** with the **Microsoft Agent Framework (MAF)**.

---

## What this sample demonstrates

| Step | API | Purpose |
|------|-----|---------|
| 1 | `Neo4jMicrosoftMemoryFacade.GetContextForRunAsync` | Fetch prior conversation history before an agent run |
| 2 | *(your MAF agent)* | Placeholder showing where the real agent invocation goes |
| 3 | `Neo4jMicrosoftMemoryFacade.PersistAfterRunAsync` | Persist new messages (+ trigger extraction) after a run |
| 4 | `MemoryToolFactory.CreateTools` | Enumerate the six standard memory tools for function calling |
| 5 | `AgentTraceRecorder` | Capture agent reasoning steps as persistent traces in Neo4j |

---

## Prerequisites

| Dependency | Version |
|------------|---------|
| .NET SDK   | 9.0+    |
| Neo4j      | 5.11+ *(optional — sample degrades gracefully without it)* |

---

## Configuration

Edit `appsettings.json` or set environment variables:

```json
{
  "Neo4j": {
    "Uri":      "bolt://localhost:7687",
    "Username": "neo4j",
    "Password": "your-password-here"
  }
}
```

Environment variable equivalents (double-underscore separator):

```bash
Neo4j__Uri=bolt://localhost:7687
Neo4j__Username=neo4j
Neo4j__Password=your-password-here
```

---

## How to run

```bash
dotnet run --project samples/Neo4j.AgentMemory.Sample.MinimalAgent
```

---

## Expected output

**Without a running Neo4j instance** (demo/compile-check mode):

```
=== Neo4j Agent Memory — Minimal MAF Sample ===
[1] Fetching prior context for session 'demo-session-01'…
    Retrieved 0 prior message(s).
[2] Agent produced 2 new message(s).
[3] Persisting messages to Neo4j memory…
    Messages persisted.
[4] Available memory tools (6):
    • search_memory        — Semantic search across all memory layers (entities, facts, preferences).
    • remember_preference  — Store a user preference.
    • remember_fact        — Store a fact as subject-predicate-object triple.
    • recall_preferences   — Retrieve stored preferences, optionally filtered by category.
    • search_knowledge     — Search entities and relationships in the knowledge graph.
    • find_similar_tasks   — Search reasoning traces for similar past tasks.
[5] Recording a reasoning trace…
    Trace recording skipped (no live Neo4j): <connection error>
=== Demo complete. ===
```

**With a running Neo4j instance**, steps 3 and 5 will fully persist to the graph database and the warning in step 5 will be replaced with `Trace recorded successfully.`

---

## Key DI registration pattern

```csharp
// 1. Neo4j infrastructure (repositories, session factory, schema bootstrapper)
services.AddNeo4jAgentMemory(options => { ... });

// 2. Core memory services (short-term, long-term, reasoning, extraction pipeline)
services.AddAgentMemoryCore(_ => { });
services.AddSingleton<IClock, SystemClock>();
services.AddSingleton<IIdGenerator, GuidIdGenerator>();
services.AddSingleton<IEmbeddingProvider, StubEmbeddingProvider>(); // swap for real provider

// 3. MAF adapter (Neo4jMicrosoftMemoryFacade, Neo4jChatMessageStore, Neo4jMemoryContextProvider)
services.AddAgentMemoryFramework(options => { ... });

// 4. Optional: register additional framework helpers
services.AddScoped<AgentTraceRecorder>();
services.AddScoped<MemoryToolFactory>();
```

> **Note:** Replace `StubEmbeddingProvider` with a real embedding provider (e.g. `OpenAIEmbeddingProvider`) before using semantic search or LLM-driven extraction.
