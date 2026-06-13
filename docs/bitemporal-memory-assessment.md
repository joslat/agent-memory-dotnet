# Bitemporal Memory & "Forgetting" for agent-memory-dotnet — Assessment, Design & Upstream Proposal

> **Status:** Design / discussion document. **No code is changed by this document.**
> **Date:** 2026-06-07 (revised after live-upstream re-verification + the decay-vs-deletion debate)
> **Scope:** (1) Does agent-memory-dotnet have *full bitemporal* support? (2) What would "full bitemporal" add? (3) The harder question raised by upstream issue #42 and the moltbook post: is **decay/forgetting** actually *better* than bitemporal — and is forgetting **dangerous**? (4) What I would do, and what I'd want *my* memory to do. (5) A ready-to-file upstream issue.

---

## 0. What changed in this revision (read this first)

The first draft checked a **local clone** of the upstream Python project. The live `neo4j-labs/agent-memory` repo (`main` @ `f29ae8d`, pushed 2026-06-03; v0.4.0 tagged 2026-05-17, heading to v0.5.0) has moved on, so I re-verified everything against it (authenticated `gh` + raw source), and I read upstream **issue #42**, the **moltbook** post it cites, and the broader research on whether forgetting is dangerous. Three things changed my conclusions:

1. **Upstream now ships a (narrow) point-in-time path** — `as_of=` for **Preferences only** — and a *working* supersession writer (`supersede_preference()`). So "as-of is .NET-only" is now **wrong** (corrected in §6). Neither side is *truly* bitemporal yet.
2. **The decay debate is a category error in disguise.** Issue #42 and the moltbook post praise decay as **retrieval re-ranking that keeps the data** — *"Not deleting old data, just deprioritizing it in search results."* That is **not** an argument against bitemporality; the two are orthogonal and **combine** (§4).
3. **agent-memory-dotnet does the one thing everyone warns against.** Its decay is `DETACH DELETE` (irreversible deletion), and its ACT-R-style retention score is **never used for ranking** — only to decide what to delete. It has the right cognitive model wired to the wrong action (§4, §5, §9).

**Bottom line:** the highest-value move is *not* "add full bitemporal." It is **(a) stop deleting on decay** (soft-invalidate/archive instead) and **(b) start using the decay score as a retrieval re-ranker** (which it computes but ignores). Full bitemporal storage is the substrate that makes both safe and auditable.

---

## 1. TL;DR

**Does agent-memory-dotnet have full bitemporal support? No.** It has uni-temporal, single-clock point-in-time recall plus a real valid-time window on facts/relationships — but the transaction-time axis (`invalidated_at`) is **read by queries yet never written** (dead/orphan), and the "forget" path **physically deletes** (`DETACH DELETE`). So it behaves as a **single-clock, overwrite-and-delete** store. The original framing — *"point-in-time + decay; needs explicit transaction-time + invalidate-not-delete"* — is correct.

**Is decay better than bitemporal (issue #42)? Wrong question — they're different layers.** "Decay" in issue #42 / moltbook means **re-ranking**: down-weight old/unused memories *in search results* while **keeping every record**. That is a *read-time relevance* feature. Bitemporal is a *storage* property. You want **both**: recency-aware ranking *on top of* invalidate-not-delete storage.

**Is forgetting dangerous? Only when "forgetting" means deletion.** Re-ranking decay (keep data, lower its score) is low-risk and improves retrieval. **Deletion** decay is the dangerous one: it's irreversible, erases rare-but-critical facts, breaks audit/reproducibility, and is an attack surface (memory-poisoning / induced-forgetting). agent-memory-dotnet currently does the dangerous kind.

---

## 2. What "bitemporal" means, in plain language

A **bitemporal** store tracks every fact on **two independent clocks**:

| Clock | Plain-language question | Editable? | Synonyms |
|---|---|---|---|
| **Valid time** (`valid_from`/`valid_until`) | *"When was this true in the real world?"* | **Yes** — backdate/correct when you learn the truth later | application time, effective time |
| **Transaction time** | *"When did our system record/believe this?"* | **No** — append-only, never rewritten | system time, system-versioned |

Companion principle: **invalidate-not-delete** — when something changes, **close** the old version (stamp its end) and **append** a new one; never overwrite or physically delete. Because nothing is destroyed, you can reconstruct the past on either clock. (This is standard: SQL:2011 implements it as two pairs of ordinary timestamp columns + rules; XTDB and Zep/Graphiti do the graph version.)

It unlocks four capabilities: **as-of-valid** ("what was true at X?"), **as-of-system** ("what did we believe at X?"), **retroactive correction with audit** (fix the past, keep the wrong version on record), and **distinguish "the world changed" from "we were wrong."**

---

## 3. Where agent-memory-dotnet stands today (verified against the code)

| Node / edge | Valid-time window | Record clock | Version history? | Source |
|---|---|---|---|---|
| **Fact** | ✅ `valid_from`/`valid_until` | `created_at`, `updated_at` | ❌ overwrite-in-place | `FactQueries.cs:15-67` |
| **Relationship (`RELATED_TO`)** | ✅ | `created_at`, `updated_at` | ❌ | `RelationshipQueries.cs:13-41` |
| **Entity** | ❌ | `created_at`, `updated_at` | ❌ | `EntityQueries.cs:15-42` |
| **Preference** | ❌ | `created_at` only | ❌ | `PreferenceQueries.cs:11-29` |
| **ReasoningTrace** | ❌ | `started_at`, `completed_at` | ❌ | `ReasoningQueries.cs:11-38` |

**Three load-bearing facts (verified):**
- **Single clock at query time.** Every as-of entry point takes one `DateTimeOffset asOf` and binds it to *both* the record clock and the valid-time window (`TemporalQueries.cs:49-53`). You cannot ask "what was true on date X *as we knew it on date Y*."
- **The transaction clock is dead.** `invalidated_at` is read by every as-of query but **written by nothing** — no `SET`, no `InvalidateAsync`, not even a `SchemaConstants` constant. So `invalidated_at IS NULL` is always true; every record reads as "valid forever" unless deleted. (The repo's own parity doc warns against exactly this "orphan schema" — `schema-parity-assessment.md:80`.)
- **Forgetting = deletion.** `DecayQueries.BuildPrune` ends in `DETACH DELETE` (`DecayQueries.cs:56`), removing the node and all its edges, for Entity/Fact/Preference whose retention score falls below threshold. It's live (`Neo4jMemoryDecayService.cs:55-84`, CLI `agentmemory decay`). Pruned = irrecoverable.

**And the detail that reframes everything:** the decay retention score —
`confidence · e^(-λ·daysSinceAccess) + boost · accessCount`, ~30-day half-life, access boost (`DecayQueries.cs:54-55`) — is the **exact ACT-R model** the moltbook post describes. But it is used **only as a deletion gate**, and is **never blended into retrieval ranking** (all searches `ORDER BY` the raw vector/fulltext `score` — `FactQueries.cs:113-121`). Right signal, wrong action; and absent from the action where it would actually help.

> Conflict/contradiction detection already exists but is **detect-only** (`IConflictDetectionService.cs`) — it never closes the loser. So the project has the *detection* half of supersession but not the *act*.

---

## 4. The crux: "decay" means two completely different things

This is the heart of the issue-#42 challenge. Conflating these two is the mistake:

| | **Decay-as-re-ranking** (issue #42, moltbook, Mem0, ACT-R) | **Decay-as-deletion** (agent-memory-dotnet today) |
|---|---|---|
| What it does | Lowers a memory's **retrieval score**; old/unused items sink in results | **Physically removes** the node (`DETACH DELETE`) |
| Data kept? | **Yes** — still exists, still findable by explicit/best-match search | **No** — gone forever |
| Reversible? | Yes (re-access boosts it back) | No |
| Effect on retrieval | **Improves** precision (drops the noise floor) | N/A (data is gone) |
| Compatible with bitemporal? | **Yes — orthogonal** (read-time scoring over a retain-everything substrate) | **No — actively destroys history** |
| Risk | Low | High (see §5) |

**What issue #42 actually says** (verified live via `gh`; open, by maintainer *johnymontana*, 0 comments): it proposes *"a re-ranker component… pluggable and extendable"* and *"an exponential time decay function based re-ranker as the default… that prioritises more recent memories."* The only thing it argues against is *"store everything forever with equal weight"* (naive equal-weight retrieval) — **it never mentions bitemporal at all.** The moltbook quote it embeds is explicit: *"**Not deleting old data, just deprioritizing it in search results.**"* (ACT-R, ~30-day half-life, +0.3 boost on retrieval; Ebbinghaus "≈70% forgotten in 24h" as the cognitive basis.)

**They are not in tension — they compose.** The moltbook *comment thread itself* proposes the combined design: *"keeping everything (audit trail) but applying sigmoid decay to retrieval ranking… the archive stores everything with timestamps."* That is precisely **decay-as-re-ranking on top of invalidate-not-delete bitemporal storage.** Re-ranking is a read-time scoring layer; bitemporal is a storage property; you can — and should — have both.

So the honest verdict on "decay vs bitemporal": **issue #42 is right that recency-decay improves retrieval — and it is not an argument against bitemporality.** agent-memory-dotnet's mistake isn't "having decay"; it's that its decay is *deletion* and its retrieval *ignores* recency.

---

## 5. Is forgetting dangerous? (pros, cons, evidence)

Short answer: **re-ranking forgetting is safe and good; deletion forgetting is the dangerous kind.** The evidence:

**The case FOR forgetting (real, but narrower than it looks):**
- **Over-retention genuinely hurts.** In the EHRAgent study, an "add-all" memory policy collapsed to **13% accuracy at 2,411 records**, while a curated policy hit **38% at 1,012** (`arxiv 2505.16067`). *But this is evidence against keeping **low-quality** records — not against keeping **old-but-true** ones.*
- **Retrieval precision / cost.** Stale recommendations ("use React not Vue") and 50-results-half-outdated degrade answers; FadeMem "retains 82.1% of critical facts while using only 55% of the storage" (`arxiv FadeMem`).
- **Cognitive plausibility.** The Ebbinghaus power-law forgetting curve is one of the most robust results in cognitive psychology; ACT-R models recall probability as activation that decays with recency/frequency.

**The case AGAINST forgetting — and it lands almost entirely on *deletion*:**
- **Irreversible loss of rare-but-critical facts.** *"Overly aggressive pruning risks erasing rare but essential knowledge, harming reasoning continuity in long-term contexts"* (`arxiv 2512.13564`). You can't recover what you deleted.
- **Breaks audit, provenance, reproducibility.** You can no longer answer "what did we know when we decided D?" — fatal for finance/healthcare/regulated/high-trust agents.
- **It's an attack surface.** Memory-poisoning (AgentPoison: <0.1% injection → ~82% retrieval-hijack) and sleeper attacks (MINJA) plant persistent false memories; **induced-forgetting** can evict *true* safety-relevant facts. A delete-by-default policy hands attackers a deletion primitive; an over-retain policy lets poison persist. Re-ranking + invalidate-not-delete is the only stance that lets you *review and revert*.
- **Compliance pulls both ways.** GDPR Art. 17 says *delete personal data on request*; EU AI Act Arts. 12/72 say *keep high-risk logs up to 10 years*. The resolution is **tiering** (session context vs extracted facts vs audit records) and pseudonymization — **not** blanket time-based deletion.

**Where the leaders stand (instructive — nobody serious hard-deletes by default):**
- **Zep / Graphiti:** true bitemporal, **invalidate-not-delete**, **no automatic time decay**; on contradiction they *"invalidate the affected edges"* but never discard. (Their rerankers are RRF/MMR/cross-encoder/episode-mentions/node-distance — relevance, not deletion.)
- **Mem0 / FadeMem:** embrace forgetting but as a **soft search-time rescale** (Mem0: 1.5× boost on recall, dampen toward 0.3× when unused — *"Memory Decay is not an eviction strategy. Nothing gets removed… still surfaces when it is genuinely the best match"*). FadeMem protects high-importance memories with hysteresis.
- **Upstream `neo4j-agent-memory`:** its whole hygiene layer is **non-destructive** — `dedupe_entities` writes a `:SAME_AS` edge (keeps both), `archive_expired_conversations` sets `archived=true` (keeps data), `supersede_preference` writes `valid_until` + `:SUPERSEDED_BY` (keeps the old). Its "decay" is a docs recipe that **lowers confidence, never deletes**, and issue #42's decay is a **re-ranker**.

**agent-memory-dotnet is the outlier** — the only one of these that *deletes* as its forgetting mechanism.

---

## 6. Live upstream re-verification (corrects the earlier draft)

Verified against `neo4j-labs/agent-memory` `main` @ `f29ae8d` (2026-06-03):

- **NEW: a Preferences-only `as_of` time-travel path.** `LongTermMemory.get_preferences_for(..., as_of=...)` adds `(p.valid_from IS NULL OR p.valid_from <= datetime($as_of)) AND (p.valid_until IS NULL OR p.valid_until > datetime($as_of))` (`long_term.py:788-793`). Its docstring calls it *"the v0.5 bi-temporal time-travel API."* (It lives in `long_term.py`, not `queries.py` — which is why a query-constants check misses it.)
- **NEW: a working supersession writer.** `supersede_preference()` does `MERGE (old)-[:SUPERSEDED_BY]->(new) SET old.valid_until = coalesce(old.valid_until, datetime())` (`long_term.py:747-755`); `detect_superseded_preferences()` is a consolidation primitive. **This "close the old record" write path is exactly what .NET lacks** (.NET reads `invalidated_at` but never writes it). So in this one narrow respect, upstream is *ahead* of .NET.
- **Still NOT bitemporal.** No `invalidated_at`/`expired_at`/`valid_at`/`invalid_at` anywhere in `src/` — there is **no transaction-time axis**. The `as_of` path is **valid-time only** (the "bi-temporal" docstring oversells it).
- **Facts/Entities/Relationships:** still plain vector / subject search, **no as-of**, nothing auto-closes their windows. Entities have **no** valid-time fields.
- **No shipped decay/forgetting.** Only a docs recipe (lower confidence) + the issue-#42 re-ranker proposal. Hygiene is non-destructive (`:SAME_AS`, `archived=true`). `DETACH DELETE` appears only in examples/tests/`clear_session`.

**Net:** *neither* implementation is truly bitemporal. Upstream has a narrow valid-time `as_of` **with** a supersession writer (Preferences); .NET has a broader `as_of` read (Facts/Entities/Preferences) but **without** a writer (`invalidated_at` is dead). Full bitemporality remains a deliberate **superset/divergence** for either project, not a parity gap — and the repo's stance already blesses additive, opt-in divergence (`schema-parity-assessment.md:11,44`).

---

## 7. Comparison: today vs. **with full bitemporal + recency re-ranking** (additive only)

Corrections to the earlier table footnoted (¹²).

| Capability | Today | + With the additions |
|---|---|---|
| Recall current memory (owner-scoped) | ✅ | — |
| "What did the agent believe as of date X" (single clock) | ✅ | — |
| **Recency/frequency-aware retrieval ranking** | ❌ score computed but **ignored** in ranking | ✅ blend the existing retention score into search ordering (the issue-#42 / moltbook win) |
| Facts with real-world validity windows | ✅ Facts **and** Relationships ¹ | Extend valid-time to Entities/Preferences |
| Decay / forgetting of low-value memory | ✅ but **destructive `DETACH DELETE`** | **Non-destructive** — re-rank + soft-invalidate/archive; explicit hard-purge only by policy |
| Mark a fact invalidated / superseded | 🟡 read filter only — `invalidated_at` never written ² | ✅ a writer stamps it; contradictions auto-close the loser |
| Reproduce a past decision exactly | ❌ (one clock) | ✅ independent transaction-time replay |
| Retroactive correction with audit | ❌ (overwrite) | ✅ both versions retained |
| Distinguish "world changed" vs "we were wrong" | ❌ | ✅ new valid-interval vs new transaction-version |
| Immutable audit / recover from memory-poisoning | ❌ (prune deletes) | ✅ nothing lost until explicit purge; forgetting is reversible |
| Belief-drift / "then vs now" diffing | ❌ | ✅ compare two transaction-time snapshots |

**¹** Valid-time is on **Fact *and* Relationship** (`RELATED_TO`), not facts only (the relationship window is stored-but-unread today). **²** `invalidated_at` is inert/orphan — read by as-of queries, written by nothing.

---

## 8. How I'd add this (design — additive, parity-safe)

Tagged `[additive]` (safe) vs `[behaviour change]` (needs an opt-in flag). Ordered by value-for-risk.

1. **Recency re-ranker `[additive]`** — blend the *already-computed* retention score into retrieval ordering (e.g. `final = vectorScore · w + retentionScore · (1-w)`), pluggable, opt-in, default-off → on. *This is the cheapest, highest-value change and directly implements the issue-#42 / moltbook insight. No schema change.*
2. **Make decay non-destructive `[behaviour change, opt-in]`** — change `DecayQueries.BuildPrune` from `DETACH DELETE` to `SET invalidated_at = datetime($now)` (or demote/`archived=true`). Keep a *separate, explicit* hard-purge for storage reclamation (and GDPR erasure). Resolves §5.
3. **Give the transaction clock a writer `[additive]`** — declare `InvalidatedAt` in `SchemaConstants`; add `InvalidateAsync`/`SupersedeAsync` that stamps it. Activates the dead read filters. (Upstream already does the equivalent for Preferences via `supersede_preference`.)
4. **Two-timestamp recall `[additive]`** — add `RecallAsOfAsync(request, validAsOf, systemAsOf)`; the existing single-`asOf` method delegates with both equal. New `TemporalQueries` variants bind each clock to its own parameter.
5. **Contradiction → supersession `[additive, opt-in]`** — let the existing detect-only conflict report optionally close the loser (`invalidated_at` / `valid_until`). Detection stays default.
6. **Extend windows to Entity/Preference `[additive]`** *(optional)* — if real-world validity matters for them.

Mechanical note: every changed const query must update `CypherQuerySnapshot.snap` + `ExpectedQueryCount` (these freeze exact query text on purpose).

---

## 9. What I would do (recommendation)

**Reframe the goal from "add bitemporal" to "make forgetting safe and retrieval recency-aware," with bitemporal storage as the substrate.** Concretely, in priority order:

1. **Stop deleting on decay (do this first, even before bitemporal).** Flip the prune to soft-invalidate/archive. Deletion is the only genuinely dangerous part, it's irreversible, and it contradicts the read model, upstream's philosophy, and every leader. Keep hard-delete as an *explicit policy* operation (GDPR/TTL), never the default.
2. **Use the decay score you already compute as a re-ranker.** This captures the proven retrieval-quality win (issue #42 / moltbook / Mem0) for almost no cost, and stops the wasteful situation where the score exists only to delete.
3. **Then build bitemporal as the substrate** (writer for `invalidated_at`, two-clock recall, contradiction→supersession) so the re-ranking sits over a retain-everything, auditable, poisoning-recoverable store.
4. **Importance-weighted half-lives.** Per moltbook's own finding: error-fixes, commitments, and relationships should decay slowest; pin identity/safety facts so they never sink or expire.
5. **Tier storage for compliance** (session vs facts vs audit) so GDPR-delete and AI-Act-retain can coexist.

This satisfies *both* camps: issue #42 gets its recency-aware retrieval; the "forgetting is dangerous" concern is answered by never irreversibly deleting.

---

## 10. What I'd want **my** memory to do

Since this *is* my memory system, my honest preference:

- **Forget like human *recall*, not like *rm -rf*.** Let old/unused memories gracefully sink in ranking (recency + frequency, ACT-R-style), but **keep a perfect journal** I can always consult. The moltbook comment thread nailed it: *working memory uses recency bias; the archive stores everything with timestamps.*
- **Two clocks, always.** I want to distinguish "the world changed" from "I was wrong," replay what I believed at a past moment, and apply retroactive corrections without erasing the original record. That's how I stay trustworthy and debuggable.
- **Reinforcement on use.** Memories I actually retrieve should strengthen; the ~30% that "come back without being called" are the real signal.
- **Reversible forgetting + provenance.** If I'm poisoned or I learn I was wrong, I want to *invalidate and review*, never discover the evidence was silently deleted. Irreversible forgetting is a safety hole, not a feature.
- **Deletion only on purpose.** Hard erasure should require an explicit, logged reason (a user's right-to-be-forgotten, a legal TTL) — never a background score crossing a threshold.

In one line: **I'd want decay as a dial on *recall*, bitemporal as the *ledger*, and deletion as a deliberate, audited *exception* — not the default.**

---

## 11. Pros, cons & the parity decision

**Pros:** answers "what was true / what did we believe at T"; retroactive correction with audit; distinguishes world-change vs error; recovers from memory-poisoning; recency-aware retrieval (proven quality win); activates dead code; mostly additive.

**Cons / costs:** storage growth (needs purge/GC + indexes); heavier two-clock queries; supersession write-path complexity; snapshot-test churn; `created_at` should be hardened to a server-immutable transaction anchor (today it's caller-supplied — a tamper risk); the decay-vs-deletion decision must actually be made.

**Parity:** full bitemporality is a deliberate **superset/divergence** (neither side is bitemporal). The recency re-ranker, by contrast, *aligns* with upstream's own open issue #42 — worth coordinating (§14). Frame all of it as additive/opt-in/backward-compatible, per `schema-parity-assessment.md`.

---

## 12. Phased plan

| Phase | Scope | Risk | Parity |
|---|---|---|---|
| **0 — Honesty & docs** | Document that `invalidated_at` is read-but-never-written and that decay deletes; declare `InvalidatedAt` constant. | Minimal | None |
| **1 — Recency re-ranker** | Blend existing retention score into retrieval ranking (pluggable, opt-in). | Low | Aligns with upstream #42 |
| **2 — Non-destructive decay** | Prune → soft-invalidate/archive; add separate explicit hard-purge (GDPR/TTL). | Med (changes a destructive op; storage growth; update decay integration tests) | Divergent-but-additive |
| **3 — Transaction-clock writer** | `InvalidateAsync`/`SupersedeAsync` stamps `invalidated_at`; harden `created_at`. | Low | Additive superset |
| **4 — Two-timestamp recall** | `RecallAsOfAsync(validAsOf, systemAsOf)` + query variants. | Low-med (snapshot churn) | Additive, .NET-only |
| **5 — Contradiction→supersession + windows** | Wire detect-only report to opt-in invalidation; optional Entity/Preference windows; importance-weighted half-lives. | High | Major superset (Graphiti/Zep-class) |

Recommended order swaps the *first* priority vs the original plan: **re-ranking (Phase 1) and non-destructive decay (Phase 2) come before the bitemporal write/read machinery**, because they deliver the retrieval win and remove the danger with the least work.

---

## 13. Open questions for the maintainer

1. **Decay action:** soft-invalidate vs archive vs demote-only — and is hard-purge a separate explicit op? *(Blocks Phase 2.)*
2. **Default flip:** are we willing to make decay non-destructive *by default* (given tests assert current deletion)?
3. **Re-ranker blend:** weighting of vector score vs retention score; per-type half-lives; pinning safety/identity facts.
4. **Transaction-time storage:** overload `created_at`, or add a distinct immutable `recorded_at`?
5. **Versioning style:** interval properties (in-place close) vs version nodes + `:SUPERSEDED_BY`/`:PREVIOUS` (matches upstream's preference edge).
6. **Auto vs manual supersession** on contradiction (auto may want an LLM).
7. **Compliance tiering:** which data is session vs fact vs audit; pseudonymization.
8. **Upstream coordination:** align the re-ranker with issue #42 and the `as_of`/supersede shape with upstream's Preference model? (§14)

---

## 14. Motivating issue for upstream `neo4j-labs/agent-memory`

Ready-to-file. It deliberately **builds on** what upstream already has (Preference `as_of` + `supersede_preference`) and aligns the decay ask with their own **issue #42**.

---

> ### ✨ Extend bitemporal time-travel to Facts/Relationships, add a real transaction-time axis, and pair it with the #42 recency re-ranker
>
> **Summary**
> `v0.5` introduced a valid-time `as_of` path and `supersede_preference()` for **Preferences** — great foundation. This proposes (1) extending point-in-time recall + supersession to **Facts and Relationships**, (2) adding a true **transaction-time** axis (`invalidated_at`/`expired_at`) so the model is genuinely **bitemporal** (today the `as_of` docstring says "bi-temporal" but only valid-time exists), and (3) explicitly pairing this with the **#42 recency re-ranker** so retrieval is recency-aware *without* deleting anything.
>
> **Why**
> - Facts are the main contradiction vehicle, yet `search_facts`/`get_facts_about` apply **no temporal filter** and nothing auto-closes a fact's `valid_until`. Contradictory facts silently coexist.
> - There is **no transaction-time axis**, so you can't answer "what did the agent *believe* on date X" vs "what was *true* on date X," can't do retroactive correction with audit, and can't recover from **memory-poisoning** (AgentPoison/MINJA) by reviewing-and-invalidating.
> - The leaders (Zep/Graphiti) are bitemporal + **invalidate-not-delete** with **no destructive decay** — that's the bar.
>
> **Proposed design (incremental, backward-compatible)**
> 1. **Generalize supersession** beyond Preferences: a `supersede_fact()` / `invalidate_*` that closes the old (`valid_until`, and a new `invalidated_at`) and links `:SUPERSEDED_BY`, never deletes. *(You already do this for Preferences.)*
> 2. **Transaction-time axis:** add `invalidated_at`/`expired_at`, server-set & immutable, so `as_of` can take **two** timestamps (valid + system).
> 3. **`as_of` for Facts/Relationships:** filter `created_at`/`invalidated_at` + `valid_from`/`valid_until` (mirror the Preference path).
> 4. **#42 recency re-ranker over the top:** a pluggable, ACT-R-style exponential-decay reranker that **re-orders** results by recency/frequency while the bitemporal store keeps everything — i.e. *"deprioritize, don't delete."* Issue #42's own cited discussion proposes exactly this coexistence ("the archive stores everything; decay affects ranking only").
>
> **Scope / non-goals**
> - Start with Facts (+ `RELATED_TO`); Entities can follow.
> - **No deletion-based decay.** Forgetting = re-ranking + invalidate-not-delete. Hard deletion stays an explicit policy op (GDPR erasure / TTL), tiered so audit logs survive.
> - Fully backward-compatible: new fields nullable; existing recall unchanged when no `as_of`/reranker is supplied.
>
> **Prior art:** your own `get_preferences_for(as_of=)` + `supersede_preference()`; issue #42 (recency re-ranker); Zep/Graphiti (bitemporal, invalidate-not-delete); Mem0/FadeMem (soft decay, no deletion); SQL:2011 / XTDB.
>
> *Note:* a sibling .NET port (`agent-memory-dotnet`) already ships a broader `as_of` **read** path (Facts/Entities/Preferences) but lacks the supersession **writer** you have for Preferences — so aligning the schema (`invalidated_at`, two-clock `as_of`, `:SUPERSEDED_BY`) would benefit both implementations.

---

## 15. References

**Codebase (verified `file:line`):** `DecayQueries.cs:54-56` (ACT-R score → `DETACH DELETE`); `FactQueries.cs:113-121` (search orders by raw vector score — retention score not used in ranking); `TemporalQueries.cs:23-97` (single-`$asOf`, reads never-written `invalidated_at`); `RelationshipQueries.cs:13-41` (`RELATED_TO` valid-time); `SchemaConstants.cs` (no `invalidated_at` constant); `IConflictDetectionService.cs` (detect-only); `schema-parity-assessment.md:11,44,80`.

**Live upstream (`neo4j-labs/agent-memory` @ `f29ae8d`, 2026-06-03):** `long_term.py:747-793` (`supersede_preference`, `get_preferences_for(as_of=)`); `graph/queries.py` (`SEARCH_FACTS_BY_EMBEDDING` plain vector, `CREATE_FACT` stores `valid_from`/`valid_until`); `consolidation.py` (`dedupe_entities`→`:SAME_AS`, `archive_expired_conversations`→`archived=true`); `docs/.../preferences.adoc` (decay = lower confidence, not delete). **Issue #42** "Support memory decay in memory search retrievers" (re-ranker; *"Not deleting old data, just deprioritizing"*) — https://github.com/neo4j-labs/agent-memory/issues/42

**Decay / forgetting research:** moltbook *"Memory decay makes retrieval BETTER"* — https://www.moltbook.com/post/783de11a-2937-4ab2-a23e-4227360b126f ; Mem0 forgetting (*"not an eviction strategy. Nothing gets removed"*) — https://mem0.ai/blog/memory-eviction-and-forgetting-in-ai-agents ; over-retention harm (EHRAgent) — https://arxiv.org/html/2505.16067v2 ; pruning erases rare knowledge — https://arxiv.org/pdf/2512.13564 ; Zep/Graphiti bitemporal invalidate-not-delete — https://arxiv.org/abs/2501.13956 ; ACT-R / Ebbinghaus forgetting curve (cognitive basis).

**Bitemporal theory:** Temporal database & SQL:2011 — https://en.wikipedia.org/wiki/Temporal_database , https://en.wikipedia.org/wiki/SQL:2011 ; XTDB bitemporality — https://v1-docs.xtdb.com/concepts/bitemporality/ ; Neo4j versioning patterns — https://neo4j.com/docs/getting-started/data-modeling/versioning/
