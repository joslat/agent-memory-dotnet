# Gaff — History

## Project Context
- **Project:** Agent Memory for .NET
- **User:** Jose Luis Latorre Millas
- **Stack:** .NET 9, C#, Neo4j, Neo4j.Driver
- **Role focus:** Neo4j persistence — repositories, Cypher, schema, indexes
- **Graph model:** Conversation, Message, Entity, Preference, Fact, ReasoningTrace, ReasoningStep, ToolCall nodes

## Learnings

### Epic 1 — Foundation Bootstrap (2025-01-27)

**NuGet package versions resolved:**
- `Neo4j.Driver` → **6.0.0**
- `Microsoft.Extensions.DependencyInjection.Abstractions` → **10.0.5**
- `Microsoft.Extensions.Options` → **10.0.5**
- `Microsoft.Extensions.Logging.Abstractions` → **10.0.5**
- `FluentAssertions` → **8.9.0**
- `NSubstitute` → **5.3.0**
- `Testcontainers.Neo4j` → **4.11.0**

**Project structure decisions:**
- 3 classlib src projects + 2 xunit test projects under `tests/`
- `Directory.Build.props` at solution root: `TreatWarningsAsErrors=true` scoped to `src/` only (not `tests/`)
- Roy had already scaffolded domain `.cs` files into Abstractions; Options classes were missing — created all 6 Option types to unblock build
- `deploy/docker-compose.dev.yml` (port 7687) and `deploy/docker-compose.test.yml` (port 7688, tmpfs data) created
- `.gitkeep` files placed in all empty source directories

**Build status:** `dotnet build` → 0 errors, 0 warnings. `dotnet test` → passes (no tests yet).

**Key insight:** Always check for pre-existing `.cs` files before first build — Roy had deposited domain models that depended on Options types not yet created.

### Epic 3 — Neo4j Infrastructure Layer (2025-07-14)

**All 12 files created in `src/Neo4j.AgentMemory.Neo4j/Infrastructure/`:**
- `Neo4jOptions.cs` — connection config (URI, credentials, pool size, acquisition timeout, encryption)
- `INeo4jDriverFactory.cs` + `Neo4jDriverFactory.cs` — singleton IDriver lifecycle; uses `GraphDatabase.Driver()` + `AuthTokens.Basic()` + `EncryptionLevel`
- `INeo4jSessionFactory.cs` + `Neo4jSessionFactory.cs` — creates `IAsyncSession` with database name and access mode
- `INeo4jTransactionRunner.cs` + `Neo4jTransactionRunner.cs` — `ReadAsync<T>` / `WriteAsync<T>` wrapper; repos never see IAsyncSession
- `ISchemaBootstrapper.cs` + `SchemaBootstrapper.cs` — idempotent; 9 `IF NOT EXISTS` constraints + 3 fulltext indexes
- `IMigrationRunner.cs` + `MigrationRunner.cs` — loads `.cypher` files from `Schema/Migrations/` sorted by name; tracks applied in `(:Migration {version, appliedAtUtc})`
- `ServiceCollectionExtensions.cs` — `AddNeo4jAgentMemory(IServiceCollection, Action<Neo4jOptions>)` extension

**API facts for Neo4j.Driver 6.0.0:**
- `IDriver` implements `IDisposable` + `IAsyncDisposable`
- `IAsyncSession.ExecuteReadAsync(Func<IAsyncQueryRunner, Task<T>> work)` — no action param needed
- `IDriver.AsyncSession(Action<SessionConfigBuilder> action)` — session creation
- `SessionConfigBuilder.WithDatabase()` + `WithDefaultAccessMode()` — session configuration
- `ConfigBuilder.WithEncryptionLevel(EncryptionLevel)` — use `EncryptionLevel.None` or `EncryptionLevel.Encrypted`

**Integration test fixes applied:**
- `Testcontainers.Neo4j` 4.11.0: `Neo4jBuilder("image")` constructor required; no `WithPassword` method — use `WithEnvironment("NEO4J_AUTH", "neo4j/pass")`
- Namespace collision: adding `Neo4j.AgentMemory.Neo4j.Infrastructure` namespace makes compiler resolve `Neo4j.*` calls ambiguously — fix with `global::` prefix or use `ToolCallStatus` directly when already imported via `using`
- FluentAssertions `As<T>` vs `Neo4j.Driver.ValueExtensions.As<T>` ambiguity — fixed with `global::Neo4j.Driver.ValueExtensions.As<T>()` static invocation

**Build status:** `dotnet build` → 0 errors, 0 warnings (all 5 projects).

### Epic 4 — Vector & Property Indexes (2025-07-14)

**Problem:** SchemaBootstrapper was missing 5 vector indexes and 9 property indexes required for production-quality search performance. Any `SearchByVectorAsync` call would have fallen back to full node scans.

**Changes made:**
- `Neo4jOptions.cs` — added `EmbeddingDimensions` (default: 1536); controls vector index dimensionality at configuration time
- `SchemaBootstrapper.cs` — injected `IOptions<Neo4jOptions>`; split index arrays into `FulltextIndexes`, `PropertyIndexes` (static), and `_vectorIndexes` (built in constructor from options); added `public static BuildVectorIndexes(int)` helper for testability; bootstrap log now reports all 4 categories
- `SchemaBootstrapperTests.cs` (new) — 12 unit tests covering constraint count, fulltext count, vector count (5), property count (9), cosine function, embedding property target, IF NOT EXISTS idempotency, configurable dimensions, and default option value
- Unit test `.csproj` — added `ProjectReference` to `Neo4j.AgentMemory.Neo4j`

**Vector index targets (all cosine, configurable dimensions):**
- `message_embedding_idx` → Message.embedding
- `entity_embedding_idx` → Entity.embedding
- `preference_embedding_idx` → Preference.embedding
- `fact_embedding_idx` → Fact.embedding
- `reasoning_step_embedding_idx` → ReasoningStep.embedding

**Property index targets:**
- Message: `sessionId`, `timestamp`
- Entity: `type`, `name`
- Fact: `category` (future property, harmless to index now)
- Preference: `category`
- ReasoningTrace: `sessionId`
- ReasoningStep: `timestamp` (future property)
- ToolCall: `status`

**Key decision:** `BuildVectorIndexes` made `public static` (not `internal`) so unit tests in a separate assembly can call it directly without `InternalsVisibleTo` setup.

**Build status:** `dotnet build` → 0 errors, 0 warnings. `dotnet test tests/Neo4j.AgentMemory.Tests.Unit` → 34 passed, 0 failed.

### Epics 4, 5, 6 — All 9 Neo4j Repository Implementations (2025-07-14)

**9 repository classes created** in `src/Neo4j.AgentMemory.Neo4j/Repositories/`:
- `Neo4jConversationRepository` — MERGE upsert, session queries, DETACH DELETE
- `Neo4jMessageRepository` — CREATE + linked list (HAS_MESSAGE, NEXT_MESSAGE), UNWIND batch, vector search with optional sessionId filter
- `Neo4jEntityRepository` — MERGE upsert preserving aliases as Neo4j list, vector search, name+alias lookup
- `Neo4jFactRepository` — MERGE upsert with optional date fields, vector search
- `Neo4jPreferenceRepository` — MERGE upsert, category queries, vector search
- `Neo4jRelationshipRepository` — MERGE on RELATES_TO relationship (stores sourceEntityId/targetEntityId as rel properties for easy mapping back), bidirectional entity queries
- `Neo4jReasoningTraceRepository` — CREATE/UPDATE pattern, session listing, task vector search with optional success filter
- `Neo4jReasoningStepRepository` — CREATE + HAS_STEP link, trace ordering by stepNumber
- `Neo4jToolCallRepository` — CREATE + USED_TOOL link, ToolCallStatus stored as string

**SchemaBootstrapper updated:** Added `task_embedding_idx` vector index targeting `ReasoningTrace.taskEmbedding` — now 6 total vector indexes.

**ServiceCollectionExtensions updated:** All 9 repositories registered via `TryAddTransient`.

**Unit test updates:** SchemaBootstrapper tests updated: total statements 26→27, vector count 5→6, `AllIndexesTargetEmbeddingProperty` test updated to use regex matching `(embedding|taskEmbedding)`.

**Key implementation patterns:**
- Metadata (IReadOnlyDictionary) serialized to JSON string via System.Text.Json (Neo4j doesn't support Map properties)
- Arrays (aliases, sourceMessageIds) stored as Neo4j native lists; read back via `.As<IList<object>>()` + `.Select(v => v.ToString()!)`
- Embeddings stored in a separate `SET` query after the node CREATE/MERGE (avoids null parameter complexity)
- DateTimeOffset stored as ISO 8601 "O" format strings, parsed back with `DateTimeStyles.RoundtripKind`
- Nullable node properties accessed via `node.Properties.TryGetValue()` — avoids KeyNotFoundException
- `RELATES_TO` relationships store both `sourceEntityId` and `targetEntityId` as relationship properties for mapping without needing to traverse source/target nodes on read
- IRelationship has `.Properties` dictionary same as INode — same mapping pattern works
- ToolCallStatus enum round-tripped via `.ToString()` / `Enum.Parse<ToolCallStatus>()`
- ReasoningTrace `success` stored as nullable bool in Neo4j; read carefully with `TryGetValue` + `As<bool?>()`

**Build status:** `dotnet build` → 0 errors, 0 warnings. `dotnet test tests/Neo4j.AgentMemory.Tests.Unit` → 34 passed, 0 failed.
