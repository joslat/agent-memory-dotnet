# ADR 0011 - Opt-In Cross-Cutting Packages

Status: Accepted

Date: 2026-07-09

## Context

The project supports observability, enrichment, Azure Language extraction, analytics, and GraphRAG retrieval. These are valuable capabilities, but each adds external dependencies, configuration, runtime costs, or infrastructure requirements.

Basic memory storage and recall should remain easy to wire and should not require optional services.

## Decision

Keep cross-cutting capabilities opt-in.

Accepted registration behavior:

- Observability is added with `WithObservability()`.
- Enrichment is added with `WithEnrichment(...)`.
- Azure Language extraction is added with `WithAzureLanguageExtraction(...)`.
- LLM extraction is active only when configured.
- GraphRAG is added with `AddGraphRagAdapter(...)`.
- Analytics is installed through `AgentMemory.Analytics` and is not part of the meta-package.

## Consequences

Positive consequences:

- Minimal installs remain lightweight.
- Users can avoid unwanted network calls and external services.
- Optional features can fail gracefully or be configured independently.
- The meta-package can be convenient without making everything active.

Tradeoffs:

- Users must explicitly opt in to features they expect.
- Docs and samples must show the registration chain clearly.
- Some capabilities require package-specific setup beyond the meta-package.

## Alternatives Considered

### Auto-enable all referenced packages

Rejected. It would make construction fragile and surprise users with external dependencies.

### Exclude optional packages from the meta-package entirely

Partially rejected. The meta-package includes some optional references for convenience, but behavior is still explicitly registered.

### Put every optional feature behind one mega-option

Rejected. Each capability has different dependencies and operational constraints.

## Verification Anchors

- `src/AgentMemory/ServiceCollectionExtensions.cs` exposes `WithObservability`, `WithEnrichment`, and `WithAzureLanguageExtraction`.
- `src/AgentMemory.Neo4j/Infrastructure/ServiceCollectionExtensions.cs` exposes `AddGraphRagAdapter`.
- `src/AgentMemory.Analytics/AgentMemory.Analytics.csproj` is separate from the meta-package.
- `docs/getting-started.md` documents optional registration.
