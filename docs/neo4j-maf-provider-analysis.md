# neo4j-maf-provider Reuse Strategy

**Last Updated:** 2025-07-24 (All Phases Complete)  
**Author:** Deckard (Lead Architect)  
**Source Code:** `Neo4j/neo4j-maf-provider/dotnet/src/Neo4j.AgentFramework.GraphRAG/`

---

## 1. Code Inventory

Complete file-by-file analysis of the existing `Neo4j.AgentFramework.GraphRAG` package.

| # | File | MAF-Specific? | Reusable? | Notes |
|---|---|---|---|---|
| 1 | `Neo4jContextProvider.cs` | **Yes** — extends `AIContextProvider` from `Microsoft.Agents.AI` | ❌ Not directly | MAF lifecycle integration (`ProvideAIContextAsync`, `InvokingContext`). Core pattern of concatenating messages → searching → formatting results is useful as a reference. |
| 2 | `Neo4jContextProviderOptions.cs` | **Partially** — `EmbeddingGenerator` uses `IEmbeddingGenerator<string, Embedding<float>>` from M.E.AI | Patterns only | Configuration model (IndexName, TopK, RetrievalQuery, MessageHistoryCount) informs our `RecallOptions` design. |
| 3 | `Neo4jSettings.cs` | No — reads env vars | ✅ Pattern | Environment-variable-based config pattern. Our `Neo4jOptions` serves the same purpose via `IOptions<T>`. |
| 4 | `IndexType.cs` | No — pure enum (`Vector`, `Fulltext`, `Hybrid`) | ✅ Pattern | Maps to our `RetrievalBlendMode` and `GraphRagSearchMode` enums. |
| 5 | `StopWords.cs` | No — pure C# utility | ✅ Directly reusable | Stop-word filtering for fulltext queries. Can be replicated in our Neo4j package if/when fulltext search is added to repositories. |
| 6 | `Retrieval/IRetriever.cs` | **No** — `Task<RetrieverResult> SearchAsync(string, int, CancellationToken)` | ✅ Key reuse target | Clean interface. Our `IGraphRagContextSource` serves an equivalent purpose at a higher abstraction level. |
| 7 | `Retrieval/VectorRetriever.cs` | **Partially** — depends on `IEmbeddingGenerator<string, Embedding<float>>` from M.E.AI, but core Cypher is pure Neo4j.Driver | ✅ Cypher patterns | `db.index.vector.queryNodes` usage, parameterized queries, `RoutingControl.Readers`, optional retrieval_query pattern. |
| 8 | `Retrieval/FulltextRetriever.cs` | **No** — depends only on Neo4j.Driver | ✅ Directly adaptable | `db.index.fulltext.queryNodes` usage, stop-word filtering, standard/enriched result formatting. |
| 9 | `Retrieval/HybridRetriever.cs` | **No** — composes Vector + Fulltext | ✅ Pattern reusable | Concurrent `Task.WhenAll` execution, content-key merge with max-score, descending sort, topK limit. |
| 10 | `Retrieval/RetrieverResult.cs` | **No** — simple record type | ✅ Pattern | Record with `IReadOnlyList<RetrieverResultItem>`. Our domain types serve the same purpose with richer typing. |

---

## 2. Cypher Patterns to Adapt

These are the specific Cypher query patterns we adapt into our typed repositories. The patterns are production-quality and tested against real Neo4j instances.

### 2.1 Vector Search Pattern

**Source:** `VectorRetriever.cs`

```cypher
-- Standard vector search (no graph enrichment)
CALL db.index.vector.queryNodes($index, $k, $embedding)
YIELD node, score
WITH node, score
ORDER BY score DESC
LIMIT $k
RETURN node, score

-- With graph enrichment (retrieval_query)
CALL db.index.vector.queryNodes($index, $k, $embedding)
YIELD node, score
WITH node, score
ORDER BY score DESC
LIMIT $k
{retrieval_query}  -- user-defined Cypher for graph traversal
LIMIT $k
```

**Parameters:**
```csharp
var parameters = new Dictionary<string, object?>
{
    ["index"] = _indexName,
    ["k"] = topK,
    ["embedding"] = embedding.ToArray()  // float[] → object for Neo4j driver
};
```

**Our adaptation:** Each typed repository (Entity, Message, Fact, Preference, ReasoningTrace) will use this pattern with:
- Index names specific to our schema (e.g., `entity_embedding`, `message_embedding`)
- Typed node labels in the RETURN clause
- Property mapping to our domain model fields
- Score returned as part of the `(T, double Score)` tuple

### 2.2 Fulltext Search Pattern

**Source:** `FulltextRetriever.cs`

```cypher
-- Standard fulltext search
CALL db.index.fulltext.queryNodes($index_name, $query)
YIELD node, score
WITH node, score
ORDER BY score DESC
LIMIT $top_k
RETURN node, score

-- With graph enrichment
CALL db.index.fulltext.queryNodes($index_name, $query)
YIELD node, score
WITH node, score
ORDER BY score DESC
LIMIT $top_k
{retrieval_query}
LIMIT $top_k
```

**Key detail — stop-word filtering:**
```csharp
var searchText = _filterStopWords
    ? StopWords.ExtractKeywords(queryText)
    : queryText;

if (string.IsNullOrWhiteSpace(searchText))
    return new RetrieverResult([]);
```

**Our adaptation:** Applied to our fulltext indexes:
- `message_content` index on `Message.content`
- `entity_name` index on `Entity.name` and `Entity.description`
- `fact_content` index on `Fact.subject`, `Fact.predicate`, `Fact.object`

### 2.3 Hybrid Search — Merge Strategy

**Source:** `HybridRetriever.cs`

```csharp
// Run both searches concurrently
var vectorTask = _vectorRetriever.SearchAsync(queryText, topK, cancellationToken);
var fulltextTask = _fulltextRetriever.SearchAsync(queryText, topK, cancellationToken);
await Task.WhenAll(vectorTask, fulltextTask);

// Merge by content key, keep max score
var merged = new Dictionary<string, RetrieverResultItem>();
foreach (var item in vectorResults.Items.Concat(fulltextResults.Items))
{
    var key = item.Content;
    if (merged.TryGetValue(key, out var existing))
    {
        if (GetScore(item) > GetScore(existing))
            merged[key] = item;
    }
    else
    {
        merged[key] = item;
    }
}

// Sort descending by score, limit to topK
var items = merged.Values
    .OrderByDescending(GetScore)
    .Take(topK)
    .ToList();
```

**Our adaptation:** This merge strategy will be used in the `MemoryContextAssembler` when `RetrievalBlendMode.Blended` is configured.

### 2.4 Read Routing Pattern

**Source:** All retrievers

```csharp
var (records, _, _) = await _driver.ExecutableQuery(cypher)
    .WithParameters(parameters)
    .WithConfig(new QueryConfig(routing: RoutingControl.Readers))
    .ExecuteAsync(cancellationToken)
    .ConfigureAwait(false);
```

**Our adaptation:** All read operations in our repositories will use `AccessMode.Read` via the `Neo4jSessionFactory` and `Neo4jTransactionRunner`:
```csharp
await _txRunner.ReadAsync(async tx =>
{
    var result = await tx.RunAsync(cypher, parameters);
    // ...map results to domain types
}, cancellationToken);
```

### 2.5 Parameterized Query Pattern

All retrievers use `Dictionary<string, object?>` for query parameters — **never** string interpolation or concatenation. Our repositories follow the same principle for security and performance.

---

## 3. What We Don't Take

### 3.1 AIContextProvider Base Class

```csharp
// neo4j-maf-provider:
public sealed class Neo4jContextProvider : AIContextProvider, IAsyncDisposable
{
    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
}
```

**Why not:** `AIContextProvider` is from `Microsoft.Agents.AI` — it's MAF-specific. Our core memory engine is framework-agnostic. The MAF adapter (Phase 3) will create its own context provider that delegates to our `IMemoryService`.

### 3.2 RetrieverResult Types

```csharp
// neo4j-maf-provider:
public sealed record RetrieverResult(IReadOnlyList<RetrieverResultItem> Items);
public sealed record RetrieverResultItem(string Content, IReadOnlyDictionary<string, object?>? Metadata);
```

**Why not:** These are untyped (content is a string, metadata is a loose dictionary). We have strongly-typed domain models (`Entity`, `Fact`, `Message`, etc.) with scored tuple returns `(T, double Score)`. Our types carry richer semantics.

### 3.3 IEmbeddingGenerator from M.E.AI

```csharp
// neo4j-maf-provider:
public IEmbeddingGenerator<string, Embedding<float>>? EmbeddingGenerator { get; init; }
```

**Why not:** This is from `Microsoft.Extensions.AI` which would add a framework dependency to our Abstractions. We define our own `IEmbeddingProvider`:
```csharp
// Our interface (in Abstractions — zero deps):
public interface IEmbeddingProvider
{
    Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default);
    int EmbeddingDimensions { get; }
}
```

### 3.4 InvokingContext / MAF Lifecycle

The entire `ProvideAIContextAsync(InvokingContext context)` flow — message filtering, text concatenation, ChatMessage construction — is MAF plumbing. Our context assembly pipeline is its own design with `RecallRequest` → `MemoryContext`.

---

## 4. Integration Plan for Phase 4 (GraphRAG Adapter)

### 4.1 Architecture

The `Neo4j.AgentMemory.GraphRagAdapter` package (Phase 4) will bridge our `IGraphRagContextSource` contract to the existing `IRetriever` interface.

```
                    Our Packages                          Existing Package
┌─────────────────────────────────┐       ┌──────────────────────────────────────┐
│ Neo4j.AgentMemory.Abstractions  │       │ Neo4j.AgentFramework.GraphRAG       │
│                                 │       │                                      │
│ IGraphRagContextSource          │       │ IRetriever                           │
│ GraphRagContextRequest          │       │ VectorRetriever                      │
│ GraphRagContextResult           │       │ FulltextRetriever                    │
│ GraphRagContextItem             │       │ HybridRetriever                      │
│                                 │       │ RetrieverResult                      │
└────────────┬────────────────────┘       └────────────────┬─────────────────────┘
             │                                              │
             │ implements                                   │ delegates to
             │                                              │
┌────────────┴────────────────────────────────────────────┴──┐
│ Neo4j.AgentMemory.GraphRagAdapter                          │
│                                                            │
│ GraphRagContextSourceAdapter : IGraphRagContextSource       │
│   - Receives GraphRagContextRequest from Core               │
│   - Creates IRetriever (Vector/Fulltext/Hybrid)             │
│   - Calls IRetriever.SearchAsync(...)                       │
│   - Maps RetrieverResult → GraphRagContextResult            │
│   - Returns to Core via IGraphRagContextSource contract     │
└────────────────────────────────────────────────────────────┘
```

### 4.2 Implementation Steps

1. Create `Neo4j.AgentMemory.GraphRagAdapter` project
2. Add NuGet reference to `Neo4j.AgentFramework.GraphRAG`
3. Implement `GraphRagContextSourceAdapter : IGraphRagContextSource`
4. Map `GraphRagContextRequest.SearchMode` to `IndexType` enum
5. Map `GraphRagContextRequest.Query` + embedding to retriever `SearchAsync` call
6. Map `RetrieverResult.Items` to `IReadOnlyList<GraphRagContextItem>`
7. Register adapter via DI: `services.AddGraphRagContextSource()`
8. Core's `MemoryContextAssembler` calls `IGraphRagContextSource.GetContextAsync()` when `EnableGraphRag = true`

### 4.3 Blend Modes

The `MemoryContextAssembler` uses `RecallOptions.BlendMode` to control how memory and GraphRAG results combine:

| Mode | Behavior |
|---|---|
| `MemoryOnly` | Skip GraphRAG entirely |
| `GraphRagOnly` | Skip memory search, use only GraphRAG |
| `Blended` | Run both in parallel, merge results (max-score, adapted from HybridRetriever pattern) |
| `MemoryThenGraphRag` | Memory first, GraphRAG fills remaining budget |
| `GraphRagThenMemory` | GraphRAG first, memory fills remaining budget |

---

## 5. MAF Version Gap

### 5.1 Current State

| Aspect | neo4j-maf-provider | Current MAF |
|---|---|---|
| **MAF Version** | 0.3 (pre-GA preview) | **1.1.0** (post-GA, stable) |
| **Base Class** | `AIContextProvider` | Likely renamed or restructured |
| **Entry Point** | `ProvideAIContextAsync(InvokingContext)` | Signature may have changed |
| **Namespace** | `Microsoft.Agents.AI` | May have been reorganized |
| **NuGet Package** | `Microsoft.Agents.AI` (preview) | `Microsoft.Agents.AI` (stable, version ≥ 1.0) |

### 5.2 Implications for This Project

1. **Our Phase 3 MAF adapter** will target MAF 1.1.0 (current stable), not 0.3
2. **The existing neo4j-maf-provider** may need updating to MAF 1.1.0 before our GraphRAG adapter (Phase 4) can reference it as a NuGet dependency
3. **If the existing package is NOT updated**, our GraphRAG adapter may need to:
   - Reference the retriever layer directly (which has no MAF dependency)
   - Or vendor the retriever code (less desirable)
4. **The retriever layer** (`IRetriever`, `VectorRetriever`, `FulltextRetriever`, `HybridRetriever`) has **no MAF dependency** — it depends only on `Neo4j.Driver` and `Microsoft.Extensions.AI`. This layer is stable regardless of MAF version changes.

### 5.3 Risk Mitigation

- The retriever layer is isolated from MAF — it can be referenced independently
- Our `IGraphRagContextSource` contract is defined in Abstractions — the adapter can be rewritten without affecting Core
- If the existing package breaks on MAF 1.1.0, we can extract just the retriever layer into a local project

---

## Appendix: File Paths

| Component | Path in Repository |
|---|---|
| neo4j-maf-provider source | `Neo4j/neo4j-maf-provider/dotnet/src/Neo4j.AgentFramework.GraphRAG/` |
| Our Abstractions | `src/Neo4j.AgentMemory.Abstractions/` |
| Our Core | `src/Neo4j.AgentMemory.Core/` |
| Our Neo4j | `src/Neo4j.AgentMemory.Neo4j/` |
| IGraphRagContextSource | `src/Neo4j.AgentMemory.Abstractions/Services/IGraphRagContextSource.cs` |
| GraphRAG domain types | `src/Neo4j.AgentMemory.Abstractions/Domain/GraphRag/` |
| SchemaBootstrapper | `src/Neo4j.AgentMemory.Neo4j/Infrastructure/SchemaBootstrapper.cs` |
