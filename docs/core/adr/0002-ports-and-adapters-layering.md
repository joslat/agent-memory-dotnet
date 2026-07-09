# ADR 0002 - Ports and Adapters Layering

Status: Accepted

Date: 2026-07-09

## Context

The library needs to support direct API usage, Neo4j persistence, extraction backends, multiple framework adapters, observability, enrichment, analytics, and future optional integrations. If core behavior depends directly on infrastructure packages, users lose testability and optionality.

The codebase already expresses this shape through `AgentMemory.Abstractions`, `AgentMemory.Core`, and infrastructure/adapter packages.

## Decision

Use a ports-and-adapters architecture.

- `AgentMemory.Abstractions` defines domain models, options, service ports, repository ports, and schema constants.
- `AgentMemory.Core` implements memory orchestration over those ports.
- `AgentMemory.Neo4j` implements the Neo4j infrastructure and repositories.
- Extraction, observability, enrichment, analytics, and framework packages are adapters around the core.

The Core package must remain constructible with stub/default implementations and without requiring Neo4j-specific code in its domain services.

## Consequences

Positive consequences:

- Core memory behavior is unit-testable.
- Infrastructure can be replaced or extended through DI.
- Optional packages can stay optional.
- Framework adapters can map runtime concepts to the same memory service contracts.

Tradeoffs:

- There are more interfaces and package boundaries.
- Some behavior requires careful DI registration to avoid missing services.
- Docs must explain package roles so users do not over-install or under-wire dependencies.

## Alternatives Considered

### Single monolithic package

Rejected. It would simplify installation but force every consumer to carry every dependency and would make optional integrations harder to isolate.

### Neo4j-first domain services

Rejected. Neo4j is the primary persistence target, but core orchestration should not be inseparable from a specific query implementation.

### Framework-specific memory implementations

Rejected. MAF, SK, MCP, and direct API usage should share the same memory model and service layer.

## Verification Anchors

- `src/AgentMemory.Abstractions/` contains contracts and schema constants.
- `src/AgentMemory.Core/ServiceCollectionExtensions.cs` registers core services over interfaces.
- `src/AgentMemory.Neo4j/Infrastructure/ServiceCollectionExtensions.cs` registers repository implementations.
- `docs/core/design-document.md` documents the dependency direction.
