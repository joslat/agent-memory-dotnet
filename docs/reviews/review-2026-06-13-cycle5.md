# Throat-check review — 2026-06-13 (cycle 5)

**Scope:** adversarial review of genuinely-unreviewed **correctness** surface (prior cycles focused on
isolation/durability): the GraphRAG retrieval layer (Vector/Fulltext/Hybrid/Graph — score blending,
fulltext/Lucene handling, traversal), the context assembler's token-budget/truncation logic, and the MCP
resources/prompts + the MCP tools other than `AdvancedMemoryTools` (reviewed in cycle 3). **Method:** 3
dimension scanners (`Explore`) → per-finding adversarial verification (skeptic defaults to *reject*).
**14 candidates → 6 confirmed.** All fixed in this cycle.

## Findings (ranked)

| # | Severity | Area | Title | Status |
|---|---|---|---|---|
| 1 | 🟥 High | MCP | `memory_get_conversation` has no owner scope → cross-owner message read | ✅ fixed |
| 2 | 🟧 Medium | GraphRAG | Fulltext raw-query path doesn't escape Lucene metacharacters → parse error / altered recall | ✅ fixed |
| 3 | 🟧 Medium | MCP | `memory_list_sessions` has no owner scope → cross-owner session/metadata enumeration | ✅ fixed |
| 4 | 🟡 Low | GraphRAG | Hybrid merge compares raw cosine vs unbounded BM25 → keyword frequency dominates ranking | ✅ fixed (RRF) |
| 5 | 🟡 Low | Assembler | Proportional GraphRAG truncation splits UTF-16 surrogate pairs (emoji) → orphaned surrogate | ✅ fixed |
| 6 | 🟡 Low | MCP | Pagination/budget params (`limit`/`offset`/`maxTokens`) unvalidated → DB error / resource exhaustion | ✅ fixed |

---

### 1 — `memory_get_conversation` returns any conversation's messages with no owner check
**High · `src/AgentMemory.McpServer/Tools/ConversationTools.cs`**

`memory_get_conversation` took only `conversationId` and called `GetConversationMessagesAsync` with no owner
scope. A `conversationId` is **not** a private random handle — it defaults to the (guessable) session id
(`cid = conversationId ?? sid`) and is enumerable via the owner-scoped `memory://conversations` resource. So a
multi-tenant MCP client could pass another owner's conversation id and read their messages. This is exactly the
"multi-row read keyed by a non-private handle" class the project's own R2 work hardened (`ListTracesAsync`,
`ConversationListResource`); this tool was simply missed. (High, not critical — the whole MCP surface is opt-in
single-tenant by default: `null` userId = unscoped/admin.)

**Best fix (applied):** mirror the R2 pattern. Added an optional `userId`; when set, fetch the conversation via
`IConversationRepository.GetByIdAsync` and **deny** (return an empty array) unless it is un-attributed
(`UserId == null`, shared) or owned by `userId`. No change to the message read path, no new owner column on
`Message`. **Tests:** another owner's id → empty *and* the message read is never called; own/shared → returned.

### 2 — Fulltext raw-query path passes unescaped Lucene metacharacters
**Medium · `src/AgentMemory.Neo4j/Retrieval/Internal/FulltextRetriever.cs`**

On the `filterStopWords = false` path (the default for the **Hybrid** retriever's fulltext leg), the raw query
text was bound straight into `db.index.fulltext.queryNodes`. Lucene then interprets metacharacters
(`+ - && || ! ( ) { } [ ] ^ " ~ * ? : \ /`) as operators, so an ordinary query — `C++ vs Rust: faster?`, an
unbalanced quote/paren — either throws an **unhandled** Lucene parse error or silently changes recall (a
leading `-` becomes NOT). (It's a *bound* parameter, so **not** Cypher injection and no owner-scope bypass —
hence Medium, a robustness defect on the caller's own query. The default `filterStopWords = true` path was
already safe: it tokenizes to `\w+` and drops every metacharacter.)

**Best fix (applied):** a small `LuceneQueryEscaper` that backslash-escapes the metacharacters, applied only on
the raw-query branch (treat the user's text as **literal** search terms — the natural intent for a recall
query; the stop-word path stays untouched). **Tests:** every metacharacter is escaped; plain text and
null/empty are unchanged.

### 3 — `memory_list_sessions` enumerates any session's conversations with no owner check
**Medium · `src/AgentMemory.McpServer/Tools/ConversationTools.cs`**

The sibling of #1: `memory_list_sessions` took only `sessionId` and returned every conversation in that session
(including each `user_id`) with no owner filter — leaking other owners' session ids / metadata to a client that
knows/guesses a session id. Lower than #1 (metadata, not message content), but the same missed-hardening class
(the parallel `ConversationListResource` *was* scoped).

**Best fix (applied):** added an optional `userId`; when set, filter the returned conversations to the owner's
own + un-attributed rows. **Test:** scoped to `alice` excludes `bob`'s conversation, keeps the shared one.

### 4 — Hybrid retrieval compares incomparable score scales
**Low · `src/AgentMemory.Neo4j/Retrieval/Internal/HybridRetriever.cs`**

The hybrid retriever merged vector and fulltext results and ordered by raw `Metadata["score"]` — but vector
scores are cosine in `[0, 1]` while fulltext scores are **unbounded BM25**. So in the default **Hybrid** mode
the final `OrderByDescending(score)` let high-magnitude BM25 (keyword frequency) items systematically outrank
semantically-relevant cosine items, defeating the point of blending. (Ranking-quality only — no crash/leak,
hence Low. Note: `docs/architecture.md` already *claimed* "RRF fusion" that didn't exist — this fix makes the
claim true.)

**Best fix (applied):** replace the raw-score merge with **Reciprocal Rank Fusion** (`score = Σ 1/(k+rank)`,
`k = 60`) — a scale-free combiner that compares the two lists by *rank*, not by their incomparable raw scores,
and reinforces items appearing in both. Extracted as `HybridRetriever.FuseReciprocalRank(...)` for testing.
**Tests:** an item in both lists outranks single-list items; within-list order preserved; `topK` respected.

### 5 — Proportional truncation can split a surrogate pair
**Low · `src/AgentMemory.Core/Services/MemoryContextAssembler.cs`**

The proportional GraphRAG truncation sliced by UTF-16 char index (`graphRag[..budget]`). A non-BMP character
(emoji) is a 2-unit surrogate pair, so a cut landing between the units leaves an orphaned surrogate. (Opt-in
strategy — default is `OldestFirst`; the downstream `System.Text.Json` encoder emits `�` rather than throwing —
hence Low/cosmetic.)

**Best fix (applied):** extracted `TruncateToCharBudget(text, budget)` that backs the cut off by one when it
lands on a low surrogate, so pairs stay whole. **Tests:** ASCII budgets; the emoji case; and an exhaustive
sweep asserting no lone surrogate is ever produced across all budgets.

### 6 — Unvalidated pagination/budget parameters
**Low · MCP resources & tools (`EntityListResource`, `ConversationListResource`, `PreferenceListResource`, `AdvancedMemoryTools`, `ObservationTools`)**

`limit`/`offset`/`maxTokens` were raw ints with no validation. A negative `SKIP`/`LIMIT` is a Neo4j error
(ungraceful exception to the client), and a huge `limit` is a resource-exhaustion vector. (Input hygiene — no
data leak, hence Low.)

**Best fix (applied):** clamp at each entry point — `limit = Math.Clamp(limit, 1, 1000)`,
`offset = Math.Max(0, offset)`, `maxTokens = Math.Clamp(maxTokens, 256, 100_000)` — friendlier than throwing
for list endpoints and caps the exhaustion vector.

---

## Changes in this cycle
- `ConversationTools` — owner scope on `memory_get_conversation` (via `IConversationRepository`) and
  `memory_list_sessions` (#1, #3).
- `LuceneQueryEscaper` (new) + `FulltextRetriever` — escape the raw fulltext query (#2).
- `HybridRetriever` — RRF fusion, extracted as `FuseReciprocalRank` (#4).
- `MemoryContextAssembler` — surrogate-safe `TruncateToCharBudget` (#5).
- Pagination/budget clamps across 5 MCP resources/tools (#6).
- **Tests:** `ConversationToolsTests` (+4), `HybridFusionAndEscapingTests` (new), 
  `MemoryContextAssemblerTruncationTests` (new). Full unit suite green: **2472 passed**.

## Series note
Cycles 1–5 are complete. Confirmed/candidate ratios fell across the series (cycle-4 6/36, cycle-5 6/14 but
mostly Low) — the codebase is solid. Remaining un-reviewed surface is thin (deep Enrichment paths, samples).
