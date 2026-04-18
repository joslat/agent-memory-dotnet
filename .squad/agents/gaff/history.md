# Gaff — History

## Project Context
- **Project:** Agent Memory for .NET
- **User:** Jose Luis Latorre Millas
- **Stack:** .NET 9, C#, Neo4j, Neo4j.Driver
- **Role focus:** Neo4j persistence — repositories, Cypher, schema, indexes
- **Graph model:** Conversation, Message, Entity, Preference, Fact, ReasoningTrace, ReasoningStep, ToolCall nodes

## Recent Sprint (2026-07-18)

**Deep Review Sprint — Schema Parity Deep-Dive**

Comprehensive code-vs-docs verification and discrepancy audit:

1. **D-GAFF-1 (ToolCallStatus Enum Parity)** — Found 4-value .NET enum vs 6-value Python enum. Dead code in Neo4jToolCallRepository.cs:61 checks `$status IN ['error', 'timeout']` but Timeout not valid. Recommendation: Add Failure and Timeout values.

2. **D-GAFF-2 (Documentation Count Audit)** — Identified stale claims across 4 documents: "21 tools" (actual 28), "55+ test files" (actual 111+). Schema.md §2.5 phantom constraint contradicts §2.3. Recommendation: Single documentation sweep.

3. **D-GAFF-3 (Schema Index Parity)** — Python uses schema_id_idx, .NET uses schema_version_idx. Documented as intentional (Schema node deferred).

4. **Neo4j-MAF-Provider Verification** — Confirmed neo4j-maf-provider is retrieval-only (vector/fulltext/hybrid). AgentMemory is strict superset (full lifecycle). GraphRagAdapter integration pattern correct.

**Output:** docs/code-review-findings.md (503 lines)

**Key finding:** Documentation drift is widespread; validates Joi's D-DOC1 post-sprint audit process.

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

### Epic — Phase 2 Entity Resolution Persistence (2025-07-15)

**Objective:** Add Neo4j support for MENTIONS, SAME_AS, and entity merge operations needed by the LLM Extraction Pipeline.

**IEntityRepository changes (Abstractions):**
- Added `SearchByNameAsync(string name, string? type, CancellationToken)` — case-insensitive partial match on `name` and `canonicalName`; optional type filter
- Added `AddMentionAsync(string messageId, string entityId, CancellationToken)` — single MENTIONS MERGE
- Added `AddMentionsBatchAsync(string messageId, IReadOnlyList<string> entityIds, CancellationToken)` — UNWIND batch
- Added `AddSameAsRelationshipAsync(string e1, string e2, double confidence, string matchType, CancellationToken)` — MERGE + SET on SAME_AS rel
- Added `GetSameAsEntitiesAsync(string entityId, CancellationToken)` — returns `(Entity, double, string)` tuples
- Added `MergeEntitiesAsync(string sourceEntityId, string targetEntityId, CancellationToken)` — full merge Cypher with CALL subqueries

**Neo4jEntityRepository new methods:**
- `SearchByNameAsync` — uses `toLower(e.name) CONTAINS toLower($name)` query; branches on type param
- `AddMentionAsync` — single `MERGE (m)-[:MENTIONS]->(e)` after `MATCH` of both nodes
- `AddMentionsBatchAsync` — `UNWIND $entityIds AS eid` pattern for efficiency
- `AddSameAsRelationshipAsync` — `MERGE (e1)-[r:SAME_AS]->(e2) SET r.confidence...`
- `GetSameAsEntitiesAsync` — bidirectional `(e)-[r:SAME_AS]-(other)` query
- `MergeEntitiesAsync` — uses CALL subquery syntax (Neo4j 5+) to transfer MENTIONS and SAME_AS in one statement, then sets `mergedInto`, `mergedAt`, and `aliases`

**SchemaBootstrapper change:**
- Added `entity_merged_into_idx` property index on `Entity.mergedInto` for fast merged-entity lookups
- Statement count: 27 → 28

**Tests:** 15 new unit tests in `Neo4jEntityRepositoryExtensionsTests.cs` covering all 7 new methods.

**Key technical insight — NSubstitute + Neo4j.Driver generics:**
- `IResultCursor.ToListAsync()` is a static extension method and cannot be mocked with NSubstitute
- Mock `cursor.FetchAsync().Returns(Task.FromResult(false))` instead — the `ToListAsync()` extension calls this in a loop and returns `[]` when false
- `ReadAsync<T>` type inference: `.ToList()` inside the lambda makes T = `List<Entity>`, NOT `IReadOnlyList<Entity>` — mock must match `Func<IAsyncQueryRunner, Task<List<Entity>>>` exactly

**Pre-existing issues (not my changes):**
- `FuzzySharp` NuGet package missing from `Neo4j.AgentMemory.Core` — causes 1 test failure in `FuzzyMatchEntityMatcherTests` (Phase 2 scaffold from another agent)

**Build status (unit tests only):** `dotnet test tests/Neo4j.AgentMemory.Tests.Unit` → 162 passed, 1 failed (pre-existing FuzzySharp). My 15 new tests: all passed.


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

### Epic — Cross-Memory Relationships & Batch Operations (session: cross-memory-fix)

**Objective:** Achieve full relationship coverage. Audit found only 6/15 relationship types implemented (40%). Fixed all missing gaps.

**Interfaces updated (Abstractions):**
- `IPreferenceRepository` — added `CreateConversationPreferenceRelationshipAsync`
- `IFactRepository` — added `CreateConversationFactRelationshipAsync`
- `IReasoningTraceRepository` — added `CreateInitiatedByRelationshipAsync`, `CreateConversationTraceRelationshipsAsync`
- `IToolCallRepository` — added `CreateTriggeredByRelationshipAsync`
- NOTE: `DeleteAsync`, `CreateAboutRelationshipAsync`, `CreateExtractedFromRelationshipAsync`, `UpsertBatchAsync` already existed as stubs (added in a prior session); I replaced those stubs with working implementations.

**Neo4j repository implementations — new relationships:**

| Relationship | Type | Where Created |
|---|---|---|
| `FIRST_MESSAGE` | Conversation→Message | `Neo4jMessageRepository.AddAsync` — MERGE after CREATE, WHERE NOT EXISTS |
| `EXTRACTED_FROM` | Entity/Fact/Preference→Message | Auto-created in `UpsertAsync` + `UpsertBatchAsync` via UNWIND over `SourceMessageIds`; explicit `CreateExtractedFromRelationshipAsync` also available |
| `ABOUT` | Preference/Fact→Entity | Explicit `CreateAboutRelationshipAsync` in Preference and Fact repos |
| `INITIATED_BY` | ReasoningTrace→Message | Explicit `CreateInitiatedByRelationshipAsync` in trace repo |
| `HAS_TRACE` + `IN_SESSION` | Conversation↔ReasoningTrace | Both created together in `CreateConversationTraceRelationshipsAsync` (single Cypher) |
| `TRIGGERED_BY` | ToolCall→Message | Explicit `CreateTriggeredByRelationshipAsync` in tool call repo (was a no-op stub, now real Cypher) |
| `CALLS` | ToolCall→Tool | Auto-created in `Neo4jToolCallRepository.AddAsync` — MERGE `:Tool {name}` + increment `totalCalls` |
| `HAS_PREFERENCE` | Conversation→Preference | Explicit `CreateConversationPreferenceRelationshipAsync` |
| `HAS_FACT` | Conversation→Fact | Explicit `CreateConversationFactRelationshipAsync` |

**Batch operations (Task 2):**
- `Neo4jEntityRepository.UpsertBatchAsync` — UNWIND MERGE for all entity fields; separate loop for embeddings; auto-creates EXTRACTED_FROM per entity
- `Neo4jFactRepository.UpsertBatchAsync` — same UNWIND pattern for facts

**Key patterns:**
- FIRST_MESSAGE uses `WHERE NOT EXISTS { MATCH (conv)-[:FIRST_MESSAGE]->() }` before MERGE — idempotent
- CALLS creates `:Tool {name}` node on first encounter and increments `totalCalls` counter
- HAS_TRACE + IN_SESSION created in a SINGLE Cypher statement for atomicity
- All explicit methods use MERGE (not CREATE) to be idempotent, except INITIATED_BY and TRIGGERED_BY which use MERGE too
- EXTRACTED_FROM auto-wires provenance during UpsertAsync when SourceMessageIds is non-empty

**Pre-existing stubs found:** Several methods (`CreateTriggeredByRelationshipAsync`, `CreateInitiatedByRelationshipAsync`, `CreateConversationTraceRelationshipsAsync`, `UpsertBatchAsync` for entities) already existed as TODO stubs with `return Task.CompletedTask`. Always check for stubs before adding new methods to avoid CS0111 duplicate errors.

**Build status:** `dotnet build` → 0 errors, 0 warnings. `dotnet test tests/Neo4j.AgentMemory.Tests.Unit` → 419 passed, 0 failed.

### Epic — Write-Layer Gaps + Fact Deduplication + Conversation Title + Re-embedding (2025-07-15)

**Objective:** Close write-layer gaps: Entity/Fact DeleteAsync, fact deduplication via SPO-triple MERGE, Conversation title field, and embedding invalidation after entity merge.

**Interface changes (Abstractions):**
- `IEntityRepository` — added `DeleteAsync(string entityId)` → returns `bool`
- `IFactRepository` — added `DeleteAsync(string factId)` → returns `bool`, `FindByTripleAsync(subject, predicate, object)` → returns `Fact?`
- `Conversation` domain model — added `Title` property (nullable `string?`)

**Neo4j repository implementations:**
- `Neo4jEntityRepository.DeleteAsync` — `MATCH (e:Entity {id: $entityId}) DETACH DELETE e RETURN count(e) > 0 AS deleted`
- `Neo4jFactRepository.DeleteAsync` — same DETACH DELETE pattern for Fact nodes
- `Neo4jFactRepository.FindByTripleAsync` — case-insensitive match via `toLower()` on subject/predicate/object, `LIMIT 1`
- `Neo4jFactRepository.UpsertAsync` — changed MERGE key from `{id: $id}` to `{subject: $subject, predicate: $predicate, object: $object}` — prevents duplicate SPO triples at the Cypher level; added `updated_at` on ON MATCH SET, `id` on ON CREATE SET
- `Neo4jConversationRepository` — added `title` property to both ON CREATE SET and ON MATCH SET; reads via `TryGetValue("title")`
- `Neo4jEntityRepository.MergeEntitiesAsync` — added `SET target.embedding = null` after alias merge to trigger re-embedding (G9 requirement)

**FakeResultCursor test helper (new):**
- Created `tests/.../TestHelpers/FakeResultCursor.cs` — implements `IResultCursor` with proper `IAsyncEnumerable<IRecord>` iteration
- Required because Neo4j.Driver 6.0 `SingleAsync`/`ToListAsync` extension methods use `GetAsyncEnumerator()` + `MoveNextAsync()` internally, which NSubstitute cannot mock on `Substitute.For<IResultCursor>()`

**Key testing insight — RunAsync overload resolution:**
- `IAsyncQueryRunner` has separate `RunAsync(string, object)` and `RunAsync(string, IDictionary<string, object>)` overloads
- `Dictionary<string, object?>` params resolve to the IDictionary overload; anonymous objects resolve to the object overload
- Tests that mock `RunAsync(Arg.Any<string>(), Arg.Any<object>())` miss calls with Dictionary params — must mock BOTH overloads

**Tests added (34 new):**
- `Neo4jEntityRepositoryDeleteTests` — 6 tests (Cypher, params, true/false return, transaction type)
- `Neo4jFactRepositoryDeleteTests` — 6 tests (same pattern)
- `Neo4jFactRepositoryDeduplicationTests` — 11 tests (FindByTripleAsync Cypher/params/null return + UpsertAsync MERGE on SPO, ON CREATE/MATCH, updatedAt)
- `Neo4jConversationRepositoryTitleTests` — 9 tests (Upsert includes title, GetById returns title, null handling, domain model)
- `Neo4jEntityRepositoryExtensionsTests` — 2 new tests (embedding cleared after merge, ordering verified)

**Build status:** `dotnet build src/Neo4j.AgentMemory.Abstractions && dotnet build src/Neo4j.AgentMemory.Neo4j` → 0 errors. `dotnet test tests/Neo4j.AgentMemory.Tests.Unit` → 823 passed, 0 failed.

### Epic G10 — Entity Index Refresh Hook After Merge (2025-07-16)

**Objective:** Ensure fulltext index stays current after entity merge by absorbing source aliases/description into target and providing a dedicated `RefreshEntitySearchFieldsAsync` utility.

**Interface change (Abstractions):**
- `IEntityRepository` — added `RefreshEntitySearchFieldsAsync(string entityId, CancellationToken)` — no-throw guarantee if entity missing; triggers fulltext re-index via property update

**Neo4jEntityRepository changes:**
- `MergeEntitiesAsync` Cypher extended:
  - Uses `WITH` to compute `mergedAliases` = existing aliases + source.name + source.aliases (dedup via `WHERE NOT x IN coalesce(target.aliases, [])` guard)
  - Merges `source.description` into `target.description` conditionally (CASE: NULL / already-contained / concat with space)
  - Sets `target.updated_at = datetime()` in the same SET block (all 4 target properties in one SET)
  - Calls `RefreshEntitySearchFieldsAsync(targetEntityId)` after the merge Cypher completes
- `RefreshEntitySearchFieldsAsync` — new public method:
  - Cypher: `MATCH (e:Entity {id: $entityId}) SET e.updated_at = $updatedAt, e.aliases = [x IN coalesce(e.aliases, []) WHERE x IS NOT NULL AND size(toString(x)) > 0] RETURN e`
  - Strips null/empty entries from aliases, stamps current UTC timestamp → fulltext auto-reindexes

**Key technical insight — Cypher spacing matters for unit tests:**
- Tests use `cypher.Should().Contain("target.embedding = null")` (single space around `=`)
- Multi-space alignment (`target.embedding   = null`) causes `IndexOf` to return -1
- Always use single-space `=` in SET clauses to keep test string-matching reliable

**Tests added (11 new in Neo4jEntityRepositoryRefreshTests.cs):**
- `RefreshEntitySearchFieldsAsync_SendsCorrectCypher`
- `RefreshEntitySearchFieldsAsync_PassesEntityIdParameter`
- `RefreshEntitySearchFieldsAsync_SetsUpdatedAtTimestamp`
- `RefreshEntitySearchFieldsAsync_UsesWriteTransaction`
- `RefreshEntitySearchFieldsAsync_DoesNotThrow_WhenEntityMissing`
- `RefreshEntitySearchFieldsAsync_DeduplicatesAliasesInCypher`
- `MergeEntitiesAsync_CallsRefreshAfterMerge`
- `MergeEntitiesAsync_RefreshUsesTargetEntityId`
- `MergeEntitiesAsync_CypherAbsorbsSourceAliases`
- `MergeEntitiesAsync_CypherMergesDescription`
- `MergeEntitiesAsync_SetsUpdatedAtOnTarget`

**Existing test updated:** `MergeEntitiesAsync_SendsCorrectCypher` — changed `HaveCount(1)` → `HaveCountGreaterThanOrEqualTo(1)` to accommodate the second RunAsync call from the refresh hook.

### CypherBuilder — Dynamic Query Composition (2026-07-19)

**Objective:** Introduce a lightweight, composable query builder to eliminate fragile string interpolation in dynamic Cypher queries.

**New class:** `src/Neo4j.AgentMemory.Neo4j/Infrastructure/CypherBuilder.cs`
- Immutable: every method returns a new instance (copy-on-write list); safe to share across threads
- Static factories: `Match(pattern)`, `Call(procedure)`
- Instance methods: `OptionalMatch`, `With`, `Unwind`, `Set`, `Return`, `Where`, `And`, `Or`, `OrderBy`, `Skip`, `Limit`, `WithVectorSearch`, `AndRawFragment`
- `Where`/`And`/`Or` are smart: first condition emits `WHERE`, subsequent emit `AND`/`OR`
- `when:` parameter on conditional methods gates inclusion without external if-statements
- `WithVectorSearch(indexName, embeddingParam, nodeAlias, topK)` — emits `CALL db.index.vector.queryNodes + YIELD`; topK is embedded as an integer literal
- `AndRawFragment(fragment)` — appends pre-formatted AND/OR lines from MetadataFilterBuilder verbatim

**Refactored queries (2 dynamic methods updated):**
1. `EntityQueries.SearchByNameFiltered(string? type)` — removed `SearchByName` and `SearchByNameWithType` const literals; replaced with single CypherBuilder call using `Where("e.type = $type", when: type is not null)`
2. `MessageQueries.SearchByVector(bool hasSessionFilter, string? metadataFilterFragment, int topK)` — replaced string interpolation approach; `topK` now embedded as literal; `$limit` parameter removed from query and parameter dict

**Repository change:** `Neo4jMessageRepository.SearchByVectorAsync` — updated call site; removed `parameters["limit"]` entry (topK now in query literal); replaced `sessionFilter` string conditional with bool.

**CypherQuerySnapshot:** Regenerated after removing 2 consts; `ExpectedQueryCount` updated 139 → 137.

**Tests added:** 26 unit tests in `tests/Neo4j.AgentMemory.Tests.Unit/Infrastructure/CypherBuilderTests.cs`
- Basic Match/Return, Call, With, Unwind, Set, OptionalMatch
- WHERE smart promotion (first = WHERE, subsequent = AND)
- And/Or with and without prior WHERE
- Conditional when: true vs when: false  
- AllConditionsFalse_ProducesBareMatchReturn
- NoWhereConditions_OmitsWhereKeyword
- OrderBy/Limit/Skip conditional inclusion
- WithVectorSearch CALL+YIELD generation, topK as literal
- Session filter conditional on vector search
- AndRawFragment verbatim append, empty/null/when:false skip
- Immutability (branching produces independent instances)
- Build() exact string snapshot with newline separators
- EmptyBuilder returns empty string

**Existing test fixed:** `Neo4jEntityRepositoryExtensionsTests.SearchByNameAsync_WithType_IncludesTypeConstraint` — updated assertion from `{type: $type}` (old property-map pattern) to `e.type = $type` (new WHERE clause pattern).

**Build:** 0 errors, 0 warnings. **Tests:** 2077 unit tests passed, 0 failed.

**Build status:** `dotnet test tests/Neo4j.AgentMemory.Tests.Unit` → 847 passed, 0 failed.

### G12 — Diffbot Enrichment Provider (2026-04-14)

**Task:** Port Python nrichment/diffbot.py to .NET as DiffbotEnrichmentService.

**Files created/modified:**
- src/Neo4j.AgentMemory.Abstractions/Domain/Enrichment/EnrichmentStatus.cs — new enum (Success, NotFound, Skipped, RateLimited, Error)
- src/Neo4j.AgentMemory.Abstractions/Domain/Enrichment/RelatedEntity.cs — new record with Name, Relation, DiffbotUri
- src/Neo4j.AgentMemory.Abstractions/Domain/Enrichment/EnrichmentResult.cs — extended with optional Status, EntityType, Confidence, ErrorMessage, SourceUrl, DiffbotUri, Images, RelatedEntities (all backward-compatible defaults)
- src/Neo4j.AgentMemory.Abstractions/Options/DiffbotEnrichmentOptions.cs — new options record (ApiKey, RateLimitSeconds, Timeout, BaseUrl)
- src/Neo4j.AgentMemory.Enrichment/Enrichment/DiffbotEnrichmentService.cs — full service implementation
- src/Neo4j.AgentMemory.Enrichment/ServiceCollectionExtensions.cs — added AddDiffbotEnrichment extension
- 	ests/Neo4j.AgentMemory.Tests.Unit/Enrichment/DiffbotEnrichmentServiceTests.cs — 15 unit tests, all passing

**Key design decisions:**
- DiffbotEnrichmentOptions uses set (not init) properties to support Action<T> DI pattern; equired dropped in favour of default empty string for ApiKey.
- Returns non-null EnrichmentResult with Status field even on error/not-found (richer error surface than Wikimedia which returns null). Backward-compatible since Wikimedia callers only check for null.
- Rate limiting via SemaphoreSlim + Task.Delay on the service instance (matches Python's syncio.Lock pattern).
- Type-specific metadata (Person/Org/Location fields) stored as JSON-serialized strings in the existing Properties dictionary.
- The DiffbotEnrichmentService accepts HttpClient directly (typed client pattern) + DiffbotEnrichmentOptions singleton.
- parent relation field handled as both array and single-object via JsonArray clone.

**Build status:** dotnet build src/Neo4j.AgentMemory.Enrichment → 0 errors, 0 warnings.
**Test status:** 15/15 Diffbot tests pass; 7/7 Wikimedia tests pass (no regression).

### Deep Verification Sprint — Code-vs-Documentation Audit (2025-07-24)

**Objective:** Full code-vs-documentation verification, schema parity deep-dive, MAF-provider comparison, and architecture map.

**Key findings (written to `docs/code-review-findings.md`):**

1. **SchemaBootstrapper.cs verified accurate:** 10 constraints, 3 fulltext, 6 vector, 15 property/point indexes (12 regular + 2 schema + 1 point). Total: 34 schema statements.
2. **Schema parity ~99%** confirmed: All 9 Python constraints present, all 5 Python vector indexes present, all 10 Python property indexes present, all 15 Python relationship types present. .NET extends with extras (not subtracts).
3. **ToolCallStatus parity gap discovered:** Python has 6 values (pending, success, failure, error, timeout, cancelled), .NET enum has only 4 (missing Failure, Timeout). The Tool aggregate stats Cypher references 'timeout' but this branch can never trigger.
4. **Schema index difference:** Python indexes Schema.id (`schema_id_idx`), .NET indexes Schema.version (`schema_version_idx`).
5. **MCP tool count grew from 21 → 28** without docs being updated (README, feature-record, python-dotnet-comparison all stale).
6. **Test file count grew from 55+ → 111+** test class files without docs being updated.
7. **docs/schema.md** has a phantom `relationship_id` constraint in section 2.5 that contradicts section 2.3 (which correctly says it was removed).
8. **neo4j-maf-provider** is read-only search; our implementation wraps it via GraphRagAdapter ProjectReference and adds full memory lifecycle.
9. **1058 unit tests pass, 0 failures** — verified by running `dotnet test`.

**Build status:** `dotnet test tests/Neo4j.AgentMemory.Tests.Unit` → 1058 passed, 0 failed.

### Wave 2 — Findings 6 + 7: Thresholds Parameterization + Azure API Cache (2026-07-18)

**Objective:** Wave 2 refactoring — two concurrent findings.

**Finding 6: Hardcoded confidence thresholds made configurable.**

- `ExtractionOptions` — added `StrongPatternConfidence` (default 0.95) and `RegexMatchConfidence` (default 0.85)
- `AzureLanguageOptions` — added `KeyPhraseFactConfidence` (default 0.7) and `LinkedEntityFactConfidence` (default 0.8)
- `PatternBasedPreferenceDetector` — replaced `private const` values with `_options.StrongPatternConfidence` / `_options.RegexMatchConfidence`; added `IOptions<ExtractionOptions>` constructor; kept parameterless ctor (uses `Options.Create(new ExtractionOptions())`) for backward compat with tests that use `new()`
- `AzureLanguageFactExtractor` — replaced hardcoded `0.7` / `0.8` literals with `_options.KeyPhraseFactConfidence` / `_options.LinkedEntityFactConfidence`

**Finding 7: Azure API deduplification via shared extraction context.**

- Created `Internal/AzureExtractionContext` (sealed, internal) — `ConcurrentDictionary<string, IReadOnlyList<AzureRecognizedEntity>>` cache; `GetOrRecognizeEntitiesAsync` checks cache before calling client
- Registered as `TryAddScoped<AzureExtractionContext>()` in `ServiceCollectionExtensions`
- `AzureLanguageEntityExtractor` — added `AzureExtractionContext` constructor param, replaced direct client call with `_context.GetOrRecognizeEntitiesAsync`
- `AzureLanguageRelationshipExtractor` — same change; also removed now-unnecessary `.ToList()` call (context returns `IReadOnlyList<T>` already castable to List for indexing)
- Updated tests: `AzureLanguageEntityExtractorTests` and `AzureLanguageRelationshipExtractorTests` — `CreateSut` now passes `new AzureExtractionContext()` as 4th param

**Behavior preserved:** All default values identical; 1059 tests pass (1 more than baseline from the new parameterless-ctor code path being exercised).

**Build status:** `dotnet build` → 0 errors, 0 warnings. `dotnet test` (unit only) → 1059 passed, 0 failed.


## Learnings

### 2026-07-18: N+1 Pagination Pattern Implementation

**Task:** Implement HotChocolate-inspired N+1 pagination to halve DB round-trips in batch back-fill operations.

**Files Created:**
- `src/Neo4j.AgentMemory.Abstractions/Domain/PagedResult.cs` — `PagedResult<T>` record with `Items` + `HasNextPage`
- `src/Neo4j.AgentMemory.Neo4j/Infrastructure/PaginationHelper.cs` — `internal static PaginationHelper.ApplyPagination<T>(List<T>, int)`
- `tests/.../Infrastructure/PaginationHelperTests.cs` — 7 tests for the helper itself
- `tests/.../Repositories/PaginatedRepositoryTests.cs` — 6 tests for Fact+Preference paginated repos

**Files Modified:**
- `IEntityRepository.cs`, `IFactRepository.cs`, `IPreferenceRepository.cs` — `GetPageWithoutEmbeddingAsync` now returns `Task<PagedResult<T>>`
- `Neo4jEntityRepository.cs`, `Neo4jFactRepository.cs`, `Neo4jPreferenceRepository.cs` — request `limit+1` from DB, call `PaginationHelper.ApplyPagination`
- `MemoryService.cs` — backfill loops changed from `while (page.Count == batchSize)` to `while (page.HasNextPage)`
- `MemoryServiceBatchTests.cs` — mocks updated to return `PagedResult<T>`
- `Neo4jEntityRepositoryLocationTests.cs` — split read capture into `CreateEntityListReadCapture` (returns `List<T>`) and `CreatePagedEntityReadCapture` (returns `PagedResult<T>`)

**Key Design Decisions:**
1. `PagedResult<T>` lives in Abstractions so all layers can use it (interface contracts + Core callers)
2. `PaginationHelper` is `internal` in the Neo4j package — implementation detail, not public surface
3. Only applied to `GetPageWithoutEmbeddingAsync` batch methods — the clearest paginated use case with a real caller that benefits from `HasNextPage` (eliminates spurious empty-page call when total is exact multiple of batchSize)
4. Vector search / top-K methods (`SearchByVectorAsync`, `SearchByLocationAsync`, etc.) intentionally NOT changed — they are top-K queries, not cursor-based pages; callers don't need `HasNextPage`

**Result:** 1,451 unit tests, 0 failures (+13 new tests)
