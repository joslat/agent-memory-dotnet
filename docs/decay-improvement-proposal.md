# Improving "Decay" in agent-memory-dotnet — a structure-first proposal

> **Status:** Design proposal / discussion. **No code is changed by this document.**
> **Date:** 2026-06-07
> **Companions:** [`bitemporal-memory-assessment.md`](./bitemporal-memory-assessment.md) (the storage substrate — invalidate-not-delete) and [`upstream-issue-memory-decay-bitemporal.md`](./upstream-issue-memory-decay-bitemporal.md) (the upstream issue). This doc is about the **retrieval/relevance** side: *which kind of decay should we actually build?*
>
> **✅ UPDATE (shipped since this draft):** The D-series proposed here **landed and was verified live (2026-06-12/13)** — D1 recency re-ranker, D2 structural hop-decay (`structScore = score·γ^hops`), D3 query-intent presets, non-destructive decay-by-default, and the GDS `AgentMemory.Analytics` package. The §3 "reality check" table below is the **pre-implementation problem statement** — see §11 for the implemented design and the CHANGELOG for the shipped surface.

---

## 1. TL;DR — which decay is for us

"Decay" is not one mechanism; it's a **family** of strategies that down-weight different things. For a **graph-native** store like ours, the answer is clear:

- **Primary decay = structural distance decay (the graph-native one).** Relevance should fall off with **graph distance** (hops from what we're reasoning about), not with time. This is **spreading activation** (Collins & Loftus, 1975) / **Personalized PageRank** (HippoRAG). It is **age-neutral**, which is exactly why analog/precedent retrieval works — *the best match may be old.*

  > Your framing, which I fully endorse: *"In a graph, your most powerful 'decay' has nothing to do with forgetting or time; it's 'how far is this from what I'm reasoning about, structurally.'"* MarketMind's cascade already does this (hop-decay), and regime-distance does it for analogs — age-independent by design.

- **Secondary decay = temporal recency, as a *dial*, not a default.** Recency/frequency (the ACT-R score we already compute) is a *staleness* signal, useful for "what's the latest" but harmful for "find me a precedent." Keep it **separate, optional, and zeroable** so it never buries a structurally-relevant old analog.
- **Representation & composition decay = non-destructive hygiene.** Summarize and merge to cut noise — but **keep the source** (archive/`:SAME_AS`), never `DELETE`.
- **Deletion = policy-only.** Never a background score crossing a threshold.

In one line: **decay over *structure* for relevance, decay over *time* as a dial, decay over *representation/composition* for tidiness — and never decay as deletion.**

The kicker: agent-memory-dotnet already has the pieces (a hop-traversal retriever that computes `hops`, an ACT-R retention score, reinforcement-on-recall) but **wires them wrong** — it throws the hop distance away, never uses the retention score for ranking, and uses it only to `DETACH DELETE`. The fix is mostly *rewiring*, not new machinery.

---

## 2. The decay family map

Seven families, two axes: **what** they down-weight, and **destructive vs non-destructive**.

| Family | One-liner | Cognitive / CS basis | Destructive? | What it solves | For us? |
|---|---|---|---|---|---|
| **Structural** (graph distance) | Relevance falls off with **hops** from the anchor | Spreading activation (Collins & Loftus 1975); Personalized PageRank; HippoRAG (damping 0.5) | **No** (pure scoring) | Associative / multi-hop / analogical recall; age-neutral relevance | ✅ **Primary** |
| **Retrieval** (temporal recency) | Recency/frequency **re-ranking** at query time; store untouched | ACT-R base-level `B_i = ln(Σ tⱼ⁻ᵈ)`; Ebbinghaus; Generative Agents (recency 0.995) | **No** | Surfacing fresh/active items; staleness | ✅ **Secondary dial** |
| **Importance** weighting | A salience score that modulates all other families | ACT-R `α_importance`; MemoryBank reinforcement | No (Yes if used as eviction key) | Rare-but-critical facts survive compression | ✅ Cross-cutting modulator |
| **Representation** | Lossy abstraction: episodic detail → gist/summary; embedding coarsening | Systems consolidation (episodic→semantic); MemGPT summarization; MemoryBank | **Yes, unless source retained** | Fit more into limited storage/context | 🟡 Non-destructive only (keep source) |
| **Composition** | Merge/dedup: instances → canonical entity/aggregate | Reflection trees (Generative Agents); KG entity resolution | **Yes at instance level, unless `:SAME_AS`/alias kept** | Duplicate/contradictory noise → one authoritative node | 🟡 Non-destructive only (`:SAME_AS`) |
| **Capacity / eviction** | Drop entries over a size budget | LRU / LFU / LFRU; MemGPT paging | **Yes** (or page to cheaper tier) | Bounded context/storage | 🟡 Page/archive, don't delete |
| **Temporal / TTL** | Hard expiration by age | Ebbinghaus as a delete rule | **Yes** | Stale data interference | ❌ Policy-only (GDPR/legal TTL) |

(The four families you named — retrieval, representation, composition, structure — map cleanly; importance/eviction/TTL round out the space. The destructive/non-destructive label is an *implementation choice*, not intrinsic.)

---

## 3. Where agent-memory-dotnet stands, per family (reality check)

| Family | Current state in agent-memory-dotnet | Verdict |
|---|---|---|
| **Structural** | **✅ Done (opt-in, D2).** `GraphRetriever.BuildTraversalCypher` now emits `structScore = score · γ^hops` and orders by it when `StructuralDecayGamma < 1.0`; off by default (γ=1.0, where `hops` remains a tiebreaker only). Only `RELATED_TO` is traversed; `ABOUT`/`MENTIONS`/`SAME_AS`/`TOUCHED` are written but not walked multi-hop. GDS/PageRank now ships as the opt-in `AgentMemory.Analytics` package. | ✅ **Done (opt-in) — see §11** |
| **Retrieval (temporal)** | Search ranks **purely on vector cosine** (`ORDER BY score DESC`). The ACT-R retention score is computed but **never blended into ranking**. | 🔴 Missing |
| **Importance** | `confidence` exists; not used as a retrieval modulator. | 🔴 Missing |
| **Representation** | Long-trace summarization is **detect-only** (counts candidates, doesn't summarize). | ⚪ Not built (safe) |
| **Composition** | Conversation expiry = **soft** (`SET archived = true`). Entity dedup = **detect-only**. **Preference dedup = `DETACH DELETE dup`** (`ConsolidationQueries.cs:44`). | 🔴 One destructive path |
| **Eviction / TTL** | — | ⚪ Not built |
| **Decay-as-deletion** | `DecayQueries.BuildPrune` → **`DETACH DELETE`** Entity/Fact/Preference below retention threshold. | 🔴 Destructive default |
| **Reinforcement** | ✅ `RecallAsync` bumps `last_accessed_at` + `access_count` on recalled items (when decay service registered). | 🟢 Wired |

**Summary:** the only "decay" actually wired into *ranking* is... none (vector-only). The retention score exists solely to **delete**. Two destructive paths (`DETACH DELETE` on prune; `DETACH DELETE dup` on preference dedup). The graph-distance signal is computed and thrown away. This is "the right cognitive models, wired to the wrong actions."

---

## 4. The case for structural / distance decay (the graph-native one)

**Theory.** Spreading activation (Collins & Loftus 1975): activation propagates over a semantic network and *"diminishes with the distance or weakness of connections"*; semantic similarity ≈ shortest-path length, with a per-link decay factor `d ∈ (0,1)`. Its modern form is **Personalized PageRank** (random walk with restart): continue along an edge with probability `α`, restart at the seed with `1−α`; longer walks decay as `αᵗ`. **HippoRAG** uses PPR over a knowledge graph (damping 0.5) as a *hippocampal index*, and *"nodes further away receive less activation"* — it beats prior SOTA by up to ~20% on multi-hop QA.

**Why age-neutral matters (the analog argument).** In case-based / analogical reasoning the **best precedent is often old**. Structural similarity, not recency, is what makes an analog useful (Gentner; legal CBR like HYPO/CATO retrieves *landmark* cases, not recent ones). Temporal decay would actively bury a structurally-relevant old case — the exact failure your regime-distance/MarketMind-cascade design avoids by decaying over **structure**, not time. Distance decay is *age-independent by construction*.

**The honest nuance (so we don't overclaim).** Structural decay is the right *primary relevance* signal, but it doesn't *replace* time — it's **orthogonal** to it:
- Classical spreading activation and HippoRAG both *can* carry a small time term; clean separation is cleanest in ACT-R, which literally splits activation into `A_i = B_i (recency/frequency) + Σ Wⱼ·Sⱼᵢ (spreading/structural)`.
- Structure answers **"how relevant/related is this?"**; time answers **"how stale is this?"** Different questions. The best systems **blend** them and let **query intent** set the weights — heavy structure for "find an analog," some recency for "what's the latest."

So: **structure is the primary, age-neutral relevance dimension; temporal recency is a separate, optional dial you can turn to zero for analog retrieval.**

---

## 5. The composite relevance model

Blend the read-time signals into one score (published practice: GraphRAG's convex blend `R = (1−β)·S_vec + β·S_graph`, arXiv 2507.19715; ACT-R's additive `B_i + spreading`; Generative Agents' `recency+importance+relevance`):

```
R(v | q) =  w_sem · S_sem(v,q)        // vector cosine — semantic match (already have)
          + w_str · S_str(v,q)        // structural: γ^hops from anchor / PPR — PRIMARY, age-neutral
          + w_tmp · S_tmp(v)          // temporal: ACT-R retention score — SECONDARY, zeroable
          ⊗ f_imp(v)                  // importance/confidence/type modulator (multiplicative)
```

- `S_str` = spreading activation from the query anchor: `seedScore · γ^hops` (or PPR `αᵗ`), optionally per-edge-type weights (`ABOUT` > `MENTIONS` > `RELATED_TO`, etc.).
- `S_tmp` = the **existing** `confidence·e^(−λ·daysSinceAccess) + boost·accessCount` — reused as a *ranking* term, not a delete gate.
- **Query-intent presets:** `latest` → raise `w_tmp`; `analog`/`precedent` → `w_tmp = 0` (pure structure+semantics); `default` → structure-leaning blend. Alternatively fuse rank lists with **RRF** (`Σ 1/(k+rank)`) to avoid score-scale calibration.

This composes cleanly with the bitemporal substrate (companion doc): re-ranking is a **read-time scoring layer**; invalidate-not-delete is a **storage property**. You get recency- and structure-aware relevance *and* full history/auditability/reversibility — no deletion required.

---

## 6. Concrete design for agent-memory-dotnet

The good news: most of this is **rewiring existing code**.

**A. Turn the discarded `hops` into structural decay `[low risk]`.** `GraphRetriever` already does the variable-length traversal and returns `hops`. Apply a per-hop decay and use it in ranking:
```cypher
MATCH path = (seed)-[:RELATED_TO*1..$maxHops]-(related)
WITH related, reduce(s = 1.0, rel IN relationships(path) | s * $gamma) AS structScore   // γ^hops
RETURN related, max(structScore) AS structScore
ORDER BY structScore DESC
```
Then blend `structScore` with the seed's vector score instead of `ORDER BY score DESC, hops ASC`.

**B. Spread across more edge types `[low-med]`.** Extend the traversal from `RELATED_TO` only to the semantic edges that already exist: `[:RELATED_TO|ABOUT|MENTIONS|SAME_AS*1..N]`, with optional per-type weights — true spreading activation over the heterogeneous graph.

**C. Blend into one score `[low-med]`.** Implement `R = w_sem·S_sem + w_str·S_str + w_tmp·S_tmp` (or RRF) in the retrieval/assembler layer; expose weights + query-intent presets via `GraphRagOptions`. Default `w_tmp` low so old analogs aren't buried.

**D. Reuse the retention score for ranking, not deletion `[low]`.** `CalculateRetentionScoreAsync` already computes the ACT-R value — feed it in as `S_tmp`. Reinforcement-on-recall is already wired (`access_count`/`last_accessed_at`), so the signal strengthens with use for free.

**E. Make the two delete paths non-destructive `[med, opt-in]`.** Switch `DecayQueries.BuildPrune` from `DETACH DELETE` to `SET invalidated_at`/demote, and `RemoveDuplicatePreferences` from `DETACH DELETE dup` to a `:SAME_AS`/`:SUPERSEDED_BY` link (mirroring upstream's non-destructive dedup) so the duplicate is *merged-and-kept*, not erased. Keep an explicit, separate hard-purge for GDPR/TTL.

**F. (Optional, deferred) GDS Personalized PageRank `[high]`.** For full spreading activation, project the subgraph and run `gds.pageRank.stream(g, {sourceNodes:[anchor], dampingFactor: γ})` — this is already a *deferred* idea (`AgentMemory.Analytics`, `pageRankScore`). Plain-Cypher `γ^hops` (A–C) gets ~80% of the value with no plugin dependency; GDS is the upgrade path.

**Net effect:** retrieval becomes structure-aware and age-neutral; the retention score earns its keep as a *dial* instead of a guillotine; nothing is deleted by default.

---

## 7. Suggestions for the Python version (`neo4j-labs/agent-memory`)

Their open **issue #42** proposes a *temporal* re-ranker. The graph-native upgrade:

1. **Make the #42 reranker a composite, structure-first one.** They're a graph store with `RELATED_TO`/`ABOUT`/`MENTIONS` edges and a `valid_from`/`valid_until` model — perfect for a **structural distance** reranker (`γ^hops` / PPR) blended with vector similarity, with temporal recency as one optional term. HippoRAG shows PPR-over-KG is SOTA for exactly this.
2. **Keep it non-destructive** (they already are — `:SAME_AS`, `archived=true`). The reranker is pure scoring; no deletion.
3. **Pluggable presets** for query intent (latest vs analog), so recency can be zeroed for precedent retrieval.

This extends #42 from "recency reranker" to "graph-native composite reranker," and it's a natural fit for their architecture. (Detail in [`upstream-issue-memory-decay-bitemporal.md`](./upstream-issue-memory-decay-bitemporal.md).)

---

## 8. Phased plan & risk

| Phase | Scope | Risk |
|---|---|---|
| **1 — Structural decay** | `γ^hops` per-hop decay in `GraphRetriever`; use `structScore` in ranking (was discarded). | Low |
| **2 — Composite reranker** | Blend `S_sem + S_str + S_tmp` (reuse retention score) + RRF option + intent presets. | Low-med |
| **3 — Multi-edge spreading** | Traverse `RELATED_TO|ABOUT|MENTIONS|SAME_AS` with per-type weights. | Med (perf; fan-out caps) |
| **4 — Non-destructive hygiene** | Decay prune → soft-invalidate; `RemoveDuplicatePreferences` → `:SAME_AS` merge; explicit hard-purge for policy. | Med (changes destructive ops; tests) |
| **5 — (Optional) GDS PPR** | `gds.pageRank` personalized from the anchor; `pageRankScore`. | High (plugin dep) |

**Risks:** traversal fan-out / latency (bound hops, cap neighbors, over-fetch then trim); score calibration across signals (use RRF if scales fight); weight tuning (ship sane defaults + presets); snapshot-test churn for any new Cypher constants.

---

## 9. Open questions

1. **Default weights** for `w_sem / w_str / w_tmp`, and the structural decay `γ` (per-hop) — and per-edge-type weights?
2. **Query-intent API:** explicit presets (`latest` / `analog` / `default`) vs. learned/auto?
3. **Anchor selection:** what seeds the spread — the query's matched entities, the current reasoning trace's `TOUCHED` entities, or both?
4. **Plain Cypher `γ^hops` vs GDS PPR** — start with Cypher, treat GDS as the opt-in upgrade?
5. **Make non-destructive hygiene the default**, or flag-gated for a release?
6. **Importance/type pinning:** which types never decay in ranking (safety/identity facts, error-fixes, commitments — per the moltbook finding that those should "decay slowest")?

---

## 10. References

**Codebase (verified `file:line`):** `GraphRetriever.cs:79-105` (RELATED_TO`*1..2` traversal, computes `hops`, no per-hop decay); `FactQueries.cs:113-121` / `EntityQueries.cs:96-104` / `PreferenceQueries.cs:57-65` (vector-only `ORDER BY score DESC`); `DecayQueries.cs:12-16,55-57` (retention score → `DETACH DELETE`; access reinforcement); `ConsolidationQueries.cs:18-22,37-45` (archive = soft; preference dedup = `DETACH DELETE dup`; entity dedup = detect-only); `Neo4jMemoryDecayService.cs:135-142` (`ComputeScore`); `MemoryService.cs:78-85,320-327` (reinforcement on recall).

**Structural decay / graph relevance:** Spreading activation — Collins & Loftus (1975), *A Spreading-Activation Theory of Semantic Processing*; HippoRAG — https://arxiv.org/html/2405.14831v1 (PPR over KG, damping 0.5); GraphRAG blend `R=(1−β)S_vec+βS_graph` — https://arxiv.org/html/2507.19715v1 ; Neo4j PPR (`gds.pageRank` `sourceNodes`/`dampingFactor`) & APOC `apoc.path.expandConfig` (minLevel/maxLevel + `length(path)`); per-hop decay via `reduce()` over `relationships(path)`.

**Decay families / cognitive basis:** ACT-R base-level activation `B_i = ln(Σ tⱼ⁻ᵈ)` — https://arxiv.org/pdf/2505.05083 ; Stanford Generative Agents (recency 0.995, never deletes) — https://ar5iv.labs.arxiv.org/html/2304.03442 ; Ebbinghaus forgetting curve; MemGPT (paging/summarization); MemoryBank (Ebbinghaus updating); cache policies LRU/LFU/LFRU; agent-memory forgetting surveys — https://arxiv.org/pdf/2512.13564 , https://arxiv.org/html/2603.07670v1 .

**Analogical age-neutrality:** Gentner/Forbus MAC-FAC (structural vs surface similarity in retrieval); legal CBR (HYPO/CATO — precedent over recency); structural-over-surface analog transfer — https://link.springer.com/article/10.3758/BF03197035 .

---

## 11. D1/D2 implementation design — signed off 2026-06-12 · ✅ IMPLEMENTED 2026-06-12

> **Maintainer decisions:** ship **OFF by default** (`RecencyWeight = 0`, `γ = 1.0`) — opt-in, zero behavior change; D1 blend computed **Cypher-side** in `SearchByVector` over the existing over-fetch pool; plus a thin `MemoryProfile` (Parity/Enhanced/Bitemporal) switch. Tracked as **§II.8 phases D1–D2** in `Memory_Review_and_Implementation_Plan.md`.
>
> **✅ Status (2026-06-12): D1 + D2 + `MemoryProfile` implemented and green.** New `MemoryRankingOptions`/`MemoryProfile` (Abstractions) + `MemoryOptions.Ranking`; `VectorRerank`/`RerankParameters` helpers; `Fact/Entity/PreferenceQueries.SearchByVector(..., recencyRerank)`; the 3 long-term repos inject the ranking + decay options; `GraphRetriever` + `Neo4jGraphRagContextSource` thread γ. **Tests: full solution builds clean; 2297 unit green (Cypher snapshot / `ExpectedQueryCount` unchanged — confirming zero catalog churn); 2 new live-Neo4j integration tests pass (D1 reorders a stale top-similarity hit below a fresh one; D2 halves a 1-hop neighbour's score at γ=0.5), and the existing GraphRAG/over-fetch/owner-scope integration suites stay green on the parity path.** `MemoryProfile` bumped the Abstractions enum count 9→10 (contract guard + `architecture.md` updated). NOT committed (per maintainer hold).

**Scope.** D1 = recency re-ranker (blend the already-computed ACT-R retention score into the **live** `SearchByVector` ranking). D2 = structural hop decay (`score · γ^hops`) in `GraphRetriever`. Both additive and opt-in. The destructive `DETACH DELETE` prune (D4) and the `invalidated_at` writer (D5) are **NOT** in this change.

**Low-risk finding.** `SearchByVector` and `BuildTraversalCypher` are *methods*, not `const string`s; `CypherQueryRegistry` reflects only consts, so **`CypherQuerySnapshot.snap` and `ExpectedQueryCount` are unaffected**. Only the dedicated query-text unit tests (`ScopedVectorSearchQueryTests`, `GraphRetrieverTests`) + the integration suite change.

### D1 — recency re-ranker (Cypher-side)
- New `MemoryRankingOptions { double RecencyWeight = 0.0 }` (Abstractions/Options). λ + access-boost continue to come from `MemoryDecayOptions` (single source of truth for the decay curve).
- `Fact/Entity/PreferenceQueries.SearchByVector` gain a `bool recencyRerank` param. **Off ⇒ today's query byte-for-byte.** On ⇒ over the over-fetch pool:
  - `daysSince = max(0, (now − COALESCE(last_accessed_at, created_at)) in days)`
  - `sTmp = min(1, COALESCE(confidence,0.5)·exp(−$lambda·daysSince) + $boostFactor·COALESCE(access_count,0))`
  - `RETURN node, ((1−$tmpWeight)·score + $tmpWeight·sTmp) AS score  ORDER BY score DESC  LIMIT $limit`
- Uses **COALESCE, never `IS NULL`**, so the owner-only variant keeps satisfying the `NotContain("IS NULL")` guard; keeps `LIMIT $limit` and the over-fetch `topK` literal.
- Scale-safe: Neo4j cosine `score`∈[0,1]; `sTmp` clamped to [0,1] ⇒ blend ∈[0,1]. (RRF is the D3 fallback if scales fight.)
- Threading: the 3 long-term repos inject `IOptions<MemoryRankingOptions>` + `IOptions<MemoryDecayOptions>` + `IClock`; set `recencyRerank = RecencyWeight > 0`; add params `$now/$lambda/$boostFactor/$tmpWeight`. No public interface change. **Live recall only — `*AsOf` untouched.** Inputs (`last_accessed_at`/`access_count`) come from the recall-time reinforcement already wired in `MemoryService.RecallAsync`; absent the decay service they're null → COALESCE to `created_at`.

### D2 — structural γ^hops decay
- `GraphRagOptions.StructuralDecayGamma` (default `1.0` = off). `GraphRetriever` ctor takes `gamma`; `Neo4jGraphRagContextSource.CreateRetriever` passes it.
- `BuildTraversalCypher`: when γ<1, `WITH seed, related, score, hops, score * ($gamma ^ hops) AS structScore … ORDER BY structScore DESC, hops ASC`. γ=1 ⇒ emits today's exact text. `$gamma` is a **bound param** (only the hop bound stays a literal). Plain-Cypher `γ^hops` ≈ 80% of PPR value; GDS PageRank remains the deferred upgrade.

### Test impact
- **None** to snapshot / `ExpectedQueryCount`. Update `ScopedVectorSearchQueryTests` (Func signature + on-path asserts) and `GraphRetrieverTests` (structScore + γ-range). New D1 blend-text unit test + a `MemoryRankingOptions` round-trip. Integration: D1 — old-high-cosine vs new-low-cosine reorders when `w>0`, unchanged when `w=0`; D2 — nearer hop wins when γ<1.
