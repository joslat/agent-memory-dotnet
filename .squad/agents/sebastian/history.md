# Sebastian — History

## Project Context
- **Project:** Agent Memory for .NET
- **User:** Jose Luis Latorre Millas
- **Stack:** .NET 9, C#, Neo4j.AgentFramework.GraphRAG
- **Role focus:** GraphRAG adapter — wrap existing provider, blended retrieval
- **Reference:** /Neo4j/neo4j-maf-provider/dotnet/ for existing GraphRAG provider

## Learnings

### 2025-07-12 — neo4j-maf-provider Analysis & Read/Write Split

**What neo4j-maf-provider IS:**
- Entirely read-only. Zero write operations in any class.
- 10 classes: `Neo4jContextProvider`, `Neo4jContextProviderOptions`, `Neo4jSettings`, `IndexType`, `StopWords`, and retrieval layer (`IRetriever`, `VectorRetriever`, `FulltextRetriever`, `HybridRetriever`, `RetrieverResult`/`RetrieverResultItem`).
- Its only function: given query text, embed it, search a Neo4j knowledge graph index (vector/fulltext/hybrid), return ranked string results.
- MAF-coupled only at the top layer (`Neo4jContextProvider` extends `AIContextProvider` from `Microsoft.Agents.AI`). The retrieval layer (`IRetriever` and implementations) has zero MAF dependency — only `Neo4j.Driver` + `Microsoft.Extensions.AI`.

**How we used it (class-by-class):**

| Reference Class | Our Treatment | Reason |
|---|---|---|
| `Neo4jContextProvider` | **Not used** | MAF-specific (`AIContextProvider` base). We have `Neo4jMemoryContextProvider` instead, which delegates to `IMemoryService`. |
| `Neo4jContextProviderOptions` | **Not used** | MAF-coupled config. We have `GraphRagAdapterOptions` with similar fields plus `GraphRagSearchMode.Graph`. |
| `Neo4jSettings` | **Not used** | We use `IOptions<Neo4jOptions>` via DI. |
| `IndexType` | **Not used** | Replaced by `GraphRagSearchMode` enum with an extra `Graph` mode. |
| `StopWords` | **Cloned** → `StopWordFilter` | Identical implementation, different namespace/class name. Decoupled from reference package. |
| `IRetriever` | **Interface referenced** | We implement `IRetriever` in our internal adapters and use `RetrieverResult`/`RetrieverResultItem` from the package. |
| `VectorRetriever` | **Re-implemented** as `AdapterVectorRetriever` | Same Cypher, same patterns. Internal class, no MAF dep. We own the implementation. |
| `FulltextRetriever` | **Re-implemented** as `AdapterFulltextRetriever` | Same. |
| `HybridRetriever` | **Re-implemented** as `AdapterHybridRetriever` | Same concurrent Task.WhenAll + max-score merge pattern. |
| `RetrieverResult` / `RetrieverResultItem` | **Used directly** | Simple record types. No reason to replace. |

Key insight: `Neo4jGraphRagContextSource` references `IRetriever` and `RetrieverResult` from the package for type interop, but all the actual retrieval logic is re-implemented in our `Internal/` adapters. The reference package is a compile-time dependency only for those two types — not for its internal implementation.

**Our write layer — complete:**
- `Neo4jEntityRepository`: `UpsertAsync`, `AddMentionAsync`, `AddMentionsBatchAsync`, `AddSameAsRelationshipAsync`, `MergeEntitiesAsync`
- `Neo4jFactRepository`: `UpsertAsync`
- `Neo4jMessageRepository`: `AddAsync`, `AddBatchAsync`, `DeleteBySessionAsync`
- `Neo4jPreferenceRepository`: `UpsertAsync`
- `Neo4jRelationshipRepository`: `UpsertAsync`
- `Neo4jReasoningTraceRepository` + `Neo4jReasoningStepRepository`: reasoning trace persistence
- `Neo4jConversationRepository`, `Neo4jToolCallRepository`: conversation/tool call persistence
- All use `_tx.WriteAsync(...)` via `INeo4jTransactionRunner` for write isolation

### 2025-07-12 — neo4j-maf-provider Analysis & Read/Write Split

**What neo4j-maf-provider IS:**
- Entirely read-only. Zero write operations in any class.
- 10 classes: `Neo4jContextProvider`, `Neo4jContextProviderOptions`, `Neo4jSettings`, `IndexType`, `StopWords`, and retrieval layer (`IRetriever`, `VectorRetriever`, `FulltextRetriever`, `HybridRetriever`, `RetrieverResult`/`RetrieverResultItem`).
- Its only function: given query text, embed it, search a Neo4j knowledge graph index (vector/fulltext/hybrid), return ranked string results.
- MAF-coupled only at the top layer (`Neo4jContextProvider` extends `AIContextProvider` from `Microsoft.Agents.AI`). The retrieval layer (`IRetriever` and implementations) has zero MAF dependency — only `Neo4j.Driver` + `Microsoft.Extensions.AI`.

**How we used it (class-by-class):**

| Reference Class | Our Treatment | Reason |
|---|---|---|
| `Neo4jContextProvider` | **Not used** | MAF-specific (`AIContextProvider` base). We have `Neo4jMemoryContextProvider` instead, which delegates to `IMemoryService`. |
| `Neo4jContextProviderOptions` | **Not used** | MAF-coupled config. We have `GraphRagAdapterOptions` with similar fields plus `GraphRagSearchMode.Graph`. |
| `Neo4jSettings` | **Not used** | We use `IOptions<Neo4jOptions>` via DI. |
| `IndexType` | **Not used** | Replaced by `GraphRagSearchMode` enum with an extra `Graph` mode. |
| `StopWords` | **Cloned** → `StopWordFilter` | Identical implementation, different namespace/class name. Decoupled from reference package. |
| `IRetriever` | **Interface referenced** | We implement `IRetriever` in our internal adapters and use `RetrieverResult`/`RetrieverResultItem` from the package. |
| `VectorRetriever` | **Re-implemented** as `AdapterVectorRetriever` | Same Cypher, same patterns. Internal class, no MAF dep. We own the implementation. |
| `FulltextRetriever` | **Re-implemented** as `AdapterFulltextRetriever` | Same. |
| `HybridRetriever` | **Re-implemented** as `AdapterHybridRetriever` | Same concurrent Task.WhenAll + max-score merge pattern. |
| `RetrieverResult` / `RetrieverResultItem` | **Used directly** | Simple record types. No reason to replace. |

Key insight: `Neo4jGraphRagContextSource` references `IRetriever` and `RetrieverResult` from the package for type interop, but all the actual retrieval logic is re-implemented in our `Internal/` adapters. The reference package is a compile-time dependency only for those two types — not for its internal implementation.

**Our write layer — complete:**
- `Neo4jEntityRepository`: `UpsertAsync`, `AddMentionAsync`, `AddMentionsBatchAsync`, `AddSameAsRelationshipAsync`, `MergeEntitiesAsync`
- `Neo4jFactRepository`: `UpsertAsync`
- `Neo4jMessageRepository`: `AddAsync`, `AddBatchAsync`, `DeleteBySessionAsync`
- `Neo4jPreferenceRepository`: `UpsertAsync`
- `Neo4jRelationshipRepository`: `UpsertAsync`
- `Neo4jReasoningTraceRepository` + `Neo4jReasoningStepRepository`: reasoning trace persistence
- `Neo4jConversationRepository`, `Neo4jToolCallRepository`: conversation/tool call persistence
- All use `_tx.WriteAsync(...)` via `INeo4jTransactionRunner` for write isolation

**Architecture assessment:**
- Read/write split is clean and intentional.
- The GraphRAG adapter reads from arbitrary user-configured Neo4j indexes (external KGs).
- Our repositories read from and write to our own schema (Entity, Fact, Message, etc.).
- These two read paths are orthogonal and complementary.
- One minor concern: `AdapterVectorRetriever` duplicates `VectorRetriever` almost verbatim. This is acceptable for decoupling, but could diverge in edge cases. Track for future sync.

### 2026-04-13 — Comprehensive neo4j-maf-provider Architecture Review (Orchestration)

**Trigger:** Multi-agent review session assessed Sebastian's analysis for completeness and architectural implications.

**Verified Findings:**
1. **Read-only nature confirmed** — Grep of reference source verified zero write operations across all 10 classes
2. **Type interop minimal** — Only `IRetriever`, `RetrieverResult`, `RetrieverResultItem` used from reference package
3. **Write layer complete** — All 9 repository classes have full CRUD implementations
4. **Boundary clean** — No MAF coupling in retriever layer, no leak of reference implementation details into our code
5. **Duplication tracked** — `AdapterVectorRetriever` mirrors `VectorRetriever` verbatim; acceptable trade-off for decoupling

**Write Layer Gaps Identified:**

| Gap | Severity | Recommendation |
|-----|----------|-----------------|
| No bulk entity upsert (`UpsertBatchAsync`) | MEDIUM | Add to `IEntityRepository` for extraction pipeline performance at scale |
| No bulk fact upsert (`UpsertBatchAsync`) | MEDIUM | Add to `IFactRepository` for consistency |
| No explicit preference deletion | LOW | Add `DeleteAsync()` to support user preference retraction |
| No entity index refresh after merge | LOW | After `MergeEntitiesAsync`, consider re-embedding canonical entity text |

**Recommendations for Team:**
1. **Batch operations** — Priority MEDIUM. Extraction pipelines process tens of entities per message; single-entity upserts will be slow at scale.
2. **Preference lifecycle** — Priority LOW. Agents should support user preference changes. Add `DeleteAsync()` to `IPreferenceRepository`.
3. **Use own schema as KG** — Document that GraphRagAdapter can be configured with `entity_embedding_idx` as `IndexName` to perform graph-enriched retrieval over our own entity schema. Powerful use case.
4. **Retriever sync tracking** — Establish mechanism to track divergence between our adapters and reference implementations. Monthly manual review or automated diff check.

**Architecture Assessment: SOUND ✅**
- Read/write split is well-structured and intentional
- Two read paths (internal memory vs. external KG) are orthogonal and complementary
- Boundary is clean with minimal coupling to reference package
- Full write capability implemented and verified
- Ready for production use with identified improvements tracked

### 2026-07-12 — Full Forensic Trace (Jose Luis Request)

**Full report written to:** `.squad/decisions/inbox/sebastian-maf-provider-trace.md`

**Key forensic findings (confirmed from source):**

#### Q1: Neo4jContextProvider anatomy
- File: `Neo4j/neo4j-maf-provider/dotnet/src/Neo4j.AgentFramework.GraphRAG/Neo4jContextProvider.cs`
- Extends `AIContextProvider` (MAF) + `IAsyncDisposable`
- Fields: `IDriver`, `bool _ownsDriver`, `Neo4jContextProviderOptions`, `IRetriever`
- `ProvideAIContextAsync`: concatenates last N User+Assistant messages → `_retriever.SearchAsync()` → formats to `ChatMessage` list in `AIContext`
- Dual construction: takes existing driver OR factory `Create(uri, user, pass)` with driver ownership

#### Q2: Neo4jMemoryContextProvider vs reference — REIMPLEMENTED
- Same: `AIContextProvider` base class, `ProvideAIContextAsync` hook, `AIContext { Messages }` output
- Different: constructor injects `IMemoryService + IEmbeddingProvider` (not IDriver); searches via `RecallAsync()` (full memory stack) not direct index; extracts session/conversationId; three try/catch layers for graceful degradation; also implements post-run store via `StoreAIContextAsync`
- Classification: REIMPLEMENTED — same MAF pattern, completely different internals

#### Q3: Compile-time dependency confirmed
- **NOT a NuGet package** — `GraphRagAdapter.csproj` has a `<ProjectReference>` to local source tree
- Types used from reference: only `IRetriever`, `RetrieverResult`, `RetrieverResultItem`
- Zero dependency in AgentFramework, Core, Abstractions, Neo4j packages

#### Q4: Class-by-class status
- `IRetriever`, `RetrieverResult`, `RetrieverResultItem` → **REUSED** (compile-time dep, 3 types only)
- `StopWords` → **COPIED** verbatim as `StopWordFilter` (same 107 words, same regex, different namespace/name)
- `VectorRetriever`, `FulltextRetriever`, `HybridRetriever` → **REIMPLEMENTED** (near-verbatim Cypher and logic; internal; use `StopWordFilter` instead of `StopWords`)
- `Neo4jContextProvider`, `Neo4jContextProviderOptions`, `Neo4jSettings` → **IGNORED** entirely
- `IndexType` → **REIMPLEMENTED** as `GraphRagSearchMode` (+ added `Graph` mode)

#### Q5: Write-layer gap analysis (forensic)

**Gap 1 — No UpsertBatchAsync:**
- Pipeline does: `foreach entity → UpsertAsync()` → 2 DB round-trips per entity
- 1000 messages × 10 entities = 20,000 DB trips. Fix: UNWIND batch Cypher. Priority: MEDIUM-HIGH.

**Gap 2 — No DeleteAsync for preferences:**
- Every extraction creates NEW Preference node via GUID (no overwrite). Contradicting preferences accumulate.
- No workaround exists via API. Priority: MEDIUM (correctness bug).

**Gap 3 — No re-embedding after merge:**
- `MergeEntitiesAsync` Cypher never touches `target.embedding`. Alias added to target but embedding not refreshed.
- Embeddings are based on `entity.Name` only. Alias-form queries may miss merged entity.
- Fix: re-embed in `CompositeEntityResolver` after auto-merge, using `name + aliases` text. Priority: LOW-MEDIUM.
