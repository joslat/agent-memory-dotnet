# Decision Proposal: Unify on MEAI IEmbeddingGenerator

**Proposed by:** Rachael (MAF Integration Engineer)  
**Date:** 2025-07-18  
**Status:** Proposed  
**Scope:** Abstractions, Core, AgentFramework, all consumers of IEmbeddingProvider

## Context

We currently maintain two embedding interfaces:
1. `IEmbeddingProvider` (our Abstractions — `Task<float[]>`) — used in 11 files across Core + AgentFramework
2. `IEmbeddingGenerator<string, Embedding<float>>` (MEAI) — used in GraphRagAdapter + BlendedAgent sample

Consumers must register both, which is confusing and error-prone. MEAI is already the standard in 5 of our 10 packages.

## Proposal

Replace `IEmbeddingProvider` with MEAI's `IEmbeddingGenerator<string, Embedding<float>>` as the single embedding interface.

### Phase A (Additive — v1.x)
- Add `Microsoft.Extensions.AI.Abstractions` to Abstractions.csproj
- Mark `IEmbeddingProvider` as `[Obsolete]`
- Core services accept `IEmbeddingGenerator` with `IEmbeddingProvider` fallback bridge

### Phase B (Clean — v2.0)
- Remove `IEmbeddingProvider` and `StubEmbeddingProvider`
- All services depend on `IEmbeddingGenerator` only

## Impact
- Abstractions gains one lightweight dependency (~100KB)
- 11 files in Core/AgentFramework change constructor signatures
- GraphRagAdapter eliminates its separate embedding registration
- Consumers register ONE embedding interface instead of two

## Rationale
- MEAI is Microsoft's official standard for AI abstractions in .NET
- MAF, Semantic Kernel, and standalone apps all provide `IEmbeddingGenerator` implementations
- Eliminates the split personality that forces double registration
- Opens the door for trivial SK adapter and standalone usage
