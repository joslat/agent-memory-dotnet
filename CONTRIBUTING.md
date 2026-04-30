# Contributing to Agent Memory for .NET

Thank you for your interest in contributing! This guide covers environment setup, build and test commands, coding conventions, and the PR process.

---

## 1. Dev Setup

### Prerequisites

| Tool | Version | Purpose |
|------|---------|---------|
| [.NET SDK](https://dotnet.microsoft.com/download) | **9.0+** | Build and test |
| [Docker Desktop](https://www.docker.com/products/docker-desktop/) | Any recent | Testcontainers (integration tests start Neo4j 5.x automatically — no manual Neo4j install needed) |
| Git | Any | Source control |

### Clone and restore

```bash
git clone https://github.com/joslat/agent-memory-dotnet.git
cd agent-memory-dotnet
dotnet restore Neo4j.AgentMemory.slnx
```

---

## 2. Build

```bash
dotnet build Neo4j.AgentMemory.slnx
```

The solution is configured via `Directory.Build.props`:
- Target framework: **net9.0**
- Nullable reference types: **enabled**
- `TreatWarningsAsErrors`: **true** for all `src/` projects (disabled for `tests/`)

A clean build must produce **zero errors and zero warnings** in `src/`. PRs that introduce warnings will not be merged.

---

## 3. Tests

### Unit tests (no external dependencies)

```bash
dotnet test tests/Neo4j.AgentMemory.Tests.Unit/Neo4j.AgentMemory.Tests.Unit.csproj
```

Unit tests cover all packages and use stub implementations (no Neo4j, no LLM required).

### Semantic Kernel adapter unit tests

```bash
dotnet test tests/Neo4j.AgentMemory.Tests.Unit.SemanticKernel/Neo4j.AgentMemory.Tests.Unit.SemanticKernel.csproj
```

### Integration tests (requires Docker)

```bash
dotnet test tests/Neo4j.AgentMemory.Tests.Integration/Neo4j.AgentMemory.Tests.Integration.csproj
```

Integration tests use [Testcontainers](https://testcontainers.com/guides/getting-started-with-testcontainers-for-dotnet/) to spin up a disposable Neo4j 5 container automatically. Docker Desktop must be running.

### All tests at once

```bash
dotnet test Neo4j.AgentMemory.slnx
```

---

## 4. Code Conventions

### Architecture — ports and adapters

The solution follows a strict **ports-and-adapters** (hexagonal) architecture:

```
Abstractions  ←  Core  ←  Neo4j / Extraction / AgentFramework / SemanticKernel / ...
```

- **`Abstractions`** defines all domain types and service interfaces. It has **zero external dependencies** (only `Microsoft.Extensions.AI.Abstractions`).
- **`Core`** implements services and the extraction pipeline. It depends only on `Abstractions`.
- **`Neo4j`** provides Neo4j repository implementations. It depends on `Abstractions` + `Core`.
- **Adapter packages** (`AgentFramework`, `SemanticKernel`, `McpServer`, etc.) depend inward on `Abstractions` and optionally `Core`. They never reference each other or `Neo4j`.

**Do not** add framework dependencies (MAF, SK, Neo4j Driver) to `Abstractions` or `Core`. Violations will be caught in review.

### Cypher queries

All Cypher query strings are stored as typed constants in domain-specific `Queries/` classes within `Neo4j.AgentMemory.Neo4j`. Do not inline Cypher strings in repository implementations.

```
Neo4j.AgentMemory.Neo4j/
└── Queries/
    ├── MessageQueries.cs
    ├── EntityQueries.cs
    ├── FactQueries.cs
    └── ...   (one file per domain)
```

### Domain models

All domain types are **`sealed record`** types with `required` properties for spec-mandated fields. Timestamps use `DateTimeOffset` with a `Utc` suffix (e.g., `CreatedAtUtc`). Collections use `IReadOnlyList<T>` / `IReadOnlyDictionary<K,V>` and default to empty (never null).

### Async and cancellation

All async methods accept a `CancellationToken cancellationToken = default` parameter. Pass the token through every layer.

### Stubs

Stub implementations live in `Neo4j.AgentMemory.Core/Stubs/` and are used in unit tests. They implement the same interfaces as production types and return deterministic results without any external service dependency.

### No TODO / FIXME / HACK

The codebase maintains zero `TODO`, `FIXME`, or `HACK` comments. Complete your work before raising a PR or track follow-up work via GitHub issues.

---

## 5. Submitting PRs

### Branch naming

```
feature/<short-description>
fix/<short-description>
docs/<short-description>
refactor/<short-description>
```

### Commit message format

```
<type>: <short imperative summary>

<optional body: why this change, not what>
```

Types: `feat`, `fix`, `docs`, `refactor`, `test`, `chore`.

Example:
```
feat: add RecallAsOfAsync temporal point-in-time recall

Allows agents to reconstruct the memory state at any past moment.
Queries use Neo4j datetime() comparisons on createdAtUtc properties.
```

### What reviewers check

1. **Zero build warnings** in `src/`
2. **Tests pass** — both unit and (if applicable) integration
3. **No boundary violations** — check dependency direction
4. **Cypher in constants files** — not inline
5. **No IAgentMemory / StoreMessageAsync / AssembleContextAsync** — these don't exist; use `IMemoryService` and its actual methods
6. **Docs updated** alongside code changes (see §6 below)

---

## 6. Documentation

Update documentation alongside code changes when:

- You add or change a public interface in `Abstractions`
- You add a new package or DI extension method
- You change configuration options or default values
- You add a new feature that users need to know about

Docs to update:
- `docs/architecture.md` — for architectural changes
- `docs/schema.md` — for graph schema changes (new node types, relationships, indexes)
- `docs/getting-started.md` — for configuration or DI registration changes
- `README.md` — for significant new capabilities

---

## 7. Reporting Issues

Use [GitHub Issues](https://github.com/joslat/agent-memory-dotnet/issues) for bug reports, feature requests, and questions. Include:
- .NET version and OS
- Neo4j version
- A minimal reproducible example if reporting a bug
