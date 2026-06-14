# Upstream proposal (ready to file) — supporting #42 with non-destructive memory decay

> **What this is:** a ready-to-post GitHub issue/RFC for **`neo4j-labs/agent-memory`** that **supports and extends [issue #42](https://github.com/neo4j-labs/agent-memory/issues/42)** ("Support memory decay in memory search retrievers"). It suggests implementing decay as **retrieval re-ranking over a bitemporal, invalidate-not-delete store** rather than as hard deletion. Tone is deliberately that of a downstream implementer offering input, not prescribing to the maintainers.
> **How to file:** post the body below as a comment on #42, or open it as a companion issue that links #42. Everything in it is verified against the live repo (`main` @ `f29ae8d`, 2026-06-03) and primary sources; citations are in the last section.
> **Companion:** the deeper architecture (bitemporal design, phased plan) lives in [`bitemporal-memory-assessment.md`](./bitemporal-memory-assessment.md); a dedicated decay-patterns proposal (structural/distance decay) is in progress.

---

## ✨ Supporting #42: memory decay as recency re-ranking over a bitemporal, invalidate-not-delete store

### Context — where this comes from

I'm building an open-source **.NET port of agent-memory** — [joslat/agent-memory-dotnet](https://github.com/joslat/agent-memory-dotnet) — bringing your graph-native memory model to the .NET / Neo4j ecosystem. Porting it means living inside your design decisions, and studying the temporal and consolidation work closely (the v0.5 Preference `as_of` + `supersede_preference()`, the non-destructive consolidation primitives) has been genuinely clarifying — thank you for it.

It also surfaced a design question that connects directly to #42, and that I'd love your perspective on: **how should "decay" and time-travel fit together?** What follows is offered as input from a downstream implementer who's been reading the temporal-memory literature (Zep/Graphiti, Mem0, HippoRAG, ACT-R) — not as prescription. You know the project's direction far better than I do, so please read the strong wording below as enthusiasm, not as telling you your business.

### TL;DR

**+1 to #42** — recency-aware retrieval is a great direction, and the discussion it cites captures the key idea: *"Not deleting old data, just deprioritizing it in search results."* This proposal tries to make that principle explicit and give it a durable home:

- Implement decay as a **read-time re-ranker** (the #42 ask) — **keep every record**.
- Put it **on top of a bitemporal, invalidate-not-delete store**, building on what already shipped for Preferences (`get_preferences_for(as_of=)` + `supersede_preference()`), so forgetting is **reversible and auditable**.
- **Never** turn decay into hard deletion. Deletion is a deliberate, audited policy action (GDPR erasure / legal TTL) — not a background score crossing a threshold.

In one line: **decay as a dial on *recall*, bitemporal as the *ledger*, and deletion as a deliberate, audited *exception*.**

---

### Design goals I've been aiming for in the port

These are the principles guiding the .NET port — shared as the *reasoning* behind the proposal, and very much open to your feedback rather than offered as rules:

1. **Forget like human *recall*, not like `rm -rf`.** Let old/unused memories gracefully sink in retrieval ranking — but keep a journal you can always consult. (The #42-cited post puts it well: deprioritize, don't delete.)
2. **Two clocks, where it helps.** Tracking *valid time* ("when it was true") and *transaction time* ("when we recorded it") lets the system distinguish **"the world changed"** from **"we were wrong"**, replay past beliefs, and apply retroactive corrections without erasing the original.
3. **Reinforcement on use.** Memories that get retrieved strengthen; the ones that "come back without being called" tend to be the real signal. (ACT-R activation; ~30-day half-life as a starting default.)
4. **Reversible forgetting + provenance.** If the store is poisoned or a fact turns out wrong, being able to **invalidate and review** beats discovering the evidence was silently deleted — irreversible forgetting tends to become a safety gap.
5. **Deletion on purpose.** Hard erasure ideally has an explicit, logged reason (right-to-be-forgotten, legal TTL) rather than being a default decay mechanism.

---

### The case against decay-as-hard-delete

Decay improves retrieval; **deletion** is the dangerous part, and the two are routinely conflated. Hard-delete decay is the wrong default because it is **irreversible**:

- **It erases rare-but-critical knowledge.** *"Overly aggressive pruning risks erasing rare but essential knowledge, harming reasoning continuity in long-term contexts."* Once pruned, it cannot be recovered.
- **It breaks audit, provenance, and reproducibility.** You can no longer answer "what did the agent know when it decided X?" — disqualifying for finance/healthcare/regulated/high-trust agents.
- **It's an attack surface.** Memory-poisoning (e.g. AgentPoison: <0.1% injection → high retrieval-hijack) and sleeper attacks plant persistent false memories; **induced-forgetting** can evict *true* safety-relevant facts. Only "invalidate + review" lets you recover.
- **Recency-deletion buries the wrong things.** In a graph/case-based store, the *best analog may be old*. Time-based deletion (and aggressive time-decay) buries structurally-relevant precedents — the opposite of what you want.
- **The pro-forgetting evidence is about quality, not age.** The studies showing "remembering everything hurts" (e.g. add-all memory collapsing in accuracy vs a curated policy) are arguments against keeping **low-quality** records, **not** against keeping **old-but-true** ones.

And notably — **this project already gets it right elsewhere.** Hygiene here is non-destructive: `dedupe_entities` writes a `:SAME_AS` edge (keeps both), `archive_expired_conversations` sets `archived = true` (keeps data), `supersede_preference` closes `valid_until` and links `:SUPERSEDED_BY` (keeps the old). Decay should follow the same philosophy. The market leaders agree: Zep/Graphiti are invalidate-not-delete with **no** automatic deletion-decay; Mem0/FadeMem implement decay as a **soft search-time rescale** ("Memory Decay is not an eviction strategy. Nothing gets removed… still surfaces when it is genuinely the best match").

---

### Proposed design (the "better way")

Build #42's re-ranker as a read-time scoring layer over a retain-everything substrate. Concrete, incremental, backward-compatible ideas:

1. **Recency/frequency re-ranker (the #42 ask).** Pluggable, extendable reranker; default = ACT-R-style exponential time decay (~30-day half-life) that **re-orders** results and **boosts on retrieval**. Data is kept; "decayed" memories still surface on explicit/best-match search.
2. **Structural / distance decay — the graph-native, age-neutral relevance signal.** In a knowledge graph the most powerful "decay" isn't time, it's **structural distance**: relevance falls off with **graph hops** from what you're reasoning about (spreading activation / personalized PageRank, `score · γ^hops`). This is **age-neutral** — it surfaces a structurally-relevant memory regardless of age, which is exactly why analog/precedent retrieval works. Blend it with vector similarity; treat temporal recency as a *secondary* signal so it never buries old-but-relevant analogs. *(Deeper treatment in a companion design doc.)*
3. **Bitemporal substrate (extend what you already have).** Generalize the Preference pattern — `as_of=` recall + `supersede_*()` (`valid_until` + `:SUPERSEDED_BY`) — to **Facts and Relationships**, and add a real **transaction-time** axis (`invalidated_at`/`expired_at`, server-set & immutable) so `as_of` can take *two* timestamps (valid + system). Today the Preference `as_of` docstring says "bi-temporal" but only valid-time exists; closing this makes it genuinely bitemporal.
4. **Importance-weighted half-lives.** Per the #42-cited discussion's own finding: error-fixes, commitments, and relationships should decay slowest; pin identity/safety facts so they never sink or expire.
5. **Non-destructive forgetting tiers.** `hot` (recent/active, full weight) → `warm` (decayed in ranking, still retrievable) → `cold` (archived / soft-invalidated, reachable via explicit or `as_of` query). Nothing physically deleted except by explicit policy.
6. **Compliance tiering.** Separate session context vs extracted facts vs audit records, and pseudonymize — so GDPR-delete (Art. 17) and audit-retention (EU AI Act Arts. 12/72) can coexist without blanket deletion.

---

### Capability comparison: today vs. + full bitemporal + recency re-ranking (additive only)

"Today" = current `neo4j-agent-memory` behaviour (verified @ `f29ae8d`). The right column lists only what's **added** — every item is additive/opt-in/backward-compatible.

| Capability | Today | + With bitemporal + recency re-ranking (added) |
|---|---|---|
| Recall current memory | ✅ | — |
| Point-in-time (`as_of`) recall | 🟡 **Preferences only** (`get_preferences_for(as_of=)`) | Extend `as_of` to **Facts & Relationships** |
| Supersession / close-the-old-record | 🟡 **Preferences only** (`supersede_preference` → `valid_until` + `:SUPERSEDED_BY`) | Generalize `supersede_*` to **Facts & Relationships** |
| Transaction-time axis ("what we believed when") | ❌ no `invalidated_at`/`expired_at` anywhere | ✅ true **bitemporal** — two-timestamp `as_of` |
| Recency/frequency-aware retrieval ranking | ❌ plain vector search (`SEARCH_FACTS_BY_EMBEDDING`, `ORDER BY score`) | ✅ **#42 re-ranker** (ACT-R decay, boost-on-recall) |
| Structural / graph-distance relevance | ❌ per-node similarity only | ✅ age-neutral **distance decay** (spreading activation / PPR) |
| Valid-time windows on facts | ✅ stored (`valid_from`/`valid_until`), but **not filtered** on retrieval | ✅ honored in `as_of` recall + supersession |
| Non-destructive hygiene (dedupe/archive) | ✅ `:SAME_AS`, `archived=true` | — (keep — this is the right model) |
| Retroactive correction with audit | ❌ | ✅ both versions retained & queryable |
| Distinguish "world changed" vs "we were wrong" | ❌ | ✅ new valid-interval vs new transaction-version |
| Recover from memory-poisoning (review + invalidate) | ❌ | ✅ invalidate-not-delete makes forgetting reversible |
| Hard-delete-based forgetting | ✅ **absent** (only `clear_session` cleanup) | — (keep absent — decay must **not** delete) |

---

### Scope / non-goals

- Start the bitemporal extension with **Facts** (+ `RELATED_TO`); Entities can follow.
- **No deletion-based decay.** Forgetting = re-ranking + invalidate-not-delete. Hard deletion stays an explicit, tiered policy op (GDPR erasure / legal TTL).
- Fully **backward-compatible**: new fields nullable; existing recall unchanged when no `as_of` / reranker is supplied.
- The re-ranker is **pluggable** (per #42) so teams can swap recency, structural distance, or composite scoring.

### Builds on what already shipped

This is not a rewrite — it generalizes existing, correct patterns: the v0.5 Preference `as_of` time-travel, `supersede_preference()`, and the non-destructive consolidation primitives (`dedupe_entities` → `:SAME_AS`, `archive_expired_conversations` → `archived=true`). It gives #42's reranker a principled home and extends the time-travel story from Preferences to the whole graph.

### Prior art / references

- **#42** — recency re-ranker proposal; cited discussion: *"Not deleting old data, just deprioritizing it in search results."*
- **This repo (verified @ `f29ae8d`):** `long_term.py:747-793` (`supersede_preference`, `get_preferences_for(as_of=)`); `graph/queries.py` (`SEARCH_FACTS_BY_EMBEDDING` plain vector; `CREATE_FACT` stores `valid_from`/`valid_until`); `consolidation.py` (`dedupe_entities` → `:SAME_AS`, `archive_expired_conversations` → `archived=true`).
- **Decay-as-soft-rerank:** Mem0 — *"Memory Decay is not an eviction strategy. Nothing gets removed."* (https://mem0.ai/blog/memory-eviction-and-forgetting-in-ai-agents); FadeMem (Ebbinghaus decay + importance hysteresis).
- **Invalidate-not-delete bitemporal:** Zep/Graphiti (https://arxiv.org/abs/2501.13956).
- **Risks of pruning / over-retention:** https://arxiv.org/pdf/2512.13564 ; EHRAgent over-retention study https://arxiv.org/html/2505.16067v2 .
- **Cognitive basis:** ACT-R base-level activation; Ebbinghaus forgetting curve; spreading activation (Collins & Loftus, 1975) for structural distance decay.
- **Bitemporal standard:** SQL:2011 / XTDB (https://v1-docs.xtdb.com/concepts/bitemporality/).
