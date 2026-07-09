# ADR 0003 - Package Topology and Dependency Boundaries

Status: Accepted

Date: 2026-07-09

## Context

The project has enough capabilities that one package would be too heavy, but a package-per-class model would be noisy. Users need a simple default install path and advanced users need optional packages for framework integrations and cross-cutting services.

The repository currently contains 11 adapter/library packages plus the `AgentMemory` meta-package.

## Decision

Ship focused packages with clear responsibility boundaries, plus a convenience meta-package.

The accepted package set is:

- `AgentMemory.Abstractions`
- `AgentMemory.Core`
- `AgentMemory.Neo4j`
- `AgentMemory.Extraction.Llm`
- `AgentMemory.Extraction.AzureLanguage`
- `AgentMemory.AgentFramework`
- `AgentMemory.SemanticKernel`
- `AgentMemory.McpServer`
- `AgentMemory.Observability`
- `AgentMemory.Enrichment`
- `AgentMemory.Analytics`
- `AgentMemory`

The meta-package references the common Core + Neo4j + extraction stack and exposes the high-level registration method. Analytics and framework adapters remain separate package choices.

## Consequences

Positive consequences:

- New users can start with `AgentMemory`.
- Advanced users can install only abstractions/core/Neo4j or specific adapters.
- Optional dependencies remain isolated.
- Package descriptions and tags can target specific use cases.

Tradeoffs:

- Documentation must explain which packages are included in the meta-package and which are not.
- More packages increase release and versioning coordination.
- Optional registration methods must be explicit to prevent surprising side effects.

## Alternatives Considered

### Only a meta-package

Rejected. Framework-specific and GDS dependencies should not be mandatory for all users.

### No meta-package

Rejected. A one-package starting point is valuable for adoption.

### Separate historical GraphRAG package

Rejected in current form. GraphRAG retrieval is now implemented inside `AgentMemory.Neo4j` and registered opt-in.

## Verification Anchors

- `src/` contains the package directories listed above.
- `src/AgentMemory/AgentMemory.csproj` references the common stack.
- `src/AgentMemory.Analytics/AgentMemory.Analytics.csproj` is separate from the meta-package.
- `docs/README.md` and `docs/core/specification.md` list the current package set.
