# ADR 0009 - Extraction Pipeline and Backends

Status: Accepted

Date: 2026-07-09

## Context

Long-term memory is produced by extracting entities, facts, preferences, and relationships from messages or text. Extraction can be done by LLMs, Azure Language, future local NLP, or stubs in tests. The persistence behavior should be consistent regardless of extractor backend.

## Decision

Use a staged extraction pipeline with backend-specific extractors and a shared persistence stage.

The pipeline shape is:

1. Extraction stage gathers output from registered extractors.
2. Persistence stage resolves entities, stamps owner scope, generates embeddings where appropriate, writes through repositories, and creates provenance links.

Core registers no-op stub extractors by default. LLM extraction and Azure Language extraction are opt-in. Streaming extraction is a helper that produces chunks/entities but does not bypass the normal persistence path.

## Consequences

Positive consequences:

- The system can run without LLM dependencies.
- Extraction backends can be replaced or combined through DI.
- Owner stamping and provenance are centralized in persistence.
- Future local NLP extractors can join the same pipeline.

Tradeoffs:

- A user expecting automatic extraction must explicitly call extraction APIs or use an adapter that does it.
- Backend registration order and replacement behavior must be documented.
- LLM extraction robustness is a backend concern, not a Core guarantee.

## Alternatives Considered

### Auto-extract on every message in Core

Rejected. Automatic extraction is an adapter/workflow concern and may be too expensive or surprising for direct Core users.

### Make LLM extraction mandatory

Rejected. Basic memory storage, tests, and privacy-sensitive deployments should not require an LLM.

### Separate persistence per extractor backend

Rejected. It would duplicate owner-stamping, provenance, and repository behavior.

## Verification Anchors

- `src/AgentMemory.Core/ServiceCollectionExtensions.cs` registers extraction stage, persistence stage, pipeline, streaming extractor, and stubs.
- `src/AgentMemory/ServiceCollectionExtensions.cs` calls LLM extraction only when `configureLlm` is supplied.
- `src/AgentMemory.Extraction.Llm/` and `src/AgentMemory.Extraction.AzureLanguage/` contain optional backend packages.
- `docs/design.md` and `docs/core/design-document.md` describe stubs as defaults, not the whole implementation.
