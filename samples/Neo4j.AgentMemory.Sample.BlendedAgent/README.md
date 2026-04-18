# Neo4j Agent Memory — Blended Memory + GraphRAG Sample

A console application that demonstrates combining **persistent agent memory** with **GraphRAG retrieval** and **OpenTelemetry observability**, all wired through a single DI container.

---

## What this sample demonstrates

| Step | API | Purpose |
|------|-----|---------|
| A | `IMemoryService.RecallAsync` (MemoryOnly) | Recall from short/long-term memory only |
| B | `IGraphRagContextSource.GetContextAsync` | Query the Neo4j knowledge graph directly via GraphRAG |
| C | `Neo4jMicrosoftMemoryFacade.GetContextForRunAsync` | Blended pre-run context (memory + GraphRAG) |
| 1 | *(your agent)* | Simulated agent turn |
| 2 | `Neo4jMicrosoftMemoryFacade.PersistAfterRunAsync` | Post-run memory persistence |
| 3 | `MemoryToolFactory.CreateTools` | Six memory tools for function calling |
| 4 | `AgentTraceRecorder` | Reasoning trace with blended context observation |
| OTel | `ActivityListener` | Console span output showing memory + GraphRAG operations |

---

## Prerequisites

| Dependency | Version |
|------------|---------|
| .NET SDK   | 9.0+    |
| Neo4j      | 5.11+ *(optional — sample degrades gracefully without it)* |

### GraphRAG index setup (optional)

The GraphRAG adapter expects a vector index named `knowledge_vectors` (configurable). Create it before running the sample against a live Neo4j instance:

```cypher
// Vector index (1536 dimensions — adjust to match your embedding model)
CREATE VECTOR INDEX knowledge_vectors IF NOT EXISTS
FOR (n:KnowledgeChunk) ON (n.embedding)
OPTIONS {
  indexConfig: {
    `vector.dimensions`: 1536,
    `vector.similarity_function`: 'cosine'
  }
};

// Fulltext index for hybrid search
CREATE FULLTEXT INDEX knowledge_vectors_fulltext IF NOT EXISTS
FOR (n:KnowledgeChunk) ON EACH [n.text];
```

---

## Configuration

Edit `appsettings.json` or set environment variables:

```json
{
  "Neo4j": {
    "Uri":      "bolt://localhost:7687",
    "Username": "neo4j",
    "Password": "your-password-here",
    "GraphRag": {
      "IndexName":         "knowledge_vectors",
      "FulltextIndexName": "knowledge_vectors_fulltext"
    }
  }
}
```

Environment variable equivalents (double-underscore separator):

```bash
Neo4j__Uri=bolt://localhost:7687
Neo4j__Username=neo4j
Neo4j__Password=your-password-here
Neo4j__GraphRag__IndexName=knowledge_vectors
Neo4j__GraphRag__FulltextIndexName=knowledge_vectors_fulltext
```

---

## How to run

```bash
dotnet run --project samples/Neo4j.AgentMemory.Sample.BlendedAgent
```

### Retrieval mode override

The blend mode is configured in `Program.cs` via `RecallOptions.BlendMode`. Switch modes for different scenarios:

| Mode | Enum value | Behaviour |
|------|-----------|-----------|
| Memory only | `RetrievalBlendMode.MemoryOnly` | Recalls from short/long-term memory; GraphRAG skipped |
| GraphRAG only | `RetrievalBlendMode.GraphRagOnly` | Queries only the knowledge-graph vector index |
| Memory then GraphRAG | `RetrievalBlendMode.MemoryThenGraphRag` | Memory first, GraphRAG enriches remaining budget |
| GraphRAG then Memory | `RetrievalBlendMode.GraphRagThenMemory` | GraphRAG first, memory fills remaining budget |
| **Blended** (default) | `RetrievalBlendMode.Blended` | Both sources contribute in parallel; results merged |

---

## Expected output

**Without a running Neo4j instance** (demo/compile-check mode):

```
=== Neo4j Agent Memory — Blended Memory + GraphRAG Sample ===
  [OTel] ▶ memory.recall
  [OTel] ■ memory.recall (0.3 ms)
[A] Memory-only retrieval…
    Recall (MemoryOnly): 0 item(s) retrieved.
[B] GraphRAG-only retrieval…
    GraphRAG skipped (no live Neo4j): <connection error>
[C] Blended pre-run context assembly…
    Retrieved 0 prior message(s) in blended mode.
[1] Agent produced 2 new message(s).
[2] Persisting messages to Neo4j memory…
    Messages persisted.
[3] Available memory tools (6):
    • search_memory        — ...
    ...
[4] Recording a reasoning trace…
    Trace recording skipped (no live Neo4j): <connection error>
=== Demo complete. ===
```

**With a running Neo4j instance**, steps 2 and 4 fully persist, step B returns GraphRAG results, and OTel spans report real durations.

---

## Key DI registration pattern

```csharp
// 1. Neo4j infrastructure (repositories, session factory, schema bootstrapper)
services.AddNeo4jAgentMemory(options => { ... });

// 2. Core memory services (short-term, long-term, reasoning, context assembly)
services.AddAgentMemoryCore(options => { options = options with { EnableGraphRag = true, ... }; });
services.AddSingleton<IClock, SystemClock>();
services.AddSingleton<IIdGenerator, GuidIdGenerator>();
services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>, StubEmbeddingGenerator>(); // swap for real generator

// 3. GraphRAG adapter — BEFORE observability so the decorator wraps it
services.AddGraphRagAdapter(options =>
{
    options.IndexName   = "knowledge_vectors";
    options.SearchMode  = GraphRagSearchMode.Hybrid;
});

// 4. MAF adapter (also registers Neo4jChatHistoryProvider for ChatClientAgentOptions)
services.AddAgentMemoryFramework(options => { ... });

// 5. Observability — LAST; decorates IMemoryService + IGraphRagContextSource
services.AddAgentMemoryObservability();

// 6. Wire MAF-compatible AI functions into your agent's tool list:
//    var tools = toolFactory.CreateAIFunctions();
//    var agent = chatClient.AsAIAgent(new ChatClientAgentOptions { ... }, tools: [..tools]);
```

> **Note:** `AddAgentMemoryObservability()` must be called **after** the services it decorates are registered.  
> Replace `StubEmbeddingGenerator` with a real `IEmbeddingGenerator<string, Embedding<float>>` (e.g. OpenAI `text-embedding-3-small`) before using semantic search.
