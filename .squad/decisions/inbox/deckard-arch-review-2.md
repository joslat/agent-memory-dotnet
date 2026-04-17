# Deckard — Architecture Review 2 Decisions

**Date:** July 2026  
**Author:** Deckard (Lead / Solution Architect)  
**Scope:** Architecture re-evaluation, MEAI strategy, killer package vision

---

## D-AR2-1: Adopt MEAI IEmbeddingGenerator<T> as Primary Embedding Contract

**Status:** Proposed  
**Impact:** HIGH  
**Breaking Change:** Yes (IEmbeddingProvider consumers must migrate)

**Decision:** Replace our custom `IEmbeddingProvider` interface in Abstractions with MEAI's `IEmbeddingGenerator<string, Embedding<float>>`. Abstractions gains a dependency on `Microsoft.Extensions.AI.Abstractions` (~50KB).

**Rationale:**
- Core already depends on M.E.AI.Abstractions 10.4.1
- GraphRagAdapter already uses IEmbeddingGenerator<T> — creating a DUAL abstraction
- Every major .NET AI SDK (OpenAI, Azure, Ollama) implements IEmbeddingGenerator<T> natively
- Eliminates all consumer adapter code
- Enables MEAI middleware pipeline (caching, telemetry) on embedding calls
- Makes Semantic Kernel integration trivial (SK uses IEmbeddingGenerator<T>)
- M.E.AI.Abstractions is effectively part of the .NET BCL now

**Migration path:**
1. Add M.E.AI.Abstractions to Abstractions.csproj
2. Replace all IEmbeddingProvider usage with IEmbeddingGenerator<T>
3. Remove IEmbeddingProvider interface
4. Update DI registrations
5. Provide migration guide for external consumers

---

## D-AR2-2: Merge Extraction Packages with Strategy Pattern

**Status:** Proposed (reaffirms D-AR1 from prior review)  
**Impact:** MEDIUM

**Decision:** Create `Neo4j.AgentMemory.Extraction` base package with `IExtractionEngine` strategy interface. Keep `Extraction.Llm` and `Extraction.AzureLanguage` as thin sub-packages with only engine implementation + SDK dependency.

**Rationale:** ~95% structural duplication between the two packages. Strategy pattern enables runtime engine selection and simplifies adding new engines.

---

## D-AR2-3: Publish Neo4j.AgentMemory Meta-Package

**Status:** Proposed (reaffirms D-PKG3)  
**Impact:** HIGH (DX)

**Decision:** Publish `Neo4j.AgentMemory` convenience meta-package containing Abstractions + Core + Neo4j + Extraction.Llm. One-line install for the common use case.

---

## D-AR2-4: Future Semantic Kernel Adapter

**Status:** Proposed  
**Impact:** HIGH (market reach)

**Decision:** After D-AR2-1 (MEAI migration), create `Neo4j.AgentMemory.SemanticKernel` adapter package (~200 LOC). Exposes memory operations as SK kernel functions/plugins. Trivially easy because SK already uses IEmbeddingGenerator<T>.

**Prerequisite:** D-AR2-1 must be implemented first.

---

## D-AR2-5: Fluent DI Builder API

**Status:** Proposed  
**Impact:** MEDIUM (DX)

**Decision:** Create unified `AddNeo4jAgentMemory()` fluent builder that wires all subsystems: Neo4j connection, embedding provider, extraction engine, schema bootstrap, observability. Replace current multi-call DI setup with single entry point.

```csharp
services.AddNeo4jAgentMemory(opts => {
    opts.Neo4j.Uri = "bolt://localhost:7687";
    opts.Embedding.UseOpenAI(apiKey);
    opts.Extraction.UseLlm();
    opts.Observability.Enable();
});
```

---

*All decisions pending Jose's approval. D-AR2-1 (MEAI migration) is the highest-impact change and should be discussed first.*
