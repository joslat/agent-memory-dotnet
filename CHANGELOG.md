# Changelog

All notable changes to this project will be documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **`IReasoningMemoryService.ListAllTracesAsync` — owner-scoped, paged, cross-session trace listing.** Returns a `PagedResult<ReasoningTrace>` (newest-first, N+1 `HasNextPage`, `offset`-advanced), optionally owner-scoped (R1). Mirrored on `IReasoningTraceRepository.ListAllAsync`. Added pre-`1.0` because extending a public interface after the freeze breaks every third-party implementer.
- **`IToolCallRepository.GetStatsAsync(toolName?)` + a `ToolCallStats` record — per-tool usage aggregates.** Groups tool calls by name (total / successful / failed / success-rate / avg-duration) over calls reachable through owner-scoped reasoning traces; never reads the cross-owner global `:Tool` node. Same pre-`1.0` interface-stability rationale.
- These two additions let the upstream-TCK bridge drop its last two raw-Cypher fallbacks (`list_traces` with no session, `get_tool_stats`) in favor of the first-class services.

### Changed

- **`IEntityRepository.MergeEntitiesAsync` now returns `Task<bool>`** (was `Task`): `true` when the merge matched and ran, `false` for a guarded / non-existent no-op. Future-proofs the deferred merge-relationship-transfer fix (it can report edges moved without another signature break).
- **1.0 API-surface lockdown — implementation types internalized.** Concrete implementation classes that are only ever resolved through the public Abstractions interfaces (the memory/reasoning services, Neo4j repositories/services/query holders/infrastructure, MCP tools/resources/prompts, extraction providers, enrichment decorators, GDS analytics services, merge strategies, and stubs) are now `internal`, shrinking the public surface from ~331 to ~203 types ahead of the SemVer-stable `1.0`. Accessibility-only: no behavior, signature, or DI-wiring change — DI resolves the internal types (via their still-public constructors) unchanged. The public contract is the Abstractions interfaces/records/options/enums, each package's `ServiceCollectionExtensions` + options, the Microsoft Agent Framework and Semantic Kernel adapters, and a small set of deliberate seams (`INeo4jTransactionRunner`, `ISchemaBootstrapper`, `IMigrationRunner`, `MemoryActivitySource`, `MemoryMetrics.MeterName`, the stub/clock/id helpers, `ExtractorBase`).
- **`SchemaConstants` and the schema-parity kit are now internal.** `SchemaConstants` (raw Neo4j backend label/property/edge strings) and the parity types (`SchemaParityVerifier`, `SchemaDescriptor`, `SchemaParityPolicy`, `SchemaParityReport`, `UpstreamSchemaRegistry`, `DotNetSchema`) — previously described as a reusable library component in `AgentMemory.Neo4j.Schema.Parity` — are implementation details for `1.0`. Schema-parity verification remains available through the `agentmemory schema-parity` CLI command; it is no longer a library API.

## [0.1.0-preview.4] - 2026-06-21

This release is dominated by a sustained correctness-hardening effort: **six rounds** of adversarial
bug-hunting plus a final exhaustive convergence-verification pass, surfacing and fixing **80+ confirmed
defects** across the library (cross-cutting issues the per-file reviews missed — DI/config wiring,
cancellation, multi-tenant isolation, bitemporal/dedup correctness, resilience, and context assembly).
Every fix shipped with a regression test targeting the trigger. The public API is unchanged except for the
small items under **Changed**/**Removed** below.

### Added

- **`agentmemory schema-check` CLI command — runtime schema conformance.** Verifies that the live Neo4j database actually has every constraint and index the bootstrapper creates: it reads `SHOW CONSTRAINTS` / `SHOW INDEXES`, diffs them against the expected baseline (parsed from `SchemaQueries`, parameterized by the configured embedding dimensions), prints any missing objects, and exits `0` when conformant / `1` otherwise — the runtime counterpart to `bootstrap`, and CI-friendly. This is distinct from `schema-parity` (a *static* check that the .NET schema is compatible with the embedded upstream Python snapshot). New `SchemaConformance` helper (`ExpectedObjectNames`/`ParseObjectName`/`MissingObjects`) + `SchemaQueries.ShowConstraintNames`/`ShowIndexNames`; unit-tested.

- **Meta-package `AddNeo4jAgentMemory` now forwards a `configureStore` delegate.** The one-line `AgentMemory` registration gained an optional 4th parameter, `Action<MemoryStoreOptions>? configureStore`, so the application/memory-store isolation tier (R1b) — e.g. `MemoryStorageStrategy.DatabasePerApplication`, which routes each `ApplicationId` to its own auto-provisioned Neo4j database (Enterprise/AuraDB) — can be configured without dropping down to the `AgentMemory.Neo4j` registration. Backward-compatible (optional, appended last; `SharedDatabase` default unchanged). Documented in `docs/getting-started.md` §3.4 ("Multiple databases & instances"), with a new `deploy/docker-compose.enterprise.yml` (Enterprise + APOC + GDS) for local multi-store/analytics testing.

- **Previously-dead configuration options are now wired and enforced.** `LongTermMemoryOptions.MinConfidenceThreshold` gates the direct `Add{Entity,Fact,Preference}` API (sub-threshold adds are skipped; MCP add tools report `persisted`/`reason`); `LlmExtractionOptions.EntityTypes` now builds the LLM system prompt; `ReasoningMemoryOptions.MaxTracesPerSession` enforces a per-session retention prune; `EnrichmentOptions.MaxRetries`/`GeocodingOptions.MaxRetries` drive a dependency-free retry handler on the enrichment/geocoding HTTP clients; `ReasoningMemoryOptions.StoreToolCalls`/`GenerateTaskEmbeddings`, `AgentFrameworkOptions.PersistReasoningTraces`, and `ShortTermMemoryOptions.DefaultRecentMessageLimit` are likewise honored.

### Fixed

The cross-cutting correctness pass (the six hunt rounds + convergence test). Grouped by area:

- **Cancellation is honored everywhere.** `OperationCanceledException` from a cancelled caller token now propagates instead of being swallowed and reported as a fabricated/empty success — across the Agent Framework adapters (chat-message store, chat-history provider, context provider, memory facade), the context assembler's GraphRAG fetch, the extraction pipeline (extractor base / extraction / persistence / entity-resolution loop), the embedding orchestrator, the context compressor, and the GraphRAG retriever.
- **Multi-tenant (R1) isolation hardening.** Session-keyed destructive writes (reasoning-trace retention prune **and** session clear / delete-by-session) now confine to a single owner bucket — owner A can never evict owner B's traces under a shared/guessable `session_id`; a null-owner clear touches only the shared bucket. `ListBySession`/`ListTraces` reads are owner-scoped; relationship creation no longer leaks owner-less edges. Entity resolution and dedup candidate queries (`GetByType`, `FindSimilarByEmbedding`) plus the duplicate-detection reports now exclude soft-invalidated nodes, so a re-extracted entity can't merge into a tombstone.
- **Bitemporal & dedup correctness.** Fact upsert MERGEs idempotently on the `{subject, predicate, object, owner}` triple on **both** the single and batch paths (batch previously MERGEd on `id`, creating duplicate nodes on re-extraction); re-asserting an invalidated/superseded triple restores it to live recall while preserving its valid-time window; embedding/provenance sub-writes land on the surviving node. `FindDuplicate` excludes invalidated nodes so a re-asserted fact isn't deduped onto a dead one.
- **Degraded-input safety.** An empty/degraded embedding (`Array.Empty<float>()`) is now a search-boundary invariant: all vector-search and dedup paths short-circuit to empty rather than passing a zero-dimension vector to `db.index.vector.queryNodes` (which throws); the as-of recall path is covered too.
- **Resilience.** Transient enrichment failures (`Error`/`RateLimited`, which providers like Diffbot *return* rather than throw) are retried and no longer cached or counted as success — in the background queue, the caching decorator, and the telemetry decorator; HTTP timeouts are distinguished from caller cancellation. The embedding-backfill loop has a forward-progress guard (no infinite loop on a persistently-failing embed). A reasoning trace concurrently deleted between read and write yields a typed "not found" (and an actionable error when a step's parent trace is gone) instead of an opaque exception. The background enrichment worker survives a transient fault instead of dying silently. GDS-availability probing distinguishes "not installed" (cache) from a transient failure (re-probe).
- **Context assembly & budgeting.** Truncation keeps the **most recent** messages (the MAF mapper and context compressor previously kept the oldest of a newest-first list); long-term memory blocks are budgeted separately so they're never the first dropped; a large `MaxTokens` can't overflow the char budget to an empty context; proportional GraphRAG truncation never splits a UTF-16 surrogate pair. Hybrid retrieval fuses semantic + keyword results with scale-free Reciprocal Rank Fusion; raw fulltext queries are Lucene-escaped.
- **Other.** `FactQueries.Upsert` no longer clobbers a fact's stable `id` (or its supersession `valid_until`) on re-extraction; numeric/culture formatting uses `InvariantCulture` (MCP responses, CLI output, Diffbot); several `OperationCanceledException`/read-then-write race and `SingleAsync`-on-empty edge cases return clean results; MCP `record_tool_call` surfaces an error payload for an unknown status instead of coercing to success.

### Changed

- **`IReasoningTraceRepository.UpdateAsync` now returns `Task<ReasoningTrace?>`** (was non-null) — `null` when the trace no longer exists (e.g. concurrently deleted), so callers surface a clean not-found instead of an opaque exception.
- **`ClearSessionAsync` (`IMemoryService` / `IShortTermMemoryService` / `IMemoryMaintenance`) and `IReasoningTraceRepository.DeleteBySessionAsync` gained an optional `string? ownerId`** to confine the reasoning-trace delete to one owner bucket (additive; default `null` = shared bucket only).
- **Library code now enforces `ConfigureAwait(false)`** via a `src/.editorconfig` CA2007 rule (applied across all production projects), so awaits don't capture the caller's synchronization context.

### Removed

- **`MemoryDecayOptions.EnableAutoPrune`** — a documented-but-unread option whose premise (auto-prune during extraction) belongs with the broader decay/forgetting work, not a settable flag that did nothing. Pruning runs only when explicitly invoked.
- **`LongTermMemoryOptions.EnableEntityResolution`** (duplicated the working `ExtractionOptions.EntityResolution` switches), **`MemoryOptions.EnableAutoExtraction`** (Core extraction is explicit by design), and **`MemoryDecayOptions.MaxMemoriesPerSession`** (long-term nodes are cross-session and carry no `session_id`, so a per-session cap couldn't be coherently enforced).

## [0.1.0-preview.3] - 2026-06-14

### Added

- **`IWritableMemoryOwnerContext.BeginOwnerScope(userId)` — host-facing ambient owner scope.** A small `IDisposable` that sets the ambient memory owner (IC8) for the current async flow and restores it on dispose. This is the reliable way to make the LLM-invokable MAF facade tools (`search_memory` / `remember_*`) owner-scoped: because the owner context is `AsyncLocal`-backed, a value set in an *enclosing* scope flows down into the awaited agent run and its tool calls — `using (ownerContext.BeginOwnerScope(userId)) await agent.RunAsync(...)`. (The MAF providers set the owner per turn, but a value set inside their awaited pre-run hook does not propagate back to the framework's later tool calls under the AsyncLocal-singleton default — so the host scope is the correct closure; see `docs/reviews/review-2026-06-13-cycle3.md` finding #4.) Unit-tested end-to-end (flows into nested async work, nested scopes restore the outer owner, restored on dispose).

### Fixed

- **Cycle-6 review fixes (Enrichment HTTP timeouts + samples).** Deep review of the Enrichment clients and the samples (`docs/reviews/review-2026-06-13-cycle6.md`); 17 candidates → 4 confirmed. (1) **Timeout masking (Medium):** an `HttpClient.Timeout` surfaces as a `TaskCanceledException` with the caller's token *not* cancelled, so the `when (ct.IsCancellationRequested)` filter missed it and a timeout was logged as a generic failure — Nominatim/Wikimedia now have a distinct timeout branch (graceful `null`, clearer log). (2) **Diffbot timeout (Medium):** Diffbot returned a terminal `Error` on timeout, which the background queue counted as success (skipping retry) and the cache stored (suppressing re-enrichment) — a timeout is now thrown as transient so the queue retries and nothing is cached. (3) **DQL escaping (Low):** Diffbot now backslash-escapes quotes in entity names so a name like `John "Jack" Doe` doesn't build a malformed query that silently returns nothing. (4) **Sample host disposal (Low):** six samples never disposed the host (leaking the async-only Neo4j driver factory in copy-pasted long-running services) and `AspireDemo.DemoApp` disposed it *synchronously* (which throws over the async-only disposable) — all now use `await using` host disposal. Covered by new/updated tests; full unit suite green (2475).

- **Cycle-5 review fixes (GraphRAG retrieval + MCP + assembler correctness).** Adversarial review of unreviewed correctness surface (`docs/reviews/review-2026-06-13-cycle5.md`); 14 candidates → 6 confirmed. (1) **MCP cross-owner read (High):** `memory_get_conversation` had no owner scope, so a multi-tenant client could read another owner's messages by passing their (guessable/enumerable) conversation id — now takes an optional `userId` and denies unless the conversation is owned by that user or un-attributed; (2) **fulltext Lucene escaping (Medium):** the raw fulltext-query path (`filterStopWords = false`, the Hybrid default) bound user text straight into the Lucene parser, so an ordinary query like `C++ vs Rust: faster?` threw an unhandled parse error or silently altered recall — now escaped via a new `LuceneQueryEscaper` (literal-text matching); (3) **MCP cross-owner enumeration (Medium):** `memory_list_sessions` likewise gained an optional `userId` filter; (4) **hybrid ranking (Low):** the Hybrid retriever compared raw cosine `[0,1]` against unbounded BM25 scores (letting keyword frequency dominate semantic relevance) — replaced with scale-free **Reciprocal Rank Fusion** (this also makes the `architecture.md` "RRF fusion" claim true); (5) **truncation (Low):** proportional GraphRAG truncation no longer splits a UTF-16 surrogate pair (emoji); (6) **input hygiene (Low):** `limit`/`offset`/`maxTokens` are clamped across the MCP resources/tools (a negative `SKIP`/`LIMIT` is a Neo4j error; a huge `limit` is a resource-exhaustion vector). Covered by new tests; full unit suite green (2472).

- **Cycle-4 review fixes (peripheral packages: CLI/SK/Observability).** Adversarial review of the previously-unreviewed surface (`docs/reviews/review-2026-06-13-cycle4.md`); 36 candidates → 6 confirmed. (1) **CLI exit codes (High):** `agentmemory` disposed its host with synchronous `using`, but the Neo4j driver factory is an `IAsyncDisposable`-only singleton — a sync `ServiceProvider.Dispose()` over it **throws**, which the top-level `catch` turned into `error: …` + **exit code 1 on every successful command** (breaking any CI/script checking `$?`). Fixed with `await using` / `CreateAsyncScope`. (2) **SK `recall` tool (Medium):** removed a dead `conversationId` parameter that did nothing (the recall pipeline has no conversation scoping) yet advertised "narrow recall scope" to the LLM and, sitting before `userId`, shadowed a positional owner id; `userId` is now the 3rd positional arg and reaches `RecallRequest.UserId`. (3) **Observability (Low):** `GenerateEmbeddingsBatchAsync` is now `async`/`await` so its trace span spans the actual work instead of closing at ~0ms; and the `extract_from_session`/`extract_from_conversation` spans now carry a `memory.user_id` tag for owner correlation. Covered by new tests (`Neo4jDriverFactoryDisposalTests`, plus observability + SK additions).

- **Cycle-3 review fixes (core/extraction/adapters durability + isolation).** Seven issues from an adversarial review of the older/core code (`docs/reviews/review-2026-06-13-cycle3.md`): (1) the semantic entity matcher no longer throws when an embedding generation transiently fails (returns empty) — it now skips semantic matching instead of letting the exception silently drop the entity **and every relationship referencing it**; (2) failed embeddings are no longer persisted as zero-length `[]` vectors that are un-searchable *and* invisible to the `embedding IS NULL` back-fill — every repository write now requires `Length > 0` (else leaves `embedding` NULL and re-queueable), and `UpdateEmbeddingAsync` skips empty arrays; (3) retroactive **session** extraction (`ExtractFromSessionAsync`) no longer silently caps at 100 messages (`MaxMessagesPerQuery`) and drop the oldest — a new uncapped, chronological `GetAllSessionMessagesAsync` is used; (4) the MAF context/chat-history providers now push the turn's `userId` into the ambient `IMemoryOwnerContext` so the LLM-invokable facade tools (`search_memory`/`remember_*`) can be owner-scoped instead of running unscoped *(note: the AsyncLocal-singleton default still requires the host to establish the owner context around the run for the value to reach tool calls — see the review doc)*; (5) the MAF chat-history surfaces (`Neo4jChatHistoryProvider`, `Neo4jChatMessageStore`, `Neo4jMicrosoftMemoryFacade`) now feed conversation history **chronologically** (oldest-first) instead of reversed; (6) MCP `memory_export_graph` / `memory_find_duplicates` now query the real schema property names (`session_id`, `id`) instead of the non-existent `sessionId`/`entityId`, which had made session-scoped exports return nothing and endpoint ids null. All covered by new/updated unit tests.

### Added

- **`AgentMemory.Analytics` — optional Neo4j GDS analytics (new package).** Opt-in PageRank + Louvain community detection over the entity `RELATED_TO` graph: `IMemoryPageRankService.RankEntitiesAsync` surfaces the most graph-important entities (memory importance), and `IMemoryCommunityService.DetectCommunitiesAsync` clusters entities into topics. Both run over a transient, **owner-scoped** Cypher projection (a relationship is projected only when *both* endpoints are in scope, so the analysis never crosses the R1 owner boundary; only live, non-invalidated entities are included) and clean up the projection afterwards. **Graceful degradation:** if the GDS plugin isn't installed (it's not bundled with Neo4j Community Edition), `IGdsAvailability` detects its absence and the services return empty rather than throwing — so it's safe to register unconditionally. DI: `AddGdsMemoryAnalytics()` (requires `AddNeo4jAgentMemory()`). The package is **not** part of the `AgentMemory` meta-package (separate, opt-in install). Verified by unit tests (query shape, graceful no-op, DI) and live integration tests against a real GDS-enabled Neo4j (PageRank ranks a hub highest; scoped projections exclude other owners; community detection separates disconnected owners).

- **Public invalidate/supersede surface (completes D5/D7).** The non-destructive soft-invalidate (D5) and supersession (D7) writers, previously only on the repositories, are now reachable through the public API. New on `ILongTermMemoryService`: `InvalidateFactAsync` / `InvalidateEntityAsync` / `InvalidatePreferenceAsync` and `SupersedeFactAsync` / `SupersedePreferenceAsync` (owner-scoped via `MemoryScope`, thin delegations to the repos). New `agentmemory` CLI verbs: `invalidate --type <fact|entity|preference> --id <id> [--owner <id>]` and `supersede --type <fact|preference> --loser <id> --winner <id> [--owner <id>]`. New MCP tools: `memory_invalidate` and `memory_supersede` (owner-scoped via `userId`). All non-destructive (kept + as-of-recallable) and R1 owner-scoped. Unit-tested at every layer (service delegation, CLI routing/exit codes, MCP routing/scoping); the underlying repo writers already have live-Neo4j coverage from D5/D7.

## [0.1.0-preview.2] - 2026-06-13

### Added

- **Schema-parity compatibility kit (TCK) — reusable component + CLI self-check + regression test.** A versioned, drop-in verifier that proves the .NET schema stays compatible with upstream `neo4j-agent-memory`. The frozen upstream `schema.json` snapshots ship as embedded resources (`UpstreamSchemaRegistry`, keyed by version — currently v0.5.0); `SchemaParityVerifier` reflects the live `SchemaConstants` (`DotNetSchema`) and compares labels, relationship types, and property names against a snapshot under a documented divergence `SchemaParityPolicy`, returning a `SchemaParityReport` (breaks vs. intentional divergences). Three surfaces over one engine: (1) a **CLI self-verification** — `agentmemory schema-parity [--upstream-version <v>]` (no Neo4j needed; exit 1 on a break; CI-friendly); (2) a **reusable library component** (`AgentMemory.Neo4j.Schema.Parity`); (3) a **regression test** that asserts current compatibility *and* that the verifier catches each drift class (dropped label, renamed property, undocumented .NET-only type, upstream catching up to a .NET superset). Adding a new upstream version is a drop-in: embed its `schema.json` and register a policy. v0.5.0 result: COMPATIBLE with 8 documented divergences (the `owner_id`/`owner_key`/`invalidated_at` supersets, the `HAS_FACT`/`HAS_PREFERENCE`/`IN_SESSION` extensions, and the `User`/`MemoryReadAudit` omissions).
- **R2 — owner-scoped `ListTracesAsync`/`ListBySessionAsync` (last R1 read gap).** A `session_id` is not a private random handle (it can be shared or guessable), so listing a session's reasoning traces is now owner-scopeable: `IReasoningMemoryService.ListTracesAsync` and `IReasoningTraceRepository.ListBySessionAsync` take an optional `MemoryScope` and, when scoped, return only the owner's own (and optionally shared) traces — never another owner's — mirroring the Fact/Entity/Preference/trace-search R1 pattern. Verified by live-Neo4j tests (alice and bob with traces in the *same* session; alice's scoped list excludes bob's). Relationship and ReasoningTrace owner-*writes* were already complete; this closes the remaining list-level read leak. ReasoningStep/ToolCall reads stay by-parent-handle (reachable only via a random trace/step id obtained through a scoped search — the same exemption as every `GetByIdAsync`), now documented as a deliberate decision.
- **Contradiction → supersession + non-destructive consolidation (D7).** New supersession writers `IFactRepository.SupersedeAsync(loser, winner, scope)` and `IPreferenceRepository.SupersedeAsync(loser, winner, scope)` close a loser **non-destructively** — stamp `invalidated_at` (and, for facts, `valid_until`) so it drops from live recall but is kept and stays visible to as-of recall before supersession — and link `(loser)-[:SUPERSEDED_BY]->(winner)` (new `SchemaConstants.RelationshipTypes.SupersededBy`; mirrors upstream `supersede_preference`). The detect-only `IConflictDetectionService` gains an **opt-in** `ResolveFactContradictionsAsync` that resolves each contradiction group by keeping the highest-confidence assertion and superseding the rest (R1 owner-scoped; detection stays the non-mutating default). The duplicate-preference collapse (`ConsolidationQueries.RemoveDuplicatePreferences`, used by `agentmemory consolidate`) is now **non-destructive** — older duplicates are soft-invalidated and linked `:SUPERSEDED_BY` to the survivor instead of `DETACH DELETE`d, and the pass is idempotent (already-invalidated rows are excluded from grouping). All supersession is owner-scoped (both endpoints must belong to the owner) and idempotent (`coalesce` + `MERGE`). This closes the second destructive path flagged in `docs/bitemporal-memory-assessment.md` — forgetting is now fully reversible. Verified by unit tests (Supersede/dedup Cypher shape, owner scoping) + live-Neo4j integration (supersede drops loser from live recall but keeps it as-of-before and links the winner; conflict resolution keeps the highest-confidence fact and respects owner isolation; non-destructive dedup is idempotent). Design: `docs/bitemporal-memory-assessment.md §8`, plan `docs/Memory_Review_and_Implementation_Plan.md §II.8`.
- **Bitemporal two-clock recall (D6).** Point-in-time recall now spans **two independent clocks**: the **valid-time** clock (`validAsOf` — "what was true in the world", bounding a fact's `valid_from`/`valid_until`) and the **transaction-time** clock (`systemAsOf` — "what the system had recorded", bounding every record's `created_at`/`invalidated_at`). New overloads `IMemoryRecall.RecallAsOfAsync(request, validAsOf, systemAsOf, …)` and `IMemoryContextAssembler.AssembleContextAsOfAsync(request, validAsOf, systemAsOf, …)` let you ask *"what was true at T1, as we believed it at T2"* — reproducing a past decision or auditing a belief before a later correction. The existing single-`asOf` overloads **delegate with both clocks equal**, so existing callers are byte-for-byte unchanged. Clock mapping: facts observe both clocks; messages, entities, preferences, and reasoning traces (no valid-time window) observe only `systemAsOf`. Builds on the D5 `invalidated_at` writer; `IFactRepository.SearchByVectorAsOfAsync` already carried `systemAsOf` — D6 surfaces it through the service + assembler. Verified by unit tests (both clocks propagate distinctly; assembler clock mapping) + a live-Neo4j test pinning all four timestamps and proving each clock filters independently of the other. Design: `docs/bitemporal-memory-assessment.md §8`, plan `docs/Memory_Review_and_Implementation_Plan.md §II.8`.
- **Per-request query-intent presets (D3).** `RecallOptions.Intent` (`RankingIntent.Default`/`Latest`/`Analog`) re-weights a single recall over the configured `MemoryRankingOptions`: `Latest` raises recency (favour fresh), `Analog` zeroes recency so structurally/semantically similar — and possibly *old* — precedents surface (case-based retrieval), `Default` is unchanged. Threaded via a new ambient `IMemoryRankingContext` (AsyncLocal, mirroring the owner/store contexts) that the context assembler publishes per-recall and the long-term repositories read — **no change to `ILongTermMemoryService` or the repository interfaces**. Verified by unit tests (`ForIntent` math, assembler publish-then-reset) + a live-Neo4j test (Latest promotes a fresh memory; Analog keeps the most-similar one on top even over a recency-heavy config).
- **Non-destructive decay + transaction-time clock (D5 + D4) — forgetting is now reversible by default.** New `invalidated_at` transaction-time axis (`SchemaConstants.Properties.InvalidatedAt`): live recall (`SearchByVector` for Fact/Entity/Preference) now excludes soft-invalidated nodes (a no-op for existing data — nothing has it set), while as-of recall keeps them for times *before* invalidation. New owner-scoped `InvalidateAsync` writers on the Fact/Entity/Preference repositories (idempotent `coalesce`; R1-scoped). **Decay pruning is now non-destructive by default** (`MemoryDecayOptions.NonDestructive = true`): low-score nodes are soft-invalidated (kept, recoverable, auditable, dropped from live recall) instead of `DETACH DELETE`d — set `NonDestructive=false` for an explicit hard purge (storage reclamation / GDPR). The non-destructive prune is idempotent (skips already-invalidated nodes). Verified by unit tests + live-Neo4j integration (invalidate hides from live recall but stays as-of-recallable; prune soft-invalidates by default, hard-deletes when opted in; both owner-scoped). This removes the irreversible-deletion behavior flagged as the highest-risk item in `docs/bitemporal-memory-assessment.md`.
- **Retrieval ranking — recency re-ranker (D1) + structural hop-decay (D2), opt-in and schema-neutral.** New `MemoryRankingOptions` (`MemoryOptions.Ranking`) with a `MemoryProfile` capability tier (`Parity` → `Enhanced` → `Bitemporal`) — a "start at parity, dial up" switch. **D1:** when `RecencyWeight > 0`, long-term vector recall (`Fact`/`Entity`/`Preference` `SearchByVector`) blends the already-computed ACT-R retention score (`confidence·e^(−λ·daysSinceAccess) + boost·access`, clamped to [0,1]) into ranking: `(1−w)·vectorScore + w·retentionScore`. **D2:** when `StructuralDecayGamma < 1`, GraphRAG `Graph`-mode traversal scores a neighbour at `h` hops as `seedScore·γ^h` (the previously-discarded hop distance). Both default **off** (`MemoryProfile.Parity` ⇒ weight 0 / γ 1.0) ⇒ byte-for-byte today's semantic-only ranking, and add **no** node property, label, index, or migration — so a profile can be raised over an existing (or upstream-parity-seeded) graph with no schema change. Verified by unit tests (query shape, profile/clamp) and live-Neo4j integration tests (recency reorders a stale top-similarity hit below a fresh one; γ halves a 1-hop neighbour's score). Design: `docs/decay-improvement-proposal.md §11`, plan `docs/Memory_Review_and_Implementation_Plan.md §II.8`.

## [0.1.0-preview.1] - 2026-06-06

First public preview release. This is a pre-release; public APIs may still change before 1.0.
NuGet package IDs are permanent once published.

### Added

#### Packages

- **`AgentMemory.Abstractions`** — Domain models (31 types across 3 memory tiers), service interfaces (`IMemoryService`, `IShortTermMemoryService`, `ILongTermMemoryService`, `IReasoningMemoryService`, `IMemoryContextAssembler`, `IMemoryExtractionPipeline`, `IEntityResolver`, and more), repository interfaces, and configuration options. Zero external dependencies except `Microsoft.Extensions.AI.Abstractions`.
- **`AgentMemory.Core`** — Memory service implementations, extraction pipeline (`ExtractionStage` → `PersistenceStage`), entity resolution chain (Exact → Fuzzy → Semantic → CreateNew), context assembler with token-budget enforcement, memory decay service (`MemoryDecayService` with configurable half-life), stub implementations for testing.
- **`AgentMemory.Neo4j`** — Neo4j repository implementations for all 9 domain repositories, centralised Cypher constants (145 in 14 domain files), schema bootstrapper and migration runner with versioned `.cypher` files, GraphRAG retrieval layer (Vector, Fulltext, Hybrid, Graph) internalized from `neo4j-maf-provider`. DI: `AddNeo4jAgentMemory()`.
- **`AgentMemory.Extraction.Llm`** — LLM-driven entity, fact, preference, and relationship extractors using `IChatClient` from `Microsoft.Extensions.AI`. DI: `AddLlmExtraction()`.
- **`AgentMemory.Extraction.AzureLanguage`** — Azure Text Analytics extractors for named entity recognition, fact extraction, and PII detection. DI: `AddAzureLanguageExtraction()`.
- **`AgentMemory.AgentFramework`** — Microsoft Agent Framework adapter: `Neo4jMemoryContextProvider` (`IContextProvider`), `Neo4jChatMessageStore`, `Neo4jMicrosoftMemoryFacade`, `MemoryToolFactory` (6 `AIFunction` tools), `AgentTraceRecorder`. DI: `AddAgentMemoryFramework()`.
- **`AgentMemory.SemanticKernel`** — Semantic Kernel adapter: memory plugin, text search, native SK DI integration. DI: `AddAgentMemorySemanticKernel()`.
- **`AgentMemory.Enrichment`** — Nominatim geocoding service and Wikimedia entity enrichment, both with caching and rate limiting. DI: `AddEnrichment()`.
- **`AgentMemory.Observability`** — OpenTelemetry decorator pattern wrapping `IMemoryService` and `IGraphRagContextSource` with distributed tracing spans and metrics. DI: `AddAgentMemoryObservability()`.
- **`AgentMemory.McpServer`** — MCP server with 21 tools, 6 resources (`memory://conversations`, `memory://entities`, `memory://preferences`, `memory://context/{sessionId}`, `memory://status`, `memory://schema`), and 3 prompts. Supports stdio and HTTP transports. DI: `AddAgentMemoryMcpTools()`.
- **`AgentMemory`** — Convenience meta-package bundling `Abstractions` + `Core` + `Neo4j` + `Extraction.Llm` + `Observability` + `Enrichment` + `Extraction.AzureLanguage` (7 project references; pulls their transitive deps, e.g. OpenTelemetry and Azure.AI.TextAnalytics). Single install for the most common use case.

#### Memory capabilities

- **Short-term memory** — session-scoped conversation history with participant tracking, recent message recall, semantic vector search, batch add
- **Long-term memory** — entities with canonical names, aliases, and dynamic labels; facts as SPO triples with confidence and validity periods; preferences by category; relationships between entities; all backed by vector and fulltext search
- **Reasoning memory** — reasoning traces from agent chains, steps (thought/action/observation), tool call recording with status and outcomes, similar-trace retrieval
- **Memory decay (scoring + pruning)** — exponential decay-score formula (`confidence × exp(−λ×days) + boost×access`) with configurable half-life, access-tracking, and server-side prune. The Neo4j adapter (`Neo4jMemoryDecayService`) runs the decay Cypher and is wired by default (it `Replace`s the portable Core no-op). Pruning is **owner-scoped**: `PruneExpiredMemoriesAsync(MemoryScope? scope)` deletes the owner's own low-score nodes only (never another owner's, never shared/global); a null scope prunes globally (admin). Exposed via `agentmemory decay [--owner <id>]`.
- **Temporal recall** — `RecallAsOfAsync` and point-in-time snapshot queries across all memory tiers using native Neo4j `datetime()` comparisons
- **Context assembly** — multi-tier recall with configurable token budget, truncation strategies, and blending modes
- **Metadata filtering** — `MetadataFilterBuilder` with `$eq`, `$ne`, `$contains`, `$in`, `$exists` operators
- **Session ID strategies** — `PerConversation`, `PerDay`, and `PersistentPerUser` via `ISessionIdGenerator`

#### Multi-user & multi-store isolation

- **Multi-user memory isolation (R1).** Memories now carry an optional `owner_id` so a single store can
  hold per-user memory alongside shared/global memory. Reads are scoped through **`MemoryScope`**
  (`{ string? OwnerId, bool IncludeShared = true }`): `owner_id = $ownerId OR (includeShared AND owner_id
  IS NULL)`. A `null` owner means shared/global (the prior behaviour), so existing single-tenant callers
  are unaffected. Facts additionally carry an `owner_key = coalesce(owner_id, '*')` sentinel that is part
  of the Fact MERGE key, so the same SPO triple asserted by different owners stays distinct. Vector-index
  reads over-fetch (topK × 5, floor 50) and post-filter by owner to avoid per-owner starvation. Every
  read/write surface — short-term, long-term (entities, facts, preferences, relationships), reasoning
  traces/steps, temporal recall, and all four retrievers — is owner-aware.
- **Ambient owner context (`IMemoryOwnerContext` / `IWritableMemoryOwnerContext`).** `AsyncLocal`-backed
  `DefaultMemoryOwnerContext` lets adapters set the current user once per request/agent flow so the
  LLM-invokable facade tools scope by owner **without trusting the model** to pass an id. Safe to register
  as a singleton (the value flows per async context, not process-wide).
- **Multi-store / application isolation (R1b).** `MemoryStorageStrategy` (`SharedDatabase` |
  `DatabasePerApplication`) plus `IMemoryStoreContext` / `IMemoryStoreProvisioner` map an `ApplicationId`
  to a Neo4j database, with an `AsyncLocal`-backed `DefaultMemoryStoreContext` ambient. Defaults to
  `SharedDatabase` inheriting `Neo4jOptions.Database`, so existing deployments are unchanged.

#### Consolidation, dedup & extraction

- **Dedup-on-create for facts and preferences.** New `LongTermMemoryOptions`
  (`DeduplicateOnCreate = true`, `DeduplicationSimilarityThreshold = 0.95`, `DeduplicationConfidenceBump
  = 0.05`): on add, a near-duplicate (same subject/predicate/owner for facts; same category/owner for
  preferences, above the similarity threshold) is updated in place with a confidence bump instead of
  creating a second node.
- **Consolidation / hygiene service (`IConsolidationService`).** Opt-in maintenance pass
  (`ConsolidationOptions`, `DryRun = true` by default) that archives expired conversations, removes
  duplicate preferences, and reports duplicate entities and over-long reasoning traces, emitting a
  `ConsolidationReport` and recording each run. Backed by migration `0004_consolidation.cypher`.
- **Conflict / contradiction detection (`IConflictDetectionService`).** Detect-only (never mutates):
  finds fact contradictions — same subject + predicate within an owner scope asserting ≥2 distinct
  objects — grouped per owner so it respects R1 isolation, with an optional confidence gate. Pairs with
  the consolidation service for the memory-hygiene story. Exposed via the `agentmemory conflicts` CLI command.
- **Streaming (chunked) extraction is DI-registered** (`IStreamingExtractor`). It is a standalone
  text → chunks → entities helper and does **not** persist; callers persist its output through their
  own ingestion path (where owner stamping applies). A built-in streaming persistence path is not
  yet wired.
- **`Conversation.Archived` reads back.** The consolidation pass sets `archived` on expired
  conversations; the domain model and `Neo4jConversationRepository` mapping now surface it (archival
  remains a consolidation-only write, not an upsert). `ConsolidationRun` is now declared in
  `SchemaConstants.NodeLabels` for parity with the Cypher that creates it.
- **MCP `memory_add_fact` accepts `category` and `metadata`.** The tool now surfaces the `Fact.Category`
  and `Fact.Metadata` fields (metadata as a JSON-object string) that were previously dropped.

#### Operational safety

- **Vector-index dimension validation at bootstrap.** Schema bootstrap (and per-application store
  provisioning) verifies every existing Neo4j vector index was created with the configured
  `Neo4jOptions.EmbeddingDimensions`, throwing `EmbeddingDimensionMismatchException` (which lists each
  offending index) when they differ. Because `CREATE VECTOR INDEX ... IF NOT EXISTS` never alters an
  existing index, switching embedding models would otherwise produce an opaque query-time failure; this
  is a fail-fast guard. Opt out via `Neo4jOptions.ValidateVectorIndexDimensions = false` (default `true`).

#### Reasoning provenance

- **`:TOUCHED` reasoning-audit edges.** `IReasoningMemoryService.RecordTouchedEntitiesAsync` /
  `GetTouchedEntitiesAsync` record and read which entities a reasoning step read or acted upon, as
  `(:ReasoningStep)-[:TOUCHED]->(:Entity)` edges (a `recorded_at` timestamp is stamped on create).
  Linking is by entity id to **existing** entities (it never creates entities, preserving the
  resolution/dedup pipeline), is idempotent, and silently skips ids that do not resolve. Ports the
  upstream `(:ReasoningStep)-[:TOUCHED]->(:Entity)` provenance edge (neo4j-labs/agent-memory PR #113).
- **Point-in-time reasoning-trace recall.** `IReasoningMemoryService.SearchSimilarTracesAsOfAsync` and
  `IReasoningTraceRepository.SearchByTaskVectorAsOfAsync` restrict task-vector search to traces that had
  started at or before the as-of instant, and `MemoryContextAssembler.AssembleContextAsOfAsync` now
  includes reasoning traces (previously omitted) — completing temporal recall across all memory tiers.

#### Entity auditability & feedback

- **`memory_get_entity_provenance` MCP tool.** Surfaces the (already-implemented) `EntityProvenance` —
  the source messages an entity was extracted from (with span/confidence) and the extractors that
  produced it — for auditability.
- **`Entity.UpdatedAtUtc` reads back.** Entity nodes already stamped `updated_at` on modification; the
  domain model and `Neo4jEntityRepository` mapping now surface it (last-modified semantics — null until
  first update), exposed on `memory_get_entity`.
- **Entity feedback.** `ILongTermMemoryService.RecordEntityFeedbackAsync` (and the
  `memory_record_entity_feedback` MCP tool) nudge an entity's confidence — positive reinforces, negative
  penalizes — clamped to [0,1], with the magnitude configurable via
  `LongTermMemoryOptions.FeedbackConfidenceDelta` (default 0.1). Owner-scoped (R1): with a `userId`/scope
  it only affects the user's own or shared entities, never another user's private entity.

#### Operational tooling

- **`agentmemory` CLI** (`tools/AgentMemory.Cli`) — an operations command-line front end over the
  shipped maintenance services: `migrate` (apply Cypher migrations), `bootstrap` (create schema
  constraints/indexes), `consolidate [--apply]` (memory-hygiene pass, dry-run by default),
  `conflicts` (detect fact contradictions), and `decay [--owner <id>]` (prune decayed memories;
  owner-scoped, or global when omitted). Connection resolves from CLI options, `Neo4j:*`
  config, or `NEO4J_*` env vars. Built for CI/CD migrations, K8s init containers, and scheduled
  pruning. (Not a published NuGet package.)

#### Search and retrieval

- Vector similarity search across all memory layers (5 indexes + reasoning-step index)
- Fulltext BM25 search (3 indexes: message content, entity name, fact content)
- Hybrid retrieval (vector + BM25 combined with max-score merge)
- Graph multi-hop traversal (`RELATED_TO*1..2`) via `Neo4jGraphRagContextSource`
- Temporal point-in-time retrieval for entities, facts, and preferences

#### Graph schema

- 13 node labels, 11 uniqueness constraints
- 6 vector indexes, 3 fulltext indexes, 1 geospatial Point index, plus owner-scope and consolidation property indexes
- Versioned migration runner (`MigrationRunner`) with `.cypher` migration files

#### Testing

- Extensive unit test suite covering all packages including stub-based tests without external services
- Integration test suite using Testcontainers (disposable Neo4j 5 containers)
- Semantic Kernel adapter unit tests
- Cypher snapshot tests for query validation

### Changed

> The items below describe API shaping done before first publish.

- **`IMemoryService` split into role interfaces** (`AgentMemory.Abstractions`). Its members are now
  declared on three focused interfaces — `IMemoryRecall` (read), `IMemoryIngestion` (write), and
  `IMemoryMaintenance` (upkeep) — and `IMemoryService` composes all three
  (`IMemoryService : IMemoryRecall, IMemoryIngestion, IMemoryMaintenance`). Consumers of
  `IMemoryService` are source-compatible (all members remain available); new code can depend on a
  narrow role for ISP. DI binds all three roles to the same scoped instance.
- **`IEmbeddingOrchestrator` slimmed to two primitives** (`AgentMemory.Abstractions`). The interface
  now declares only `EmbedAsync(string)` and the new `EmbedBatchAsync(IReadOnlyList<string>)`. The
  six domain-specific methods (`EmbedEntityAsync`, `EmbedFactAsync`, `EmbedPreferenceAsync`,
  `EmbedMessageAsync`, `EmbedQueryAsync`, `EmbedTextAsync`) are preserved as **extension methods**
  in `EmbeddingOrchestratorExtensions` (same namespace), so call sites that `using
  AgentMemory.Abstractions.Services` are source-compatible. Code that *implements* or *mocks*
  `IEmbeddingOrchestrator` must now implement/mock `EmbedAsync`/`EmbedBatchAsync`.
- Renamed all NuGet packages from `Neo4j.AgentMemory.*` to `AgentMemory.*` to remove the implied Neo4j
  affiliation before first publish. C# namespaces updated accordingly across all 11 source packages, 3
  test projects, and 3 sample projects.
- **Retrieval blend modes are now enforced.** `RecallOptions.BlendMode` previously had no effect —
  every mode behaved like `Blended`. `MemoryContextAssembler` now honors it: `MemoryOnly` suppresses
  GraphRAG, `GraphRagOnly` suppresses the memory layers (and the query-embedding call), and
  `GraphRagOnly`/`GraphRagThenMemory` render GraphRAG context ahead of memory in both
  `MemoryContextFormatter` and the MAF context mapper. `MemoryContext` gains a `BlendMode` property
  (defaults to `Blended`, so existing output ordering is unchanged).
- **Microsoft Agent Framework (MAF) 1.9.0** (from 1.1.0) and `Microsoft.Extensions.AI.Abstractions`
  10.5.1 (from 10.4.1, the floor MAF 1.9.0 requires). The migration was source-compatible — no adapter
  code changes — see `docs/archive/maf-1.9.0-migration.md`.

### Fixed

- **Batch entity upsert now persists geospatial `location`.** `Neo4jEntityRepository.UpsertBatchAsync`
  set embeddings, labels, and provenance but silently dropped `Latitude`/`Longitude`, so entities
  created via the batch path had no `location` point and were invisible to `SearchByLocationAsync` /
  `SearchInBoundingBoxAsync` (single `UpsertAsync` already persisted it). The batch now writes the
  point for every entity with both coordinates, matching the single path. Covered by single + batch
  round-trip integration tests (model coords → read-back → spatial search) and batch unit tests.
- **Owner-scoped entity resolution (R1 isolation hardening — fixes a cross-owner write-path leak).**
  Entity resolution fetched its candidate set via an unscoped read, so when extracting for user A an
  incoming entity could exact/fuzzy/semantic-match onto user B's **private** entity and auto-merge into
  it (aliases/sources appended; the foreign node's `owner_id` then re-stamped at persistence). The
  resolution read is now owner-scoped: `MemoryScope?` flows through `IEntityResolver.ResolveEntityAsync` /
  `FindPotentialDuplicatesAsync`, the extraction stage, and `MemoryExtractionPipeline` (derived from
  `ExtractionRequest.UserId`), down to a new owner-conditional `IEntityRepository.GetByTypeAsync(…, scope)`.
  Shared/global entities (`owner_id IS NULL`) stay matchable by everyone; a null scope reproduces the
  prior single-tenant behavior. Proven by live cross-owner resolution-isolation tests.
- **Scope-optional hooks on dedup/name reads (R1).** `Neo4jEntityRepository.SearchByNameAsync`,
  `IEntityRepository.FindSimilarByEmbeddingAsync`, and `IFactRepository.FindByTripleAsync` gained an
  optional `MemoryScope` (default global) so a future user-facing wiring can confine them; the entity
  name search also fixes a latent precedence bug by parenthesizing the name/canonical-name OR. The
  remaining unscoped reads (by-id lookups, background embedding back-fill, and admin dedup/provenance
  surfaces) are intentionally global — documented inline, since the lookup key is itself an owned handle
  or the surface is operator-only.
- **MCP resources `memory://entities` and `memory://preferences` are now owner-scopable (R1).** Both
  listed every owner's nodes via raw Cypher with no owner filter and no user parameter — a cross-owner
  read leak (preference free-text is sensitive). They now accept an optional `userId` that confines the
  listing to that owner's plus shared rows; omitting it stays unscoped (admin/single-tenant), consistent
  with the MCP tools. `memory://status` stays global (aggregate counts only).
- **Cross-owner write/delete/merge denial (R1 isolation hardening).** `IEntityRepository.DeleteAsync` /
  `IFactRepository.DeleteAsync` / `IPreferenceRepository.DeleteAsync` / `ILongTermMemoryService.DeletePreferenceAsync`,
  `Neo4jEntityRepository.MergeEntitiesAsync`, and the spatial reads (`SearchByLocationAsync` /
  `SearchInBoundingBoxAsync`) now take an optional `MemoryScope`. When scoped, a delete only removes the
  owner's **own** node (never another owner's, and never shared/global data), a merge cannot cross the
  owner boundary, and spatial search can't enumerate another owner's locations. Previously these matched
  by id/coordinates with no owner check — a destructive multi-tenant gap. Unscoped (null) stays
  admin/global for back-compat. Covered by cross-owner integration tests.
- **The meta `AddNeo4jAgentMemory` is now self-sufficient.** It now registers default `IClock`
  (`SystemClock`) and `IIdGenerator` (`GuidIdGenerator`) via `TryAdd`, so consolidation, reasoning,
  the context assembler, and dedup resolve out of the box. Previously these were *registered* but not
  *resolvable* — every consumer (and every sample) had to register the two primitives by hand. Consumers
  can still override by registering their own first.
- **`DatabasePerApplication` provisioning now works.** `Neo4jMemoryStoreProvisioner` inlined the store
  database name into `CREATE DATABASE … IF NOT EXISTS WAIT` unquoted. The default `DatabasePrefix` is
  `mem-`, so every provisioned name contains a dash — which is a Cypher syntax error unquoted, breaking
  store provisioning for all real `DatabasePerApplication` users. The name is now backtick-quoted.
  (Caught by a new live Neo4j Enterprise integration test; previously only mock-tested.)
- **Memory-only DI now works.** Two `AddAgentMemoryCore` registrations failed at runtime for consumers
  that don't add the GraphRAG adapter: `MemoryContextAssembler` required `IGraphRagContextSource`
  (now resolved optionally via `GetService`), and `IMemoryExtractionPipeline` was registered by type
  despite an internal constructor (now registered via a factory).
- **`ReasoningStep.TimestampUtc` / `ToolCall.TimestampUtc` are now read back.** Both nodes are created
  with a server `timestamp` that was previously write-only from .NET; the domain records gained the
  property and the Neo4j mappers now populate it.
- **`Fact.Category` is now persisted and read back.** It was defined on the domain model and indexed
  (`fact_category`) but omitted from the upsert queries and mapping, so it was silently dropped on
  write and always returned `null`. `FactQueries.Upsert`/`UpsertBatch`, the repository parameters, and
  `MapToFact` now round-trip it (mirroring `Preference.Category`).
- **CI now builds and tests.** `.github/workflows/squad-ci.yml` was a placeholder that ran no
  commands; it now restores, builds, and runs unit + SemanticKernel tests plus the Testcontainers
  integration suite. Fixed `Directory.Build.props` so the src-only `TreatWarningsAsErrors` condition
  evaluates correctly on non-Windows CI runners, and tagged `Neo4jConnectivityTests` with
  `[Trait("Category", "Integration")]` so it no longer leaks into the unit-test filter.

---

[Unreleased]: https://github.com/joslat/agent-memory-dotnet/compare/v0.1.0-preview.1...HEAD
[0.1.0-preview.1]: https://github.com/joslat/agent-memory-dotnet/releases/tag/v0.1.0-preview.1
