# AgentMemory for .NET — Tests

This folder holds the test suite for the solution (`AgentMemory.slnx`). All test projects target
`net9.0` and use **xUnit**, **FluentAssertions**, and **NSubstitute** (with `coverlet.collector` for
coverage); all are `IsPackable=false`.

## Test projects

| Project | ~Tests | Needs Docker | What it covers |
|---|---|---|---|
| `AgentMemory.Tests.Unit` | ~1,600 | No | Pure unit tests. Mocks `IDriver` / `INeo4jTransactionRunner`. Domain/options/schema, extraction (LLM + AzureLanguage + pattern + streaming), repositories, services, MCP tools & resources, queries, observability, MAF & SK adapters, enrichment, infrastructure, resolution, guardrails. |
| `AgentMemory.Tests.Unit.SemanticKernel` | ~33 | No | SK `Neo4jMemoryPlugin`, `Neo4jTextSearch`, and the kernel DI extensions. |
| `AgentMemory.Tests.Integration` | ~170 | **Yes** | Live Neo4j via **Testcontainers** (`neo4j:5.26`). Repository CRUD/dedup/delete, R1 owner-isolation, per-app store isolation, schema bootstrap/migrations, decay prune, conflict/consolidation, GraphRAG, and the full-stack shakedown. |
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
  of older classes (Conversation repository, GraphRAG adapter). Don't use it for new tests.
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
  `OverFetchStarvationIntegrationTests`, plus the `*ReadScope` / `*OwnerScope` repository tests. The
  shared pattern: seed `alice` / `bob` / shared(`null`) rows and assert a scoped read returns the
  owner's + shared rows and **never** another owner's.

## Conventions & guardrails

- **`CypherQuerySnapshotTests`** — snapshot regression + structural checks (valid Cypher keyword,
  parameterized `WHERE`/`SET`, known labels, balanced parens) + inventory count.
- **`PackageBoundaryGuardTests`** / **`AbstractionsContractGuardTests`** — architecture guards
  (project-reference boundaries; public-type counts of the Abstractions surface).
- **`MetaPackageDiRegistrationTests`** — the meta-package DI surface resolves.

## Known coverage gaps / follow-ups

- **End-to-end owner-stamp on extraction** is verified at the *pipeline* unit level
  (`MemoryExtractionPipelineTests`: `request.UserId` → resolution scope + persistence owner-stamp) but
  there is no *live* test that supplying a `userId` to `extract_and_persist` / `memory_extract_session`
  results in owner-stamped persisted nodes through the full ingest path (the stub extractors produce no
  entities, so a live test would need a real-ish extractor).
- **MCP `ContextResource`** owner-confinement is unit-covered (it routes through
  `IMemoryContextAssembler`, asserted via mock) but — unlike the Entity/Preference/Conversation list
  resources — has no live-Neo4j isolation test.

See `docs/Memory_Review_and_Implementation_Plan.md` (Part 0 = fix log, Part III = review annex) for the
isolation design and the full per-finding history.
