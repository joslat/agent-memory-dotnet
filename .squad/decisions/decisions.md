# Architecture & Implementation Decisions

## Decision: Foundation Bootstrap Structure

**Author:** Gaff  
**Date:** 2025-01-27  
**Epic:** 1 — Foundation Bootstrap  

### Decision

The solution scaffold and directory structure for `Neo4j.AgentMemory` is now in place with the following conventions locked in:

#### Project Layout
```
src/
  Neo4j.AgentMemory.Abstractions/   — zero external deps, domain + interfaces
  Neo4j.AgentMemory.Core/           — services, DI wiring, validation
  Neo4j.AgentMemory.Neo4j/          — driver, repos, Cypher, schema
tests/
  Neo4j.AgentMemory.Tests.Unit/
  Neo4j.AgentMemory.Tests.Integration/
deploy/
  docker-compose.dev.yml            — port 7687, persistent volume
  docker-compose.test.yml           — port 7688, tmpfs (ephemeral)
```

#### Build Conventions
- `Directory.Build.props` at root: `net9.0`, nullable, implicit usings, latest lang, `TreatWarningsAsErrors=true` for src only
- All NuGet packages pinned to latest stable at time of bootstrap

#### Key Finding
Roy (domain engineer) pre-deposited domain model `.cs` files into `Abstractions` before the solution existed. The Options types (`RecallOptions`, `MemoryOptions`, `ContextBudget`, `ShortTermMemoryOptions`, `LongTermMemoryOptions`, `ReasoningMemoryOptions`) were missing. Gaff created them from Roy's design doc to unblock the build.

**Recommendation:** Establish a convention that domain design agents commit code only after the solution exists, or coordinate with Gaff to create stubs.

### Status
- `dotnet build` → ✅ 0 errors, 0 warnings  
- `dotnet test` → ✅ passes (no tests yet)

---

## Decision: Abstractions Package — Type File Organization

**Author:** Roy  
**Date:** 2025-01-27  
**Status:** Implemented

### Context

During Epic 2 scaffolding, Roy discovered Gaff had pre-seeded the `Options/` folder with some files that co-located enums and records in the same file (e.g., `SessionStrategy` enum + `ShortTermMemoryOptions` record in one file). The task spec called for each type to have its own file.

### Decision

**Each type — record, class, or enum — lives in its own `.cs` file**, named after the type.

- `SessionStrategy.cs` → `SessionStrategy` enum only  
- `ShortTermMemoryOptions.cs` → `ShortTermMemoryOptions` record only  
- `RetrievalBlendMode.cs` → `RetrievalBlendMode` enum only  
- `TruncationStrategy.cs` → `TruncationStrategy` enum only  

The pre-existing combined files were updated to remove the duplicated enum declarations.

### Rationale

1. Easier to navigate and find types in a large codebase
2. Cleaner git history — a change to an enum won't touch the related record's file
3. Consistent with the directory structure specified in the task

### Impact

- Any future agent scaffolding files in this repo should follow one-type-per-file convention
- Applies to Domain, Services, Repositories, and Options layers

---

## Decision: Neo4j Infrastructure Layer Design

**Author:** Gaff  
**Date:** 2025-07-14  
**Status:** Implemented  
**Scope:** Epic 3 — Neo4j Infrastructure Layer

### Context

The Neo4j infrastructure layer is the plumbing used by all repositories. It provides driver/session management, transaction wrapping, schema bootstrapping, and migration running.

### Decisions Made

#### 1. Transaction Runner hides IAsyncSession from repositories

`INeo4jTransactionRunner` provides `ReadAsync<T>` and `WriteAsync<T>` with `Func<IAsyncQueryRunner, Task<T>>` delegates. Repositories receive an `IAsyncQueryRunner` only — they never see `IAsyncSession`. This enforces correct lifecycle management and prevents direct session use.

#### 2. SchemaBootstrapper uses IF NOT EXISTS everywhere

All 9 constraints and 3 fulltext indexes use `IF NOT EXISTS` syntax. This makes bootstrap idempotent — safe to run at every application startup without side effects.

#### 3. MigrationRunner uses file-system loading + graph tracking

Migrations are `.cypher` files in `Schema/Migrations/`, sorted alphabetically (use date-prefixed names like `001_init.cypher`). Applied migrations are tracked as `(:Migration {version, appliedAtUtc})` nodes in Neo4j itself. A `IF NOT EXISTS` constraint on `Migration.version` ensures idempotent tracking.

#### 4. DI lifecycle: Singleton driver + factory, Transient runner

- `INeo4jDriverFactory` — **singleton** (one IDriver per app)
- `INeo4jSessionFactory` — **singleton** (stateless, creates sessions from singleton driver)
- `INeo4jTransactionRunner` — **transient** (opens and closes session per call)
- `ISchemaBootstrapper` / `IMigrationRunner` — **transient** (called once at startup, then discarded)

#### 5. Encryption controlled by Neo4jOptions.EncryptionEnabled

Maps to `EncryptionLevel.None` or `EncryptionLevel.Encrypted`. Default is `false` (local dev). Production deployments should set `true`.

### Files Created

```
src/Neo4j.AgentMemory.Neo4j/Infrastructure/
  Neo4jOptions.cs
  INeo4jDriverFactory.cs
  Neo4jDriverFactory.cs
  INeo4jSessionFactory.cs
  Neo4jSessionFactory.cs
  INeo4jTransactionRunner.cs
  Neo4jTransactionRunner.cs
  ISchemaBootstrapper.cs
  SchemaBootstrapper.cs
  IMigrationRunner.cs
  MigrationRunner.cs
  ServiceCollectionExtensions.cs
```

### Build Result

`dotnet build` — 0 errors, 0 warnings across all 5 projects.

---

## Decision: Stub Implementation Patterns (Roy, Epic 8)

**Status:** Implemented  
**Author:** Roy  
**Date:** 2025-01-28

### Context

Phase 1 requires all AI-backed services (embedding, extraction) to be stubbed so the memory core can function end-to-end without real LLM/NLP infrastructure.

### Decisions

#### D-ROY-STUBS-1: Stub Location

All stub implementations live in `src/Neo4j.AgentMemory.Core/Stubs/`. Named with `Stub` prefix for extractors/providers and no prefix for simple wrappers (e.g., `SystemClock`, `GuidIdGenerator`).

#### D-ROY-STUBS-2: Deterministic Embeddings

`StubEmbeddingProvider` uses `string.GetHashCode()` as the `Random` seed to produce deterministic vectors per input text within a process run. Sufficient for Phase 1 testing — not cryptographically stable across .NET versions.

**Rationale:** Enables repeatable test assertions. Phase 2 replaces with a real provider; determinism will then come from model behavior.

#### D-ROY-STUBS-3: Logging Contract

- `StubEmbeddingProvider` logs at **Warning** level (callers should notice it's a stub in production-like environments)
- All extraction stubs log at **Debug** level (less noisy, expected to be replaced)
- `StubEntityResolver` logs at **Debug** level

#### D-ROY-STUBS-4: StubEntityResolver Requires IClock + IIdGenerator

`StubEntityResolver.ResolveEntityAsync` must produce a valid `Entity` record. Since `Entity.EntityId` and `Entity.CreatedAtUtc` are required, the resolver accepts `IClock` and `IIdGenerator` via constructor injection.

#### D-ROY-STUBS-5: FluentAssertions v8 Large Collection Caveat

`NotBeEquivalentTo` on large (1536-element) float arrays hangs the test host in FluentAssertions 8.x. Use `SequenceEqual(...).Should().BeFalse(reason)` for large collection inequality checks.

### Impact

- Phase 1 system is fully wirable with DI using only these stubs
- No AI API keys required to run the application or unit tests
- Phase 2 simply replaces stubs in DI registration — no interface changes needed

---

## Decision: Test Harness Design (Epic 9)

**Author:** Holden  
**Date:** 2025-01-28  
**Status:** Implemented

### Context

Epic 9 required bootstrapping the integration test infrastructure so all future repository and service tests have a working harness from day one.

### Decisions Made

#### TC-1: Single shared Neo4j container per test run

All integration tests share one `Neo4jContainer` via xUnit's `[CollectionDefinition("Neo4j")]` pattern. This avoids repeated container startup costs (each start takes ~10–20 seconds).

**Implication:** Tests must use unique IDs or clean up their own data. Do not assume a clean database state at test start — either use `Guid.NewGuid()` for entity IDs or add a `MATCH (n) DETACH DELETE n` in test setup where isolation is critical.

#### TC-2: Testcontainers.Neo4j password via environment variable

`Neo4jBuilder` 4.x does not expose `WithPassword`. Password is set via `WithEnvironment("NEO4J_AUTH", "neo4j/testpassword")`. The fixture hardcodes `Username = "neo4j"` and `Password = "testpassword"`.

#### TC-3: `As<T>` ambiguity resolution

`FluentAssertions` and `Neo4j.Driver` both define `.As<T>()` extension methods. In files that import both, use `global::Neo4j.Driver.ValueExtensions.As<T>(value)` or explicit casts (`(long)record["key"]`) to avoid CS0121.

#### TC-4: MockFactory for unit tests

`MockFactory` in `Tests.Unit/TestHelpers/` provides:
- `CreateFixedClock` — deterministic time (2025-01-01T00:00:00Z)
- `CreateSequentialIdGenerator` — returns `{prefix}-1`, `{prefix}-2`, …
- `CreateStubEmbeddingProvider` — deterministic vectors seeded from `text.GetHashCode()`

### Files Delivered

```
tests/Neo4j.AgentMemory.Tests.Integration/
  Neo4jTestFixture.cs
  Neo4jTestCollection.cs
  TestDataSeeders.cs
  IntegrationTestBase.cs
  Neo4jConnectivityTests.cs

tests/Neo4j.AgentMemory.Tests.Unit/
  TestHelpers/MockFactory.cs
```

---

## Decision: User Directives — Architecture Boundaries

**Directive:** Jose Luis Latorre Millas (via Copilot)  
**Date:** 2026-04-12T17:58Z

### What

1. **MAF must NOT be collapsed into core** — it stays as a separate adapter layer
2. **GraphRAG must NOT be merged into the memory engine** — separate interoperability adapter
3. **No speculative extra features** — build only what the spec calls for
4. **Tests from the beginning** — TDD, not afterthought
5. **Investigate Neo4j/neo4j-maf-provider/dotnet** as potential base for the Neo4j adapter layer (already has KG access for Neo4j, context provider for MAF, built for MAF 0.3, now MAF is 1.1.0 post-GA)

### Why

User request — architectural boundaries are load-bearing constraints for the project.

---

## ADR-7: Package Dependency Graph and Boundary Enforcement

**Status:** Approved  
**Author:** Deckard (Lead Architect)  
**Date:** 2025-01-28  
**Scope:** All phases

### Package Dependency Graph

```
Phase 1 (Current):
┌──────────────────────┐
│  Abstractions        │  ← Zero dependencies
│  (domain contracts)  │
└──────┬───────────────┘
       │ depends on
       ▼
┌──────────────────────┐
│  Core                │  ← Abstractions + M.E.DI + M.E.Logging + M.E.Options
│  (orchestration)     │
└──────┬───────────────┘
       │ depends on
       ▼
┌──────────────────────┐
│  Neo4j               │  ← Abstractions + Core + Neo4j.Driver + M.E.*
│  (persistence)       │
└──────────────────────┘

Phase 3 (Future):
┌──────────────────────┐     ┌──────────────────────┐
│  MAF Adapter         │     │  GraphRAG Adapter     │
│  (AgentMemory.MAF)   │     │  (AgentMemory.        │
│                      │     │   GraphRagAdapter)    │
└──────┬───────────────┘     └──────┬───────────────┘
       │ depends on                  │ depends on
       ▼                             ▼
┌──────────────────────┐     ┌──────────────────────┐
│  Core + Abstractions │     │  Core + Abstractions  │
│  + Microsoft.Agents  │     │  + Neo4j.AgentFW.     │
│    .AI.*             │     │    GraphRAG           │
└──────────────────────┘     └──────────────────────┘
```

Both adapters depend INWARD on Core + Abstractions. Neither adapter depends on the other. Core never depends on either adapter.

### Boundary Enforcement Rules

| Rule | What MUST NOT Happen |
|---|---|
| B1 | Abstractions MUST NOT reference any NuGet package |
| B2 | Core MUST NOT reference Neo4j.Driver |
| B3 | Core MUST NOT reference Microsoft.Agents.* |
| B4 | Core MUST NOT reference Neo4j.AgentFramework.GraphRAG |
| B5 | Neo4j MUST NOT reference Microsoft.Agents.* |
| B6 | Neo4j MUST NOT reference Neo4j.AgentFramework.GraphRAG |
| B7 | No adapter package may add business logic that belongs in Core |
| B8 | Adapters depend on Core/Abstractions — never the reverse |

**Enforcement mechanism:** Code review gates. CI should eventually include a boundary-check step that scans .csproj files for prohibited references.

### Neo4j Adapter Layer Strategy

**Decision: ADAPT patterns, do NOT fork or wrap.**

Rationale:
1. **Do not fork** — the retriever code is tightly coupled to its own `RetrieverResult` type and M.E.AI's `IEmbeddingGenerator`. Forking creates a maintenance burden with no upstream sync benefit.
2. **Do not wrap** — wrapping would add an unnecessary dependency on the existing package. Our Neo4j package must only depend on `Neo4j.Driver`, not on `Neo4j.AgentFramework.GraphRAG`.
3. **Do adapt the Cypher patterns** — the `db.index.vector.queryNodes` and `db.index.fulltext.queryNodes` Cypher patterns are the valuable reusable knowledge. Copy the Cypher query structures into our repository implementations, adapted to our graph schema (typed nodes: `(:Entity)`, `(:Message)`, `(:Fact)`, etc.).
4. **For the GraphRAG adapter (Phase 3)** — when we build `AgentMemory.GraphRagAdapter`, it WILL reference the existing `Neo4j.AgentFramework.GraphRAG` package and implement our `IGraphRagContextSource` by delegating to the existing `IRetriever` implementations. This is the correct integration point.
5. **StopWords utility** — the stop-word filtering pattern is useful. Replicate the pattern in our Neo4j package if/when we add fulltext search to repositories.

### Test Strategy

| Test Type | Project | Scope | Dependencies |
|---|---|---|---|
| **Unit** | `Tests.Unit` | Core services, stubs, domain logic, validation | NSubstitute, FluentAssertions, xUnit |
| **Integration** | `Tests.Integration` | Repository implementations, schema bootstrap, transaction behavior | Testcontainers.Neo4j, Neo4j.Driver, real DB |
| **E2E** (future) | `Tests.E2E` (Phase 3+) | Full pipeline with MAF adapter | MAF test host + Testcontainers |

Rules:
- Every repository implementation gets integration tests BEFORE moving to the next repository.
- Every service implementation gets unit tests BEFORE the service is considered done.
- Integration tests use shared Neo4j fixture (one container per test run).
- Unit tests use MockFactory + NSubstitute — no real infrastructure.
- Test data seeders provide factory methods for all domain types.

### Architecture Status

The foundation is solid. Package boundaries are correct. No leakage. No speculation. 

**Architecture Alignment: 10/10 checks pass.**

### Standing Directives (Reinforced)

1. **MAF stays separate.** No `Microsoft.Agents.*` in any `src/Neo4j.AgentMemory.*` package. Ever.
2. **GraphRAG stays separate.** No `Neo4j.AgentFramework.GraphRAG` dependency in any `src/Neo4j.AgentMemory.*` package. Ever.
3. **Tests first.** Every repository and service gets tests before or during implementation, not after.
4. **No feature creep.** If it's not in the spec, don't build it.
5. **The existing neo4j-maf-provider is a reference** — we adapt its Cypher patterns, we don't absorb its code.

---

## Decision: Vector and Property Indexes in SchemaBootstrapper

**Author:** Gaff  
**Date:** 2025-07-14  
**Status:** Implemented

### Context

The Python agent-memory analysis (`docs/python-agent-memory-analysis.md`) revealed that the SchemaBootstrapper only created 9 unique constraints and 3 fulltext indexes. Five vector indexes (essential for `SearchByVectorAsync`) and 9 property indexes (essential for query performance on filtered lookups) were missing entirely. Without vector indexes, any embedding-based search would scan all nodes — unacceptable for production.

### Decision

**Add 5 vector indexes and 9 property indexes to `SchemaBootstrapper`, and make embedding dimensions configurable via `Neo4jOptions`.**

#### Vector indexes (cosine similarity, configurable dimensions, default 1536)

| Index name | Node label | Property |
|---|---|---|
| `message_embedding_idx` | Message | embedding |
| `entity_embedding_idx` | Entity | embedding |
| `preference_embedding_idx` | Preference | embedding |
| `fact_embedding_idx` | Fact | embedding |
| `reasoning_step_embedding_idx` | ReasoningStep | embedding |

#### Property indexes (for filtered queries)

| Index name | Node label | Property |
|---|---|---|
| `message_session_id` | Message | sessionId |
| `message_timestamp` | Message | timestamp |
| `entity_type` | Entity | type |
| `entity_name_prop` | Entity | name |
| `fact_category` | Fact | category |
| `preference_category` | Preference | category |
| `reasoning_trace_session_id` | ReasoningTrace | sessionId |
| `reasoning_step_timestamp` | ReasoningStep | timestamp |
| `tool_call_status` | ToolCall | status |

### Configuration

`Neo4jOptions.EmbeddingDimensions` (int, default 1536) controls the `vector.dimensions` in all 5 vector index definitions. This allows operators to switch to 3072-dim models (OpenAI text-embedding-3-large) or 768-dim models without rewriting schema.

### Rationale

1. **Correctness:** Neo4j vector search requires a vector index — without one, `db.index.vector.queryNodes()` returns nothing. The repositories being built in the next epic depend on these indexes existing.
2. **Performance:** Property indexes on high-cardinality filter fields (sessionId, type, status) prevent full label scans on common query patterns.
3. **Idempotency:** All statements use `IF NOT EXISTS` — safe to re-run on every startup.
4. **Configurability:** Hardcoding 1536 would break deployments using different embedding models. `EmbeddingDimensions` in options follows the existing options pattern in the codebase.

### Impact

- `SchemaBootstrapper` now executes 26 statements total (9 constraints + 3 fulltext + 5 vector + 9 property) instead of 12.
- `Neo4jOptions` gains one new property (`EmbeddingDimensions`), backward-compatible default.
- Unit test project now references `Neo4j.AgentMemory.Neo4j` — required for testing schema infrastructure directly.

---

## Decision: Consolidated Architecture Documentation

**Author:** Deckard (Lead Architect)  
**Date:** 2025-07-12  
**Status:** Implemented  
**Requested by:** Jose Luis Latorre Millas

### Context

Architecture knowledge was scattered across internal Squad files (`.squad/decisions/inbox/deckard-architecture-review.md`, `.squad/agents/roy/domain-design-v1.md`, `.squad/decisions.md`). Jose needed proper project-level documentation that:
- Can be reviewed by anyone (not just Squad agents)
- Is traceable to the canonical specification
- Is accurate to what's actually implemented
- Can be shared with stakeholders

### Decision

Created three project-level documents under `docs/`:

1. **`docs/architecture.md`** — Architecture overview (package dependencies, graph model, boundary rules, neo4j-maf-provider relationship, test strategy, phase roadmap)
2. **`docs/design.md`** — Software design (domain model, memory layers, context assembly, extraction pipeline, service/repository catalogs, configuration model, design decisions)
3. **`docs/neo4j-maf-provider-analysis.md`** — Reuse strategy (code inventory, Cypher patterns, what we don't take, Phase 4 integration plan, MAF version gap)

### Rationale

- Consolidates scattered Squad-internal knowledge into canonical, shareable documents
- All claims verified against actual .csproj files and source code
- References specific specification sections for traceability
- Includes "Last Updated" and "Phase 1 Status" for temporal context
- Uses Mermaid diagrams (GitHub-renderable) where appropriate

### Impact

- Stakeholders can now review architecture without reading Squad internal files
- New contributors have a clear onboarding path
- Boundary rules are documented in a single authoritative location
- neo4j-maf-provider reuse strategy is formally documented and not just tribal knowledge

---

## Decision: Document Alignment Review Results

**Author:** Deckard (Lead Architect)  
**Date:** 2025-07-13  
**Status:** Implemented  
**Scope:** Documentation alignment and status tracking

### Decision

Created `docs/implementation-status.md` as the single status reference for the project. Updated `docs/architecture.md` to fix 3 stale sections (vector indexes, property indexes, Phase 1 status).

### Findings

1. **No contradictions** between the specification, implementation plan, and docs/ files.
2. **No spec-level gaps** requiring changes to the read-only specification.
3. **5 staleness issues** found and addressed (3 fixed in architecture.md, 2 documented as known-stale in python analysis).
4. **1 schema gap** identified: missing `task_embedding_idx` for `ReasoningTrace.taskEmbedding`. Should be added during Epic 6.

### Rationale

Jose needs a single document to understand project status without reading all 6+ documents. The implementation-status.md serves this purpose and also provides a document alignment audit trail.

### Impact

- `docs/implementation-status.md` is now the canonical status reference
- `docs/architecture.md` is now current as of 2025-07-13
- `docs/python-agent-memory-analysis.md` has known-stale sections (index comparison) documented in the status tracker
- The spec and impl plan remain untouched (read-only source of truth)

---

## Decision: Python agent-memory Reference Analysis

**Author:** Deckard (Lead Architect)  
**Date:** 2025-07-12  
**Status:** Implemented  
**Requested by:** Jose Luis Latorre Millas

### Context

The Python `neo4j-labs/agent-memory` is the conceptual reference for our .NET implementation. The team needed a comprehensive analysis that maps every Python module to our .NET solution, identifies what we take, what we skip, and what we do differently — with verified claims against actual source code.

### Decision

Created `docs/python-agent-memory-analysis.md` — a 13-section reference document covering:

1. **Architecture comparison** — Python flat modules vs .NET layered ports-and-adapters
2. **Module-by-module analysis** — 15 modules with ADAPT/SKIP/DEFER/REFERENCE strategy
3. **Neo4j graph model comparison** — node types, relationships, constraints, indexes
4. **Cypher pattern catalog** — 60+ queries mapped to .NET repositories
5. **Extraction pipeline deep dive** — 3-stage pipeline, merge strategies, LLM prompts
6. **Entity resolution deep dive** — 4-strategy chain with type-strict filtering
7. **Configuration, test, and dependency comparisons**
8. **Phase mapping** — Python features to our Phases 1–6
9. **Risk assessment** with specific action items

### Key Findings Requiring Action

1. **Vector indexes missing** — SchemaBootstrapper needs 5 vector indexes (HIGH priority)
2. **Property indexes missing** — 9 regular indexes needed for query performance (MEDIUM)
3. **Cross-memory relationships undefined** — INITIATED_BY, TRIGGERED_BY, HAS_TRACE (MEDIUM)
4. **Cypher query centralization** — recommend moving inline queries to centralized constants (LOW)

### Rationale

- Analysis verified against actual source: 7/7 claims PASS
- Honest about gaps — document says what Python does that we don't, and why
- Honest about improvements — document says where our .NET design is better
- References spec sections for traceability
- Phase-mapped so the team knows what's Phase 1 vs future

### Impact

- Team has a single reference document for understanding the Python → .NET adaptation
- Action items identified for schema gaps (vector indexes, property indexes)
- Phase 2 planning has concrete targets (extraction prompt templates, resolution algorithms)
- Phase 6 MCP planning has tool definitions and profiles documented
