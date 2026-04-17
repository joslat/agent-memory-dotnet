# GraphRagAdapter → Neo4j Unified Layer Migration Guide

## Overview

The `Neo4j.AgentMemory.GraphRagAdapter` package has been merged into `Neo4j.AgentMemory.Neo4j`.
All GraphRAG retrieval capabilities now live inside the unified Neo4j infrastructure layer, reducing the package count from 10 to 9 and eliminating a separate Neo4j driver dependency.

## What Changed

| Before | After |
|--------|-------|
| `Neo4j.AgentMemory.GraphRagAdapter` package | Removed — code merged into `Neo4j.AgentMemory.Neo4j` |
| `GraphRagAdapterOptions` class | Renamed to `GraphRagOptions` |
| Namespace `Neo4j.AgentMemory.GraphRagAdapter` | `Neo4j.AgentMemory.Neo4j.Services` / `.Infrastructure` / `.Retrieval` |
| `AdapterVectorRetriever` | `VectorRetriever` (internal, under `Retrieval.Internal`) |
| `AdapterFulltextRetriever` | `FulltextRetriever` (internal) |
| `AdapterHybridRetriever` | `HybridRetriever` (internal) |
| `Neo4j.AgentMemory.GraphRagAdapter.Retrieval.IRetriever` | `Neo4j.AgentMemory.Neo4j.Retrieval.IRetriever` |

## Migration Steps

### 1. Remove the GraphRagAdapter project reference

```xml
<!-- REMOVE this line from your .csproj -->
<ProjectReference Include="..\..\src\Neo4j.AgentMemory.GraphRagAdapter\Neo4j.AgentMemory.GraphRagAdapter.csproj" />
```

You only need a reference to `Neo4j.AgentMemory.Neo4j` (which you likely already have).

### 2. Update `using` statements

```csharp
// BEFORE
using Neo4j.AgentMemory.GraphRagAdapter;

// AFTER — remove the above line entirely.
// The extension method and options are now in Neo4j.Infrastructure (already imported if you use AddNeo4jAgentMemory):
using Neo4j.AgentMemory.Neo4j.Infrastructure;
```

### 3. Rename `GraphRagAdapterOptions` → `GraphRagOptions`

```csharp
// BEFORE
services.AddGraphRagAdapter(options =>
{
    // options is GraphRagAdapterOptions
});

// AFTER — same method name, new options type
services.AddGraphRagAdapter(options =>
{
    // options is now GraphRagOptions (same properties, just renamed)
});
```

All properties (`IndexName`, `SearchMode`, `FulltextIndexName`, `RetrievalQuery`, `TopK`, `FilterStopWords`) remain identical.

### 4. No changes needed for these

- **`IGraphRagContextSource`** — still in `Neo4j.AgentMemory.Abstractions.Services` (unchanged)
- **`GraphRagSearchMode`**, **`GraphRagContextRequest`**, **`GraphRagContextResult`**, **`GraphRagContextItem`** — still in `Neo4j.AgentMemory.Abstractions.Domain` (unchanged)
- **`AddGraphRagAdapter()`** extension method name — unchanged, now on `Neo4j.AgentMemory.Neo4j.Infrastructure.ServiceCollectionExtensions`

### 5. If you reference `IRetriever` directly (rare)

```csharp
// BEFORE
using Neo4j.AgentMemory.GraphRagAdapter.Retrieval;

// AFTER
using Neo4j.AgentMemory.Neo4j.Retrieval;
```

## Why This Merge?

1. **Single Neo4j owner** — one package owns all Neo4j driver access (CRUD, retrieval, schema), simplifying dependency management.
2. **Fewer packages** — eliminates a standalone package that only had ~10 source files and one public entry point.
3. **Shared infrastructure** — retrievers can now reuse the `INeo4jTransactionRunner`, `SchemaBootstrapper`, and other Neo4j infrastructure without cross-package coupling.
4. **Cleaner DI** — consumers call `AddNeo4jAgentMemory()` + `AddGraphRagAdapter()` from the same namespace.
