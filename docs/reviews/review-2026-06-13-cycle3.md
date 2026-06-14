# Throat-check review — 2026-06-13 (cycle 3)

**Scope:** adversarial review of the **older / core code** not covered by cycles 1–2 — the extraction
pipeline (`ExtractionStage`, `PersistenceStage`, entity-resolution chain, `EmbeddingOrchestrator`),
short-term / reasoning / memory services, and the framework adapters (MAF providers + chat-history,
SK plugin, MCP tools). Out of scope (already reviewed): D1–D7 decay/bitemporal, the GDS analytics
package, invalidate/supersede, R1/R2 isolation. **Method:** 3 dimension scanners (extraction,
core-services, adapters) → per-finding adversarial verification (skeptic defaults to *reject*).
**8 raw findings confirmed → 7 distinct issues** (two were the same MCP property-name bug found by two
scanners). All actionable findings are fixed in this cycle; one Low is deferred with rationale.

## Findings (ranked)

| # | Severity | Area | Title | Status |
|---|---|---|---|---|
| 1 | 🟥 High | Extraction | Semantic matcher throws on a failed/empty embedding → entity **and its relationships** silently dropped | ✅ fixed |
| 2 | 🟥 High | Persistence | Failed embeddings persisted as `[]` → node permanently un-searchable, never healed by back-fill | ✅ fixed |
| 3 | 🟥 High | Core services | Retroactive **session** extraction silently capped at 100 messages → data loss | ✅ fixed |
| 4 | 🟥 High → see note | Adapters / isolation | MAF providers never set the ambient owner context → facade tools run unscoped | ✅ wiring fixed + ⚠️ host caveat documented |
| 5 | 🟧 Medium | Adapters | MAF chat-history surfaces feed the conversation **reverse-chronological** | ✅ fixed |
| 6 | 🟧 Medium | MCP | `memory_export_graph` / `memory_find_duplicates` query non-existent props (`sessionId`/`entityId`) | ✅ fixed |
| 7 | 🟡 Low | Adapters | `Neo4jMicrosoftMemoryFacade` read path can't be owner-scoped while its write path can (API asymmetry, **no leak**) | ⏸️ deferred (rationale below) |

---

### 1 — Semantic entity matcher throws on a failed/empty candidate embedding → entity + edges silently lost
**High · `src/AgentMemory.Core/Resolution/SemanticMatchEntityMatcher.cs`**

Under the default matcher chain (Exact → Fuzzy → Semantic), a novel entity misses exact+fuzzy and reaches the
semantic matcher. That matcher embeds the candidate name via `EmbeddingOrchestrator`, which **degrades to an
empty vector** (`Array.Empty<float>()`) on any generation failure (transient outage / rate limit) instead of
throwing. The matcher then called `CosineSimilarity(empty, existing.Embedding)`, which throws
`ArgumentException("Embedding dimensions must match.")` (0 ≠ N). The exception propagates out of the resolver;
`ExtractionStage` catches it, logs, and **does not** add the entity to `resolvedEntityMap` — so the entity is
dropped, and every relationship whose endpoint references it is dropped too. One transient embedding hiccup =
permanent, silent loss of an entity and its edges.

**Best fix (applied):** guard the matcher against a zero-length candidate embedding (`return null` → no
semantic signal → resolution falls through to *CreateNew*, so the entity + edges are still persisted), and
skip any `existing` whose stored vector has a different dimensionality. The genuine non-zero dimension-mismatch
throw is kept as fail-fast for truly inconsistent stored vectors. **Tests:** empty candidate → returns null
without throwing; mismatched-dimension existing is skipped.

### 2 — Failed embeddings persisted as zero-length vectors → permanently un-searchable, never healed
**High · `EmbeddingOrchestrator` + `Neo4j{Entity,Fact,Preference}Repository` + `MemoryService` back-fill**

`EmbedAsync` returns `Array.Empty<float>()` for both blank input and generation failures. Every persistence
guard tested `Embedding is not null`, so an empty array was written as a node property `embedding = []`. A
node with `[]` (a non-null zero-length vector) can never be returned by the vector index, **and** the
self-heal back-fill (`GetPageWithoutEmbedding` = `WHERE embedding IS NULL`) never re-selects it because
`[] IS NULL` is false. `UpdateEmbeddingAsync` also wrote `[]` unconditionally, so a back-fill that itself hit
a transient failure re-poisoned the node.

**Best fix (applied):** establish the invariant **"a node either has a real (Length>0) embedding or its
`embedding` is NULL — never `[]`."** Changed every persistence write guard from `is not null` to
`is { Length: > 0 }` (Entity/Fact/Preference upsert + batch paths; also Message/ReasoningStep/ReasoningTrace
for consistency), and made all three `UpdateEmbeddingAsync` skip the write on an empty array. A degraded
embedding now leaves `embedding` NULL so the back-fill keeps the node re-queueable. **Tests:**
`UpdateEmbeddingAsync(empty)` writes no Cypher; non-empty writes one SET.

### 3 — Retroactive session extraction silently capped at 100 messages
**High · `src/AgentMemory.Core/Services/MemoryService.cs` (`ExtractFromSessionAsync`)**

`ExtractFromSessionAsync` called `_shortTerm.GetRecentMessagesAsync(sessionId, int.MaxValue, …)` intending to
load **all** session messages. But `ShortTermMemoryService.GetRecentMessagesAsync` does
`Math.Min(limit, MaxMessagesPerQuery)` (default **100**), and the underlying query returns the 100 *most
recent* messages (DESC). For any session with >100 messages the **oldest** messages were silently never
extracted — no error, no log. (The sibling `ExtractFromConversationAsync` uses an uncapped conversation fetch,
so the two contracts were inconsistent.)

**Best fix (applied):** added a dedicated **uncapped, chronological** session fetch
(`MessageQueries.GetAllBySession` ASC → `IMessageRepository.GetAllBySessionAsync` →
`IShortTermMemoryService.GetAllSessionMessagesAsync`) and routed `ExtractFromSessionAsync` through it. The
capped recent path is reserved for recall/context. **Tests:** `GetAllSessionMessagesAsync` bypasses the cap
(never calls the capped recent path); `ExtractFromSessionAsync` uses the uncapped fetch.

### 4 — MAF providers never set the ambient owner context → LLM-invokable facade tools run unscoped
**High (wiring) · `Neo4jMemoryContextProvider`, `Neo4jChatHistoryProvider`**

The LLM-invokable memory tools (`search_memory` / `search_knowledge` / `recall_preferences` /
`find_similar_tasks` / `remember_*`) built by `MemoryToolFactory` delegate to `MemoryQueryFacade`, which scopes
**only** via the ambient `IMemoryOwnerContext`. Neither MAF provider ever set
`IWritableMemoryOwnerContext.UserId` — they threaded `userId` into their own `RecallRequest`/`ExtractionRequest`
(masking the gap) but left the ambient context null. So in a multi-tenant host, an agent calling those tools
mid-turn would recall across **all** owners and `remember_*` writes would store `OwnerId=null` (shared).
(SK's `Neo4jMemoryPlugin` is unaffected — it takes `userId` as an explicit tool parameter.)

**Fix (applied):** both providers now inject an optional `IWritableMemoryOwnerContext` and push the turn's
`userId` into it (unconditionally, incl. null, so a prior turn can't bleed through), alongside the existing
`ApplyStoreContext`. **Tests:** provider pushes the userId into a substitute owner context (set + null-reset).

> **⚠️ Important nuance discovered while testing (the original finding & its verifier missed this).**
> The default `DefaultMemoryOwnerContext.UserId` is **`AsyncLocal`-backed**, and a value set *inside* an
> awaited async method does **not** propagate back to the caller after the `await` returns (verified by a unit
> test: reading the value in the caller yields null). Since MAF invokes the provider's pre-run hook, awaits it,
> and *then* runs the model + tool calls in the framework's own context, the provider-set AsyncLocal value does
> **not, on its own, reach the tool calls.** The provider wiring is the correct prerequisite and fully works
> when a **scoped** owner context is registered and each turn runs in its own DI scope (provider + tools then
> share one instance). For the AsyncLocal-singleton default, **the host must establish the owner context around
> the agent run** (set `IWritableMemoryOwnerContext.UserId` before `RunAsync`). This is documented in the
> providers' `ApplyOwnerContext` comments.
>
> **Follow-up landed (post-merge):** `IWritableMemoryOwnerContext.BeginOwnerScope(userId)` — a host-facing
> `IDisposable` owner scope. Because `AsyncLocal` flows *down* into awaited work, wrapping the run closes the
> gap reliably:
> ```csharp
> using (ownerContext.BeginOwnerScope(userId))
>     await agent.RunAsync(message, session); // RunAsync + its tool calls inherit the owner
> ```
> Unit-tested end-to-end (`MemoryOwnerContextExtensionsTests`): the owner is visible to async work awaited
> inside the scope (a simulated tool call after an `await`), nested scopes restore the outer owner, and the
> previous value is restored on dispose. This is the recommended multi-tenant pattern for the
> AsyncLocal-singleton default.

### 5 — MAF chat-history surfaces feed the conversation reverse-chronological
**Medium · `Neo4jChatHistoryProvider`, `Neo4jChatMessageStore`, `Neo4jMicrosoftMemoryFacade`**

`RecentMessages` is newest-first (recall orders `timestamp DESC`). The chat-history surfaces mapped it
straight into the agent's prepended history, so the agent received prior turns **newest-first / oldest-last** —
the conversation backwards, degrading coherence and "continue where we left off." (The retrieved *set* is
correct; only the order was wrong — hence Medium, not a data/security issue.)

**Best fix (applied):** reverse `RecentMessages` to chronological (oldest-first) in the three chat-history
consumers — `Neo4jChatHistoryProvider.ProvideChatHistoryAsync`, `Neo4jChatMessageStore.GetMessagesAsync`, and
the `RecentMessages` portion of `Neo4jMicrosoftMemoryFacade.GetContextForRunAsync` — leaving the DB query and
the similarity-ranked `RelevantMessages`/RAG ordering untouched. **Test:** message store returns oldest-first.

*Deliberately not changed:* `MafTypeMapper.ToContextMessages` (the `Neo4jMemoryContextProvider` RAG-context
blend). That path injects recent + relevant memory as *system context*, where the existing recent-first blend
(with `DistinctBy`) is an intentional design choice, not literal chat history; reordering it carries more risk
than reward.

### 6 — MCP `memory_export_graph` / `memory_find_duplicates` use non-existent property names
**Medium · `src/AgentMemory.McpServer/Tools/AdvancedMemoryTools.cs`**

The hand-written Cypher referenced camelCase props that don't exist in the schema: session scoping on
`n.sessionId` / `a.sessionId` (stored as snake_case `session_id`) and endpoint ids `a.entityId` / `b.entityId`
(stored as `id`). A Cypher reference to a missing property silently evaluates to **null**, so a session-scoped
export returned **zero** rows and every relationship endpoint id came back **null** — silent wrong results
(behind the `EnableGraphQuery` opt-in). The mock-based unit tests never exercised the real Cypher, so they
masked it.

**Best fix (applied):** `n.sessionId → n.session_id`, `a/b.sessionId → a/b.session_id`,
`a/b.entityId → coalesce(a/b.id, elementId(a/b))` (stable id even for node types without `id`); dropped the
redundant `a.entityId <> b.entityId` (the `elementId(a) < elementId(b)` guard already ensures distinct nodes);
`find_duplicates` returns `a.id`/`b.id`. Documented in a code comment that only session-bearing nodes
(Message/Conversation/ReasoningTrace) carry `session_id`, so a session-scoped export surfaces those node types.
**Tests:** captured Cypher uses `session_id` / `id` (and the `$sessionId` *parameter* name is unchanged), and
contains none of the broken `.sessionId` / `.entityId` property accessors.

### 7 — `Neo4jMicrosoftMemoryFacade` read path can't be owner-scoped while its write path can
**Low · `src/AgentMemory.AgentFramework/Neo4jMicrosoftMemoryFacade.cs` — DEFERRED**

`PersistAfterRunAsync` accepts `userId` (owner-stamped writes) but the paired `GetContextForRunAsync` has no
`userId` and builds `RecallRequest` with `SessionId`+`Query` only. The verifier **downgraded this to Low with
no data leak**: `GetContextForRunAsync` returns only `RecentMessages`+`RelevantMessages`, which are
**session-keyed and carry no `owner_id`** — so passing `userId` would have *zero* behavioral effect on what it
returns today (it discards the owner-scoped entity/fact/preference/trace sections). Adding a `userId` param now
would be misleading (it would read as "recall is owner-scoped" when it is not). **Decision: defer** until/unless
the facade is extended to surface the long-term owner-scoped sections; revisit then. Recorded here so the
asymmetry is tracked rather than lost.

---

## Changes in this cycle

**Source:**
- `SemanticMatchEntityMatcher` — empty/mismatched-dimension embedding guards (#1).
- `Neo4j{Entity,Fact,Preference,Message,ReasoningStep,ReasoningTrace}Repository` — `Length > 0` write guards;
  `UpdateEmbeddingAsync` skips empty (#2).
- `MessageQueries.GetAllBySession` + `IMessageRepository.GetAllBySessionAsync` +
  `IShortTermMemoryService.GetAllSessionMessagesAsync` + `MemoryService.ExtractFromSessionAsync` (#3).
- `Neo4jMemoryContextProvider`, `Neo4jChatHistoryProvider` — optional `IWritableMemoryOwnerContext` +
  `ApplyOwnerContext` (#4); chat-history reverse-to-chronological (#5).
- `Neo4jChatMessageStore`, `Neo4jMicrosoftMemoryFacade` — reverse-to-chronological (#5).
- `AdvancedMemoryTools` — corrected schema property names (#6).

**Tests added/updated:** `SemanticMatchEntityMatcherTests` (+2), `Neo4jEmbeddingInvariantTests` (new, 6),
`ShortTermMemoryServiceTests` (+1), `MemoryServiceBatchTests` (updated 2, +1), `Neo4jMemoryContextProviderTests`
(+3), `Neo4jChatMessageStoreTests` (+1), `AdvancedMemoryToolsTests` (+2), `CypherQuerySnapshotTests` (count
133 + regenerated snapshot).

**Result:** full unit suite green (**2441 passed, 0 failed**). No interface *count* changed (only methods
added), so the abstractions contract-guard counts are unaffected.

## Follow-ups (not blocking)
- **#4 correct-by-default owner scoping** — ✅ addressed post-merge with
  `IWritableMemoryOwnerContext.BeginOwnerScope(userId)` (host wraps the run; AsyncLocal flows down into the
  tool calls). A fully zero-host-wiring default would still require a per-run DI scope or MAF middleware hook;
  the helper is the pragmatic, tested closure.
- **#7**: revisit if `GetContextForRunAsync` is extended to surface long-term owner-scoped sections.
