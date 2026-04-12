# Holden — History

## Project Context
- **Project:** Agent Memory for .NET
- **User:** Jose Luis Latorre Millas
- **Stack:** xUnit, FluentAssertions, Testcontainers, NSubstitute, Docker
- **Role focus:** Test harness, unit/integration/E2E tests, quality gates
- **Test strategy:** Tests alongside implementation, golden datasets, Testcontainers for Neo4j

## Learnings

- `Testcontainers.Neo4j` 4.11.0 `Neo4jBuilder` accepts image in the constructor (`new Neo4jBuilder("neo4j:5.26")`); there is no `WithPassword` method — set password via `WithEnvironment("NEO4J_AUTH", "neo4j/<password>")`.
- `Neo4j.Driver` and `FluentAssertions` both expose `.As<T>()` extension methods causing CS0121 ambiguity — use `global::Neo4j.Driver.ValueExtensions.As<T>(value)` to be explicit.
- Domain models live in `Neo4j.AgentMemory.Abstractions.Domain` namespace. Avoid using fully-qualified names starting with `Neo4j.` at the call site to prevent conflicts with the `Neo4j.Driver` package namespace.
- The `Neo4j.AgentMemory.Neo4j` project (infrastructure) had no `.cs` files when Epic 9 was run — the test harness is built ahead of that implementation.
- `IIdGenerator` uses `GenerateId()` (not `NewId()`); `IEmbeddingProvider` uses `GenerateEmbeddingAsync(string, CancellationToken)` (not `GenerateAsync()`). Always verify abstract method names against the interface before mocking.
- `ReasoningMemoryService` does NOT take `IOptions<ReasoningMemoryOptions>` in its constructor (Roy's Wave 4 implementation). Budget enforcement logic lives in `MemoryContextAssembler`, not in individual services.
- `MemoryService` constructor order: `(shortTerm, assembler, extraction, IOptions<MemoryOptions>, clock, idGenerator, logger)` — options come before clock/idGenerator.
- `MemoryContextAssembler` uses character-based estimation (`EstimateItemChars`) for budget enforcement: messages use `Content.Length`, facts use `Subject+Predicate+Object+4`, entities use `Name+Description+10`, traces use `Task+Outcome+10`.
- For `TruncationStrategy.OldestFirst`, items are sorted descending by timestamp THEN `FitWithinBudget` removes from the end of each list in round-robin (facts first, then entities, relevant messages, traces, preferences, recent messages).

## Work Log

### 2025-01-28 — Epic 9: Test Harness Bootstrap

**Completed:**
- Created `tests/Neo4j.AgentMemory.Tests.Integration/Neo4jTestFixture.cs` — shared `IAsyncLifetime` fixture wrapping a `Neo4jContainer` (Testcontainers)
- Created `tests/Neo4j.AgentMemory.Tests.Integration/Neo4jTestCollection.cs` — xUnit `[CollectionDefinition("Neo4j")]` so all integration tests share one container
- Created `tests/Neo4j.AgentMemory.Tests.Integration/TestDataSeeders.cs` — factory methods for all domain types: `Conversation`, `Message`, `Entity`, `Fact`, `Preference`, `Relationship`, `ReasoningTrace`, `ReasoningStep`, `ToolCall`
- Created `tests/Neo4j.AgentMemory.Tests.Integration/IntegrationTestBase.cs` — abstract base with `[Collection("Neo4j")]`, `Fixture` property, `CreateDriver()` and `RunCypherAsync()` helpers
- Created `tests/Neo4j.AgentMemory.Tests.Integration/Neo4jConnectivityTests.cs` — smoke tests: `CanConnectToNeo4j`, `CanCreateAndQueryNode`
- Created `tests/Neo4j.AgentMemory.Tests.Unit/TestHelpers/MockFactory.cs` — `CreateFixedClock`, `CreateSequentialIdGenerator`, `CreateStubEmbeddingProvider` using NSubstitute
- Added explicit `Neo4j.Driver Version="6.0.0"` reference to integration test project
- `dotnet build` — **Build succeeded** (0 errors)

### 2025-01-28 — Wave 4: Core Service Unit Tests

**Completed:**
- Created `tests/Neo4j.AgentMemory.Tests.Unit/Services/` directory
- Created `ShortTermMemoryServiceTests.cs` — 12 tests covering conversation creation, embedding generation/skipping, message persistence, limit capping, score stripping, session clearing
- Created `LongTermMemoryServiceTests.cs` — 14 tests covering entity/fact/preference/relationship add+search, embedding conditional generation, score stripping for all search methods
- Created `ReasoningMemoryServiceTests.cs` — 10 tests covering trace start, step add, tool call record, trace completion, parallel GetTraceWithSteps, list and search with score stripping
- Created `MemoryContextAssemblerTests.cs` — 10 tests covering embedding generation, all-layer retrieval, GraphRAG enable/disable/null, assembled timestamp, budget enforcement (OldestFirst + LowestScoreFirst), token count estimation
- Created `MemoryServiceTests.cs` — 5 tests covering recall wrapping, message creation via IIdGenerator+IClock, batch delegate, extraction pipeline delegate, session clear delegate
- **Total unit tests: 85 passing (0 failures)**
