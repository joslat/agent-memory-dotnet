# AgentMemory for .NET — Tests

This folder holds the test suite for the solution (`AgentMemory.slnx`). All test projects target
`net9.0` and use **xUnit**, **FluentAssertions**, and **NSubstitute** (with `coverlet.collector` for
coverage); all are `IsPackable=false`.

## Test projects

| Project | ~Tests | Needs Docker | What it covers |
|---|---|---|---|
| `AgentMemory.Tests.Unit` | 2,654 as of 2026-07-09 local Release run | No | Pure unit tests. Mocks `IDriver` / `INeo4jTransactionRunner`. Domain/options/schema, extraction (LLM + AzureLanguage + pattern + streaming), repositories, services, MCP tools & resources, queries, observability, MAF & SK adapters, enrichment, infrastructure, resolution, guardrails. |
| `AgentMemory.Tests.Unit.SemanticKernel` | 34 as of 2026-07-09 local Release run | No | SK `Neo4jMemoryPlugin`, `Neo4jTextSearch`, and the kernel DI extensions. |
| `AgentMemory.Tests.Integration` | 236 as of 2026-06-21 ROADMAP record | **Yes** | Live Neo4j via **Testcontainers** (`neo4j:5.26`). Repository CRUD/dedup/delete, R1 owner-isolation, per-app store isolation, schema bootstrap/migrations, decay prune, conflict/consolidation, GraphRAG, and the full-stack shakedown. |
| `AgentMemory.Tests.Performance` | 3 | **Yes** | Throughput/latency smoke against live Neo4j (own fixture). |

## Running the tests

```bash
# Unit only (no Docker) — fast inner loop
dotnet test tests/AgentMemory.Tests.Unit
dotnet test tests/AgentMemory.Tests.Unit.SemanticKernel

# Integration / Performance (Docker required — pulls neo4j:5.26 via Testcontainers)
dotnet test tests/AgentMemory.Tests.Integration
dotnet test tests/AgentMemory.Tests.Performance
```

**Trait filtering:** only the **Integration** and **Performance** classes carry
`[Trait("Category", "Integration"|"Performance")]`; unit tests have **no** category trait. So
`--filter Category=Integration` selects the live tests, but there is no negative-trait way to isolate
units — run the unit project directly instead.

There are **no** Enterprise/edition-gated tests and **no** `Skip` attributes in the suite.

## Environment switches

| Variable | Effect |
|---|---|
| `UPDATE_CYPHER_SNAPSHOTS=1` | Regenerates `AgentMemory.Tests.Unit/Queries/CypherQuerySnapshot.snap`. Commit the updated `.snap`. When you add/remove a `public const string` Cypher query, also bump `ExpectedQueryCount` in `CypherQuerySnapshotTests` (currently **133**). Owner-conditional queries are **methods** (not consts) and are intentionally excluded from the count. |

## Testcontainers fixtures & collections

The integration project uses xUnit collection fixtures so the Neo4j container is shared across a
collection rather than per-test:

- **`[Collection("Neo4j Integration")]` → `Neo4jIntegrationFixture` (prefer this for new tests).**
  Starts `neo4j:5.26`, runs `SchemaBootstrapper` once, waits for the VECTOR indexes to come ONLINE
  (embedding dim = 4), and exposes `Driver` / `TransactionRunner` / connection info. Each test class
  calls `_fixture.CleanDatabaseAsync()` in `InitializeAsync`.
- **`[Collection("Neo4j")]` → `Neo4jTestFixture` (legacy, no schema bootstrap).** Only used by a couple
  of older classes (Conversation repository, legacy GraphRAG classes). Don't use it for new tests.
- **`PerfNeo4jFixture`** — the Performance project's own container fixture.

`[Trait("Category", "Integration")]` (or `"Performance"`) is applied at the class level.

## End-to-end / full-stack tests

- **`ShakedownEndToEndTests`** *(the canonical E2E)* — builds the **entire stack through the real meta
  `AddNeo4jAgentMemory` DI container** (`validateScopes: true`) against live Neo4j with a real stub
  embedding generator. It (1) resolves every top-level service from a scope — catching meta-DI wiring
  gaps and asserting the Neo4j DI replaces the Core no-op decay service with `Neo4jMemoryDecayService` —
  and (2) drives a multi-user (alice/bob/shared) flow across short-term recall, long-term **owner
  isolation**, owner-scoped entity feedback, a full reasoning-trace lifecycle (`:TOUCHED` included),
  dry-run consolidation, and conflict detection — all through resolved interfaces.
- **R1 multi-user isolation suite** (multi-subsystem, live): `OwnerScopeIsolationIntegrationTests`,
  `EntityResolutionOwnerScopeIntegrationTests`, `McpResourceIsolationIntegrationTests`,
  `Neo4jMemoryDecayServiceIntegrationTests`, `ExtractorProvenanceScopeIntegrationTests`,
  `OverFetchStarvationIntegrationTests`, `ExtractionOwnerStampIntegrationTests`, plus the
  `*ReadScope` / `*OwnerScope` repository tests. The shared pattern: seed `alice` / `bob` /
  shared(`null`) rows and assert a scoped read returns the owner's + shared rows and **never**
  another owner's. `ExtractionOwnerStampIntegrationTests` is the one that runs the real DI-registered
  `IMemoryExtractionPipeline` (deterministic test extractors standing in for an LLM) end to end, rather
  than seeding repositories directly.

## Conventions & guardrails

- **`CypherQuerySnapshotTests`** — snapshot regression + structural checks (valid Cypher keyword,
  parameterized `WHERE`/`SET`, known labels, balanced parens) + inventory count.
- **`PackageBoundaryGuardTests`** / **`AbstractionsContractGuardTests`** — architecture guards
  (project-reference boundaries; public-type counts of the Abstractions surface).
- **`MetaPackageDiRegistrationTests`** — the meta-package DI surface resolves.

## Known coverage gaps / follow-ups

None currently tracked here. The two gaps previously listed — end-to-end owner-stamp verification
through the full extraction/persistence path, and live-Neo4j isolation for the MCP `ContextResource` —
were closed by `ExtractionOwnerStampIntegrationTests` and the `ContextResource_*` tests added to
`McpResourceIsolationIntegrationTests`, respectively (see issue #99).

The isolation design and full per-finding history are recorded in the maintainers' internal project archive
(not part of the published docs).
