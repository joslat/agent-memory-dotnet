# Memory Review & Implementation Plan

> **Author:** Architecture review (Claude) · **Date:** 2026-06-05 · **Branch:** `remediation/analysis-review-hardening`
> **Scope:** A through-and-through review of AgentMemory-for-.NET — what is done, what is pending, and where the gaps are — with a deep dive and concrete implementation plan for the headline topic: **multi-user / multi-session memory isolation** (user-scoped memory, with an *optional* shared/global scope).

---

## 1. Summary & recommendation

AgentMemory-for-.NET is **feature-complete for v1 and well-hardened**: 11 packages, ~2,139 unit + 31 SK + 84 integration + 3 performance tests green, real CI, package-boundary guard tests, Testcontainers integration, 9 samples, a 4-phase remediation + adversarial review pass all closed, a clean MAF **1.9.0** migration, and a working file-based migration runner + schema bootstrapper. Code quality is high and the architecture is clean (centralized Cypher constants, a `CypherBuilder`, role-split service interfaces). **The library does what it claims — for a single logical memory space.**

There is **one architecturally significant gap, and it is a privacy/correctness gap rather than a missing feature: long-term knowledge is a single global graph with no notion of an owner.** A user's extracted **entities, facts, preferences and relationships** (and reasoning traces) are stored ownerless, and every semantic recall searches *all* of them with no user filter. In any multi-user deployment, **one user's recall can surface another user's facts and preferences**, and a fact extracted for user A can be silently overwritten by user B (the `Fact` node is `MERGE`d on its subject/predicate/object triple with no owner component). Short-term *messages* are correctly isolated by `session_id` — that is the only isolation that exists today, and it is exactly the proven pattern we extend.

**Recommendation (headline):** make **user-scoped memory with an optional shared/global scope** the next **HIGH-priority** workstream, and land it **before the first public NuGet release**. The fix is additive and tractable — it reuses the existing session-filtered message-search pattern, the existing migration runner, and the existing guard tests; no architectural rework is required. Adding optional scope parameters/fields *now* is non-breaking; adding them *after* publishing a permanent NuGet API surface would be a breaking change. Everything else pending (NuGet release at ~80%, streaming-extraction DI wiring, CLI, GDS, BenchmarkDotNet, the S9 refactor) is deferred-by-decision and genuinely non-blocking.

The design below introduces a nullable `owner_id` per record (**`NULL` = shared/global**, which makes all existing data and all unscoped callers behave exactly as today — zero-data-loss, backward-compatible) plus a `MemoryScope` that reads "my records **OR** shared records," filtered into vector search via the same over-fetch-then-`WHERE` technique already used for message search. A complementary **storage tier** ([§R1b](#r1b--application--memory-store-isolation-the-storage-tier-above-the-owner)) adds an optional **application / memory-store id** *above* the owner — ideally a dedicated Neo4j **database per application** (Enterprise/AuraDB), with a shared-database + `owner_id` fallback for Community — giving the full hierarchy **store ⊃ owner ⊃ session**.

---

## 2. Index

| # | Topic | Priority |
|---|---|---|
| [R1](#r1--multi-user--multi-session-memory-isolation-headline) | Multi-user / multi-session memory isolation **(headline)** | 🟥 HIGH |
| [R1b](#r1b--application--memory-store-isolation-the-storage-tier-above-the-owner) | Application / memory-store isolation (storage tier above the owner) | 🟥 HIGH |
| [R2](#r2--reasoning-trace-isolation-secondary-scope-gap) | Reasoning-trace isolation (secondary scope gap) | 🟧 MED |
| [R3](#r3--nuget-release-sequencing) | NuGet release sequencing | 🟥 HIGH |
| [R4](#r4--streaming-extraction-doc-drift--di-wiring) | Streaming extraction (doc drift + DI wiring) | 🟧 MED |
| [R5](#r5--code-quality--overall-status) | Code quality / overall status assessment | 🟩 INFO |
| [R6](#r6--deferred-backlog) | Deferred backlog: CLI, GDS, BenchmarkDotNet, S9 | ⬜ LOW–MED |
| [R7](#r7--done-vs-pending) | Done vs pending (full ledger) | 🟩 INFO |

---

## 3. Status tracking table

| ID | Topic | Area | Priority | Status | Effort | Recommendation |
|---|---|---|---|---|---|---|
| R1 | Multi-user memory isolation (`owner_id` + `MemoryScope`, optional shared) | core / neo4j / abstractions / adapters | 🟥 HIGH | 🟡 In progress — ✅I1 abstractions (`MemoryScope`, `OwnerId` on 5 models, `RecallOptions.Scope`, `SchemaConstants.OwnerId/OwnerKey`) | L | Implement before first NuGet publish; phased plan in §R1 |
| R1b | Application / memory-store isolation (`ApplicationId` → Neo4j database; shared-db fallback) | core / neo4j / infra | 🟥 HIGH | 🟡 In progress — ✅I2 scaffolding (`MemoryStorageStrategy`, `MemoryStoreOptions`, `IMemoryStoreContext`, `DefaultMemoryStoreContext`) | L | `DatabasePerApplication` (Enterprise/Aura) + provisioner; `SharedDatabase`+`owner_id` on Community; §R1b |
| R2 | Reasoning-trace isolation | core / neo4j | 🟧 MED | ❌ Not started | M | Fold into the R1 workstream (same mechanism) |
| R3 | NuGet release sequencing | packaging / ci | 🟥 HIGH | 🟡 ~80% (metadata + workflow done) | S | Hold publish until R1's API surface lands |
| R4 | Streaming extraction DI wiring + doc fix | core | 🟧 MED | 🟡 Built, unregistered | S | Register `IStreamingExtractor`; thread `owner_id` when R1 lands |
| R5 | Code quality / overall correctness | all | 🟩 INFO | ✅ Strong | — | Reuse existing rails; no rework needed |
| R6a | CLI tool (`migrate` / `schema-check`) | tooling | 🟧 MED | ❌ Not started | M | After R1, so `migrate` ships the owner-scope migration |
| R6b | GDS analytics package | analytics | ⬜ LOW–MED | ❌ Not started | M | Defer; must respect `owner_id` once R1 lands |
| R6c | BenchmarkDotNet harness | testing | ⬜ LOW | ❌ Not started | M | Defer; add an owner-filtered vector-search benchmark |
| R6d | S9 truncation-strategy refactor | core | ⬜ LOW | ❌ Not started | S | Defer; do after R1's assembler change to avoid churn |
| R7 | WorkflowMemory sample | samples | ⬜ LOW | ⏸ Deferred | S | Not part of the canonical memory pattern; optional |

Legend — Effort: S ≈ ≤1 day, M ≈ 2–4 days, L ≈ 1–2 weeks. Status: ✅ done · 🟡 partial · ❌ not started · ⏸ deferred.

---

## R1 — Multi-user / multi-session memory isolation (headline)

### R1.1 The problem, in the user's words
*"If I'm a user talking to the AI and it stores the memory, I don't want another user to log in and see what we talked about. I want an ID that identifies this knowledge and assigns it to the user — and this should be optional, as maybe I want the knowledge to be general for everybody to consult."*

### R1.2 Current state — confirmed gap (model → write → read → API → adapter)

Every layer was verified against the code (and three adversarial refutation attempts failed):

1. **Model.** `Entity`, `Fact`, `Preference`, `Relationship` (`src/AgentMemory.Abstractions/Domain/LongTerm/*.cs`) declare **no** `OwnerId`/`UserId`/`TenantId`. `SchemaConstants.Properties` (`SchemaConstants.cs:120-328`) has no owner property; the only user-ish field anywhere is `session_id` on the short-term `Conversation`/`Message`. `ReasoningTrace` has `SessionId` but no user; `ReasoningStep` has neither. `UserId` *does* exist — but only on **request/short-term** types (`RecallRequest.cs:18`, `ExtractionRequest.cs:21`, `Conversation.cs:21`, `SessionInfo.cs:16`, `GraphRagContextRequest.cs:16`).
2. **Write.** No owner is ever persisted. Worse, `FactQueries.Upsert` (`FactQueries.cs:13-33`) `MERGE`s on `{subject, predicate, object}`, so the **same triple from two users collapses onto one shared node and the last writer wins** (cross-user bleed + provenance loss). `Entity`/`Preference`/`Relationship` `MERGE` on their id with no owner. `SchemaQueries.cs` defines no per-user index or ownership edge.
3. **Read.** The three semantic searches call `db.index.vector.queryNodes('{fact,entity,preference}_embedding_idx', $limit, $embedding)` and filter **only** on `score >= $minScore` (`FactQueries.cs:77-82`, `EntityQueries.cs:65-70`, `PreferenceQueries.cs:45-50`). The temporal `AsOf` variants add only time predicates (`TemporalQueries.cs:16-70`). Repository signatures (`Neo4jFactRepository.SearchByVectorAsync` `:153-177`) and service signatures (`ILongTermMemoryService.cs:28-78`) accept **only** `(embedding, limit, minScore)` — there is no parameter through which a filter could even be expressed.
4. **API / assembly.** `RecallOptions` (`RecallOptions.cs`) has no scope field. `MemoryContextAssembler.AssembleContextAsync` runs the long-term searches with no scope and forwards `request.UserId` **only** to GraphRAG (`MemoryContextAssembler.cs:262`). `MemoryExtractionPipeline.ExtractAsync` never reads `request.UserId`, so a supplied user id is **silently dropped** before any node is written.
5. **Adapter.** The MAF providers read only `session_id`/`conversation_id` from the `StateBag` and never a user id (`Neo4jMemoryContextProvider.cs:195-223`), with weak fallbacks (`agent.Id` or a random GUID). Nothing in the repo ever *writes* those `StateBag` keys, and `ISessionIdGenerator` (which can derive a session from a `userId`) is orphaned. MCP defaults `sessionId` to the shared constant `"default"`.

**Consequence:** long-term knowledge is a single global graph. Any session/user can retrieve any other user's entities, facts and **preferences** (the most personal type). This is a deliberate *omission* (the spec only ever required session isolation of short-term messages), so it cannot be patched in one place — it needs coordinated model + write-key + index + read-filter + API changes.

### R1.3 Design goals
- **Per-record ownership** with an **optional shared/global scope**.
- **Backward-compatible & lossless**: existing data and unscoped callers keep working unchanged.
- **Reuse proven rails**: the message-search session filter, the migration runner, the schema bootstrapper, the guard tests.
- **Forward-compatible API** so v1 can publish without a future breaking change.

### R1.4 Core concept — `owner_id` + `MemoryScope`
- Add **one nullable `owner_id`** per long-term/reasoning node. **`NULL` means shared/global** (visible to everyone). This makes every pre-migration node automatically global → zero-data-loss default, and lets a caller mark knowledge shared simply by not setting an owner.
- Reads accept a **`MemoryScope`** = "show me my owner's records **plus** (optionally) shared records":

```csharp
// src/AgentMemory.Abstractions/Options/MemoryScope.cs
public sealed record MemoryScope
{
    /// <summary>Owner whose records to include. Null = no owner filter (today's global behavior).</summary>
    public string? OwnerId { get; init; }
    /// <summary>Also include shared records (owner_id IS NULL). Default true.</summary>
    public bool IncludeShared { get; init; } = true;

    public static MemoryScope Global { get; } = new();                       // no filter
    public static MemoryScope For(string ownerId, bool includeShared = true)
        => new() { OwnerId = ownerId, IncludeShared = includeShared };
}
```
Semantics: `OwnerId == null` ⇒ no filter (current behavior, the safe default). `OwnerId` set ⇒ return rows where `owner_id == $ownerId` **OR** (`IncludeShared && owner_id IS NULL`).

> The owner is the **middle** tier of a three-tier scope model — **store / application ⊃ owner / user ⊃ session** — where the outermost *store* tier (an optional application / memory-store id, ideally its own Neo4j database) is specified in [§R1b](#r1b--application--memory-store-isolation-the-storage-tier-above-the-owner).

### R1.5 Implementation plan (phased, additive)

**Phase A — Abstractions (non-breaking).**
- Add `string? OwnerId { get; init; }` (default `null`) to `Entity`, `Fact`, `Preference`, `Relationship`, `ReasoningTrace`. Not `required`, so all existing initializers compile.
- Add `MemoryScope` (above) and `SchemaConstants.Properties.OwnerId = "owner_id"`.
- Add `MemoryScope? Scope { get; init; }` to `RecallOptions` (default `null` ⇒ Global).

**Phase B — Write path.**
- Thread `ExtractionRequest.UserId` into extraction: `MemoryExtractionPipeline.ExtractAsync` passes it to `PersistenceStage`, which stamps `OwnerId = request.UserId` on every entity/fact/preference/relationship before upsert. `null` ⇒ written as shared (preserves current behavior).
- **Fix the `Fact` MERGE key** (the worst bleed): include the owner in the merge identity so the same triple is distinct per owner. Because Neo4j `MERGE` treats a `NULL` pattern property specially, merge on a non-null **`owner_key = coalesce($ownerId, '*')`** stored alongside the real `owner_id`:
  ```cypher
  MERGE (f:Fact {subject:$subject, predicate:$predicate, object:$object, owner_key:$ownerKey})
  ON CREATE SET f.owner_id = $ownerId, ...
  ```
  `Entity`/`Preference`/`Relationship` keep merging on their unique id and just `SET f.owner_id`.
- Repository upsert param maps add `owner_id`/`owner_key`. MCP `MemoryAdd{Entity,Preference,Fact}` and the SK plugin gain an optional `userId` that flows to `OwnerId`.

**Phase C — Read / vector-search filtering (the Neo4j constraint).**
Neo4j's `db.index.vector.queryNodes` returns top-K by similarity and **cannot pre-filter on a property**; a naïve post-`WHERE` on a small K silently drops owned rows that fell outside K. The codebase already solves this for messages via **over-fetch + post-filter** (`MessageQueries.SearchByVector` builds `CypherBuilder.WithVectorSearch(...)` then `.And("node.session_id = $sessionId", when: hasSessionFilter)`). Apply the identical pattern to facts/entities/preferences:
```cypher
CALL db.index.vector.queryNodes('fact_embedding_idx', $fetchK, $embedding) YIELD node, score
WHERE score >= $minScore
  AND ( $ownerId IS NULL OR node.owner_id = $ownerId OR ($includeShared AND node.owner_id IS NULL) )
RETURN node, score ORDER BY score DESC LIMIT $limit
```
- **Over-fetch** to avoid starvation: `fetchK = max(limit * OverFetchFactor, limit + Floor)` (e.g. factor 5, floor 50), then `LIMIT $limit` *after* the `WHERE`. Expose `OverFetchFactor` on `LongTermMemoryOptions`.
- Add `MemoryScope? scope = null` to repository `SearchByVectorAsync`, the temporal `AsOf` queries, and `ILongTermMemoryService.Search*` (default `null` ⇒ Global ⇒ backward compatible).
- **The one line that makes `RecallRequest.UserId` finally meaningful:** in `MemoryContextAssembler`, build `scope = request.Options.Scope ?? (request.UserId is null ? null : MemoryScope.For(request.UserId))` and pass it to every long-term + reasoning search (both the live and `AsOf` paths).

**Phase D — Indexes.** Add property indexes (`fact_owner_idx`, `entity_owner_idx`, `preference_owner_idx`, `trace_owner_idx`) to `SchemaQueries.PropertyIndexes` so the owner predicate is cheap. Vector indexes are unchanged.

**Phase E — Adapters.**
- MAF: add `DefaultUserIdKey = "user_id"` to `AgentFrameworkOptions`; extend `ExtractIds` in both providers to read `userId` from the `StateBag` and set `RecallRequest.UserId`/`ExtractionRequest.UserId`. Ship a helper `AgentSession.WithMemoryIdentity(userId, sessionId)` (since nothing currently writes the StateBag) and a sample. Optionally wire the orphaned `ISessionIdGenerator` (PersistentPerUser) to derive `sessionId` from `userId`.
- MCP / SK: surface the optional `userId` (MCP tools already accept it on some paths — make it flow to scope).

**Phase F — Backward-compatible migration.** Use the existing file-based `MigrationRunner` (reads `Schema/Migrations/*.cypher`, version-tracked via `(:Migration {version})`). Add `0002_owner_scope.cypher`: create the four owner indexes `IF NOT EXISTS`; **do not backfill** (leaving `owner_id` `NULL` keeps all existing knowledge shared/global = safe, lossless). Provide a *separate, optional* operator backfill that derives `owner_id` from `Conversation.user_id` via the `EXTRACTED_FROM → Message → Conversation` provenance chain, for deployments that want to retroactively attribute existing data.

**Phase G — Tests.**
- *Unit*: `MemoryScope` predicate semantics; assembler builds scope from `RecallRequest.UserId` and `RecallOptions.Scope`; `PersistenceStage` stamps `OwnerId`; `null` user ⇒ shared.
- *Cypher snapshot*: `Search*ByVector` emits the owner OR-shared clause only when scoped, and over-fetches `fetchK` then limits.
- *Integration (Testcontainers)*: seed facts/entities/prefs for userA, userB, and shared(null); assert `recall(scope=userA)` returns **userA + shared and NEVER userB**; `recall(scope=null)` returns all (backward compat); two users store the same SPO fact without overwrite; over-fetch prevents owned-row starvation when many higher-scoring foreign rows exist.
- *Migration*: `0002` is idempotent, creates indexes, leaves existing nodes globally recallable.
- *Guard*: extend the boundary/contract guard tests to assert every long-term `SearchByVector` signature carries a `MemoryScope` parameter (prevents regression of the isolation contract).

### R1.6 Why this design
- **Optional by construction** — exactly the user's requirement: set an owner to privatize, leave it null to share globally.
- **Safe default** — unscoped callers and all existing data are unchanged (everything is "shared" until an owner is assigned).
- **Proven mechanics** — over-fetch+filter already ships for message search; we are generalizing, not inventing.
- **Forward-compatible** — the new params/fields are optional with defaults, so they are non-breaking *now* and avoid a breaking change after the NuGet API surface is frozen.

---

## R1b — Application / memory-store isolation (the storage tier above the owner)

### R1b.1 The requirement
*"I'd like an **application id** / **memory-storage id** (they're roughly the same) that could be `null` (the default storage). A concrete id creates its own memory storage — and inside that storage, the `owner_id` rules from R1 still apply (null = shared to everyone in that storage; a concrete id = private to that user). I think it makes sense for each to have its own Neo4j database."*

### R1b.2 The scope model (three tiers)
This adds the **coarsest** tier on top of R1:

```
Store / Application   (ApplicationId — coarsest; ideally a Neo4j DATABASE)   null ⇒ default store
  └─ Owner / User     (owner_id, R1 — logical, within a store)               null ⇒ shared in store
       └─ Session      (session_id — short-term conversation)                 required for messages
```

A **memory store** is an isolated memory space selected by an optional `ApplicationId`. `null` ⇒ the single default store (today's behavior). A concrete id ⇒ its own store. **Within** a store, R1's `owner_id` provides the per-user layer unchanged.

### R1b.3 Database vs. property — why per-database for this tier
The owner tier (R1) is a **property** (`owner_id`) because it only needs to filter long-term *search*. The application tier is different: it must isolate **every** node type — messages, conversations, entities, facts, preferences, **and** reasoning traces. Two ways to realize it:

| | **Per-database** (recommended) | **Per-property `application_id`** |
|---|---|---|
| Mechanism | `ApplicationId` → a Neo4j database; route per request | `application_id` on every node + predicate on every query |
| Coverage | The DB boundary isolates **all** node types automatically | Must touch **every** `MATCH`/`MERGE`/`CREATE` — large surface |
| Leak risk | Structural isolation; very low | One forgotten query = a cross-tenant leak |
| Ops upside | Per-tenant backup/restore, noisy-neighbour isolation, easy "delete a tenant" | None |
| Cost | **Requires Neo4j Enterprise / AuraDB** (Community = 1 DB) | Works on Community, but invasive + risky |

**Recommendation: per-database for the application tier.** The database boundary is the right tool for "a whole separate memory space," and the driver already supports per-session database selection (`AsyncSession(o => o.WithDatabase(db))`). The property approach is only a fallback when multi-database is unavailable and multiple apps truly must share one Community database.

> ⚠️ **Edition constraint.** Neo4j **Community Edition supports a single user database** — `CREATE DATABASE` / multi-database needs **Enterprise Edition or AuraDB**. A typical local Neo4j is Community, so `DatabasePerApplication` requires Enterprise/Aura; on local Community use `SharedDatabase` + `owner_id`, or run a separate Neo4j instance/connection per application.

### R1b.4 Recommended design — a pluggable `MemoryStorageStrategy`

```csharp
public enum MemoryStorageStrategy
{
    SharedDatabase,        // default — one DB; isolate users via owner_id (R1). Community-compatible.
    DatabasePerApplication // each ApplicationId → its own Neo4j database. Requires Enterprise / AuraDB.
}

public sealed class MemoryStoreOptions
{
    public MemoryStorageStrategy Strategy { get; set; } = MemoryStorageStrategy.SharedDatabase;
    public string DefaultDatabase { get; set; } = "neo4j";  // store used when ApplicationId is null
    public string DatabasePrefix  { get; set; } = "mem_";   // db name = DatabasePrefix + sanitize(ApplicationId)
    public bool   AutoProvision   { get; set; } = true;     // CREATE DATABASE + bootstrap on first use
}

// Ambient, per-DI-scope (the application rarely changes mid-operation, unlike the per-request owner/user).
public interface IMemoryStoreContext { string? ApplicationId { get; } }
```

**Resolution & routing.**
- `INeo4jSessionFactory.OpenSession` currently binds a single static `Neo4jOptions.Database`. Change it to resolve the database name **per scope** from `IMemoryStoreContext`:
  `db = ctx.ApplicationId is null ? opts.DefaultDatabase : opts.DatabasePrefix + Sanitize(ctx.ApplicationId)` (then `WithDatabase(db)`).
- `Sanitize` enforces Neo4j database-naming rules (lowercase, alphanumeric/dot/dash, length); reject/hash anything else.
- `SharedDatabase` strategy always returns `DefaultDatabase` (so `ApplicationId` is ignored unless you opt into the property fallback).

**Provisioning** (`IMemoryStoreProvisioner`).
- `EnsureStoreAsync(applicationId)` → on `DatabasePerApplication`: `CREATE DATABASE {db} IF NOT EXISTS` (run against the `system` database; needs admin), wait for it online, then run `SchemaBootstrapper` **against that database** (constraints + vector/fulltext/property indexes, including the R1 `owner_*` indexes). Cache provisioned store ids to avoid repeat checks.
- On Community / when multi-database is unavailable: throw a clear, actionable `NotSupportedException` ("DatabasePerApplication requires Neo4j Enterprise or AuraDB; use SharedDatabase, or run a separate instance per application").

**API & adapter surface.**
- Set the store per agent/host scope: a `services.AddAgentMemoryStore(o => o.ApplicationId = "...")` or a scoped setter; default null.
- MAF/MCP/SK: read an `application_id` (alongside `user_id`) from the MAF `StateBag` / tool args / SK parameter and populate `IMemoryStoreContext`, so a single deployment can serve multiple applications by setting the store id per request scope.

**Migration & backward compatibility.**
- Fully additive: default `SharedDatabase` + `DefaultDatabase="neo4j"` reproduces today's behavior exactly. No existing call site changes.
- For `DatabasePerApplication`, the existing file-based `MigrationRunner` + `SchemaBootstrapper` run **per database** at provision time (each store gets the same schema, incl. the R1 `0002_owner_scope` migration).

**Composition with R1.** Within any store, R1 is unchanged: `owner_id` null ⇒ shared to everyone in that store; concrete ⇒ private to that user in that store. So a recall resolves to: *this store's database* → *owner_id == me OR shared* → (for messages) *session_id*.

### R1b.5 Tests
- Session factory routes to `DefaultDatabase` when `ApplicationId` is null, and to `mem_<id>` when set; `Sanitize` rejects/encodes invalid names.
- Provisioner is idempotent (`CREATE DATABASE … IF NOT EXISTS`), bootstraps schema in the new database, and caches.
- *Integration (Enterprise/Aura image)*: data written under application A is invisible from application B (different databases); within app A, R1 owner rules still hold.
- Community path: requesting `DatabasePerApplication` without multi-database throws the actionable error.
- Backward compat: default strategy + null application id behaves exactly like today.

### R1b.6 Sequencing note
Like R1, the `IMemoryStoreContext` / `MemoryStoreOptions` / store-aware `INeo4jSessionFactory` surface should land **before the first NuGet publish** so multi-store support is forward-compatible. The `SharedDatabase` default keeps it zero-impact for single-store users; `DatabasePerApplication` is opt-in for Enterprise/Aura multi-tenant deployments.

---

## R2 — Reasoning-trace isolation (secondary scope gap)

`ReasoningTrace` is session-scoped only; `ReasoningQueries.SearchByTaskVector` applies **no** session/user filter, and the assembler calls `SearchSimilarTracesAsync` with a `null` session (`MemoryContextAssembler.cs`), so trace recall crosses sessions/users. `ReasoningStep` has no scope at all. Same class of leak as R1 but lower blast radius (traces are less personal than preferences).

**Recommendation:** fold into the R1 workstream — add `OwnerId` to `ReasoningTrace`, an owner predicate to the task-vector search (same over-fetch+filter), and thread the scope from the assembler.

---

## R3 — NuGet release sequencing

Release prep is ~80% (packaging metadata + tag-gated `squad-release.yml` exist; needs the `NUGET_API_KEY` secret and a `v*` tag; nothing functional blocks it). **But NuGet IDs and the public API surface become permanent on first publish.** Publishing without `MemoryScope`/`OwnerId`/scoped search signatures would force a breaking change to add isolation later.

**Recommendation:** do **not** publish until R1's *API surface* lands (even if the filtering sits behind a feature flag initially). Then release immediately after, so v1's surface is forward-compatible.

---

## R4 — Streaming extraction (doc drift + DI wiring)

`docs/nextsteps.md` lists Streaming Extraction at 0%/pending, but a working `IStreamingExtractor` (chunking, overlap, cross-chunk dedup) already exists in Core with unit tests — it is simply **not registered in DI** (and the shipped interface name differs from the planned `IStreamingExtractionPipeline`).

**Recommendation:** correct the roadmap to "partially done"; register the existing implementation in `ServiceCollectionExtensions`; reconcile the interface name; and ensure its extracted nodes carry `OwnerId` once R1 lands (otherwise streaming re-introduces the isolation gap).

---

## R5 — Code quality / overall status

**Strong.** The 4-phase remediation + adversarial review are closed; there is real CI, package-boundary and abstractions-contract guard tests, centralized Cypher constants, a clean `CypherBuilder`, a file-based `MigrationRunner`, and a `SchemaBootstrapper`; ~99% Python parity. The MAF 1.9.0 migration was source-compatible. The architecture is clean and the isolation fix **rides existing rails** — no structural rework is required; the gap is missing scope plumbing, not bad design. Treat R1 as the single most important pre-release workstream; reuse the message-search pattern and migration infrastructure rather than inventing new mechanisms.

---

## R6 — Deferred backlog

| Topic | Assessment | Recommendation |
|---|---|---|
| **CLI tool** (`migrate`/`schema-check`) | `MigrationRunner` + `SchemaBootstrapper` already provide the engine; a CLI is a thin `dotnet tool` wrapper. Valuable for ops, especially once `0002_owner_scope` exists. | Build *after* R1 so `migrate` ships the owner-scope migration; keep v1 scope to `migrate` + `schema-check`. |
| **GDS analytics** (`AgentMemory.Analytics`) | Opt-in PageRank/community detection; needs the GDS plugin (not in Community Edition). Pure enhancement. | Defer; ensure analytics respect `owner_id` once R1 lands (don't surface another user's nodes). |
| **BenchmarkDotNet harness** | Backs perf claims; hardware-sensitive. The over-fetch+filter strategy adds read cost worth quantifying. | Defer; add an owner-filtered vector-search benchmark to tune `OverFetchFactor`. |
| **S9 truncation refactor** | `MemoryContextAssembler` budget/truncation logic is large and inline but correct and well-tested. | Defer; if touched, do it *after* R1's assembler change to avoid churn. |
| **WorkflowMemory sample** | Not part of the canonical MAF memory pattern (context-provider + tools) the official refs showcase. | Optional; low priority. |

---

## R7 — Done vs pending

**Done (verified).** v1 feature set complete and hardened — 11 packages; 2,139 unit + 31 SK + 84 integration + 3 perf tests green; Testcontainers integration; 9 samples (incl. AgentWithMemory, RealAgent, MemoryToolsAgent, ChatHistoryProvider, Aspire demo); 4-phase remediation + adversarial review closed; MAF 1.1.0→1.9.0 migration; package rename; `DELETE_SESSION_DATA` gap closed; real CI + boundary/contract guard tests. Short-term message search **is** correctly session-scoped (the pattern to reuse). Fix infrastructure already exists: `MigrationRunner`, `SchemaBootstrapper`, the `CypherBuilder` over-fetch+filter vector pattern, `IIdGenerator`/`IClock`.

**Pending — deferred by decision (non-blocking).** NuGet release ~80%; CLI 0%; GDS 0%; BenchmarkDotNet 0%; S9 refactor 0%; backlog (conflict detection, cross-agent sharing, ONNX local embeddings, local NLP extractors, Opik, more framework integrations). Doc drift: streaming extraction is largely built but unregistered.

**Pending — genuine gap (the focus of this plan).** **Multi-user / multi-session isolation** of long-term knowledge (R1) and reasoning traces (R2). No owner field, no owner write, no owner read-filter, no scope parameter, and `RecallRequest.UserId`/`ExtractionRequest.UserId` dropped before the core layers. This is the recommended next **HIGH-priority** workstream and should land **before the first public NuGet release** so the API surface is forward-compatible. The fix is tractable and additive.

---

*This document supersedes the multi-user/isolation aspects of earlier plans and complements `docs/Implementation_Plan_Remediation.md` (closed), `docs/architecture.md`, and `docs/schema.md`.*
