# ADR 0005 - MEAI Abstractions for Embeddings and LLMs

Status: Accepted

Date: 2026-07-09

## Context

The .NET AI ecosystem has converged around `Microsoft.Extensions.AI` abstractions for chat clients and embedding generators. The project needs to support OpenAI, Azure OpenAI, and future embedding/chat providers without hardcoding a vendor-specific client into core memory behavior.

## Decision

Use `Microsoft.Extensions.AI` abstractions for embeddings and LLM-facing extraction.

- Embeddings use `IEmbeddingGenerator<string, Embedding<float>>`.
- LLM extractors use MEAI-compatible chat abstractions.
- Core registers `StubEmbeddingGenerator` and stub extractors as safe defaults.
- Production semantic search requires the host to register a real embedding generator.

## Consequences

Positive consequences:

- Provider choice is delegated to the host application.
- OpenAI, Azure OpenAI, and future MEAI-compatible providers can be swapped through DI.
- Core remains vendor-neutral.
- Tests can use stubs and fakes.

Tradeoffs:

- Users must understand that stubs are not production semantic search.
- Embedding dimensions must be configured consistently with Neo4j indexes.
- LLM extraction requires explicit opt-in and a configured chat client.

## Alternatives Considered

### Hardcode OpenAI

Rejected. It would make initial examples simple but would lock the project to one provider.

### Define a custom embedding abstraction

Rejected. MEAI already provides the .NET ecosystem abstraction, and another local abstraction would add adapter work without much benefit.

### Require embeddings for all usage

Rejected. Basic message storage and non-semantic workflows should still work without a live embedding provider.

## Verification Anchors

- `docs/getting-started.md` shows MEAI embedding registration.
- `src/AgentMemory.Core/ServiceCollectionExtensions.cs` registers the embedding orchestrator and stubs.
- `src/AgentMemory/ServiceCollectionExtensions.cs` wires LLM extraction only when `configureLlm` is provided.
- `src/AgentMemory.Neo4j/Infrastructure/Neo4jOptions.cs` defines embedding dimensions and validation.
