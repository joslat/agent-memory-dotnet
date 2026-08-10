# The Memory Map

A reference for what agent memory is, what a memory system has to decide that a store does not, and
**exactly** which of those decisions AgentMemory makes today.

This is not a feature page. Several sections below describe things this library does not do, does
partially, or does in a way that has never been measured. That is the point: a memory map that only
lists strengths is a brochure, and you cannot plan against a brochure.

---

## How to read the status labels

Three words are used throughout, and they mean three different things:

| Label | Means |
|---|---|
| **BUILT** | The type, query, or option exists in the codebase. |
| **WIRED** | It is reachable from configuration or from a first-party read/write path — not just present. |
| **MEASURED** | Its effect has been observed in an evaluation run, and the number is written down. |

Most published memory claims — in this project and elsewhere — answer the first question and are
presented as though they answered the third. Everything in [§5](#5-our-coverage-today) is labelled.

Every claim about *this* library cites a file. Where a number is given, its source is named. Where
something is unmeasured, it says so.

---

## 1. What an agent memory system is for

A **store** answers *"what did I write?"*

A **memory system** answers *"what should I know right now?"*

The gap between those two questions is not schema, scale, or embedding quality. It is that a memory
system makes decisions a store declines to make: what to keep, what to rank down, which of two
incompatible beliefs is current, whose it is, when it was true, and — the decision that subsumes all
the others — what fits in the few thousand tokens that actually reach the model.

| Decision | A store's answer | A memory system's answer |
|---|---|---|
| What to keep | everything | what earns its slot |
| Two incompatible beliefs | both | one, with the other superseded and dated |
| Whose it is | a column | a constraint enforced inside retrieval |
| When it was true | a timestamp | two clocks, both honoured on the live path |
| Why you believe it | a foreign key | a specific message and a named extractor |
| What reaches the model | everything matching | a budgeted, deduplicated, calibrated selection |

### The memory types

The six types below are not a storage taxonomy. They are a taxonomy of **questions an agent has to
answer about itself and its world**. Each one is a distinct type because no other type can answer its
question, not because it needs its own table.

| Type | The question it answers |
|---|---|
| **Semantic** | What is true about the entities I deal with, independent of when I learned it? |
| **Episodic** | What happened, in what order, and who said it? |
| **Procedural** | How do things get done here? |
| **Prospective** | What am I supposed to do later — and has "later" arrived? |
| **Meta-memory** | How much should I trust what I just recalled? |
| **Agent-episodic (reasoning traces)** | Have I attempted something like this before, and how did it go? |

---

## 2. What makes a memory system great

Six positions, stated as claims so they can be argued with.

**1. The retrieval budget *is* the memory system.** Everything upstream of it is bookkeeping in
service of a few thousand tokens. A system holding a million excellent memories that assembles the
wrong 2,000 tokens is worse than one holding a thousand that assembles the right ones.

**2. Every memory type is a claimant on a shared, finite channel.** Adding a type without giving it a
dedicated budget — and preferably its own index — makes every existing type worse. This is the
strongest architectural argument for building new types on their own retrieval channel rather than
pouring everything into one embedding space.

**3. Candidate generation beats ranking.** A reranker reorders survivors. If candidate generation is
starved, a reranker reorders the wrong seven items and reports an improvement. Fix the recall ceiling
before tuning precision. Getting this order backwards produces measurable-looking gains on a broken
foundation — see [§4.4](#44-isolation) for the measured case in this library.

**4. Forgetting is about attention, not storage.** Storage is cheap. Attention is not. You are not
deciding what to delete, you are deciding what to **rank down** — which implies decay should be
non-destructive by default. The cost of a wrong deletion is unbounded; the cost of a wrong down-rank
is one mediocre retrieval.

**5. Reconcile on the write path, not the read path.** Read-time reconciliation pays on every query,
is nondeterministic, and leaves no record. Write-time resolution pays once and leaves an
artifact — a supersession edge is auditable; a read-time tiebreak is not.

**6. A metric that cannot fail is not a measurement.** If a provenance edge links a fact to the
*batch* it was extracted from, then "was this attributed correctly?" is satisfied by construction.
This library has exactly that defect today; it is documented in [§4.1](#41-provenance) rather than
quietly left in place.

---

## 3. The memory types in detail

### 3.1 Semantic memory

**Answers:** *What is true about the entities I deal with, independent of when I learned it?*

**Fails without it.** A travel agent. In March the user mentions in passing that they are vegetarian
and will not fly overnight. In August they say "book me to Lisbon for the conference." Without
semantic memory the agent asks again — or worse, doesn't, and books a red-eye with a chicken meal.

Note what that example actually demands: the fact must survive the session it was uttered in, **and**
be retrievable by a query sharing no vocabulary with the original utterance. "Book me a flight" has
to reach "dietary preference: vegetarian." A keyword index over a transcript will not do it. That
lexical gap is why semantic memory is a distinct type rather than a search feature.

**When it is the wrong tool.** Semantic memory is the most expensive kind of memory to be wrong
about, because it is the kind the agent stops questioning.

- *Flattening time-indexed statements into timeless ones.* "User works at Acme" becomes permanently
  true; a statement that was true when uttered outlives the world it described.
- *Promoting transient states to beliefs.* "User is frustrated" is an episode, not a fact. Stored as
  semantic memory it becomes a permanent character trait.
- *Budget consumption.* Every promoted fact is a permanent claimant on the retrieval budget. Semantic
  memory that grows linearly with conversation length is not consolidating, it is accumulating.

**How you would know it works.** Measure the fraction of correct answers whose supporting fact was
written in a **different session** from the query *and* has low lexical overlap with it. That slice
is the only one that isolates semantic memory from transcript search. Secondary signal: fact count
per entity should plateau. A curve that keeps rising means you are storing restatements.

### 3.2 Episodic memory

**Answers:** *What happened, in what order, and who said it?*

**Fails without it.** A support agent hears: "do the thing we agreed on last Tuesday." Semantic
memory knows the account ID and the communication preference. It does not know that on Tuesday **the
agent itself** proposed a partial refund and the user accepted it conditionally.

This is not hypothetical here. Measured on 2026-08-10 against this library's own extraction:
extraction mined the user's turns for facts *about the user*, and given a turn where the assistant
recommended a specific film, the stored memory was `User asked about …` / `User is interested in …`
— the recommendation itself existed nowhere in the graph. The rationale is recorded in
[`AssistantContentMode.cs`](../src/AgentMemory.Abstractions/Options/AssistantContentMode.cs) lines
6-17. The agent's own proposals were structurally unrepresentable.

Order is the other irreducible property. "The user changed their mind" is only expressible as a
sequence. No set of timeless facts encodes a reversal.

**When it is the wrong tool.**

- *As a primary retrieval surface*, raw episodes are high-volume and low-density. The same fact
  restated twenty times crowds out twenty distinct facts, because similarity search has no notion of
  redundancy.
- *Assistant content admitted as fact-shaped truth converts model speculation into recorded
  knowledge.* If you extract from assistant turns, the provenance must be marked model-generated —
  and many systems structurally cannot, because trust is stamped once per extraction request rather
  than per message. This library is currently one of them; see [§5.5](#55-meta-memory). If you cannot
  label it, do not promote it.

**How you would know it works.** Build a probe set whose answers depend on **sequence** or on
**assistant-originated content**: "what did you recommend?", "what did I change my mind about?",
"what did we decide before I mentioned the budget?" Score that slice separately from fact recall.
Second signal, and it is a trap-detector: check the fan-out of your provenance edges
([§4.1](#41-provenance)).

### 3.3 Procedural memory

**Answers:** *How do things get done here?*

**Fails without it.** A coding agent in a repository where the tests run under one specific command
with one specific environment variable, where a release requires the version bump *before* the tag
push, and where a particular build failure means a stale process is holding a file lock and must be
killed first. None of that is inferable from the source. Without procedural memory the agent
re-derives it every session, gets the ordering wrong a third of the time, and a human re-teaches it.

Note precisely what semantic memory cannot do here. It can hold "the test command is X" as a fact.
It cannot hold an **ordered, conditional trajectory with failure branches** — and the ordering and
the conditions are where the value is. Flatten a procedure into facts and you keep the vocabulary and
lose the method.

**When it is the wrong tool.** Procedural memory is a bet that the environment is stable, and a
confidently-retrieved stale procedure is *worse than no procedure*. The failure mode is specific: an
agent with no procedural memory investigates; an agent with a wrong one executes.

Generalisation is the second hazard. Promoting one successful trajectory to "the way we do this"
overfits to one episode's concrete arguments, and similarity search is exactly the mechanism most
likely to retrieve it for a superficially similar but materially different task.

An honest note that should temper any roadmap: the most effective procedural memory in wide use
today is a hand-written, version-controlled instructions file checked in beside the code. It has no
retrieval step, therefore no retrieval failure; it is always complete; and staleness is caught
socially, by a human reviewing the same diff that invalidated it. A learned procedural tier has to
beat "always right and always loaded."

**How you would know it works.** **Same-task second-attempt cost.** Take a task class; measure steps,
tool calls, and tokens on first encounter and on the *n*th. A flat curve means the procedural memory
is decorative. Then run the falsification test: change the environment so the stored procedure is now
wrong, and check whether the agent detects and updates, or loops. A procedural memory that cannot be
invalidated is not memory — it is a trap with a retrieval index.

### 3.4 Prospective memory

**Answers:** *What am I supposed to do later — and has "later" arrived?*

**Fails without it.** "Remind me to chase the vendor if they haven't replied by Friday." A
query-triggered memory system stores this flawlessly and never surfaces it, because on Friday nobody
asks. The subtler variant is worse: "once the migration ships, switch the default to X" is stored as
an ordinary fact, returned as noise against unrelated queries for weeks, and then indistinguishable
from noise on the day it matters.

**The distinction that clarifies the whole area.** Prospective memory is three separable mechanisms,
routinely conflated:

1. **Expression** — a schema that can represent "this holds from T." A validity window.
2. **Gating** — a read path that honours it: not surfaced before T, surfaced after.
3. **Firing** — acting at T with no query at all. A scheduler.

Most systems that claim prospective memory have (1) only. (1)+(2) gives **due-on-next-interaction**
semantics, which captures most of the value for a conversational agent at essentially zero
infrastructural cost — it is a predicate in a `WHERE` clause. (3) is a different risk class: it makes
the memory layer an *actor*, and actors need delivery guarantees, idempotency, retries, and defined
behaviour when they are wrong at 3 a.m.

**When it is the wrong tool.** When the trigger is the orchestrator's job. If the product already has
durable timers, a workflow engine, or a job queue, putting wall-clock firing in the memory layer
means implementing a scheduler badly, in a component whose failure mode is now "sends things."
Memory's defensible role is to be the **record** of the intention and the **gate** on its visibility.

**How you would know it works.** Two cheap counters:

- **Premature surfacing rate** — how often a not-yet-due item appears in assembled context. Target
  zero. Non-zero means expression without gating.
- **Due-item latency** — elapsed time between an item becoming due and the first assembled context
  containing it. Under purely query-triggered recall this is bounded below by the user's next visit,
  and that number is the product's honest promise.

### 3.5 Meta-memory

**Answers:** *How much should I trust what I just recalled — and do I actually know this, or did I
merely find something nearby?*

**Fails without it.** An agent is asked for a customer's contract renewal date. Retrieval returns
three loosely related items at similarity 0.42, and the agent confidently synthesises a date. The
correct behaviour was "I don't have that" plus a tool call.

The frustrating part is that **every input needed to make that call is usually computed and then
thrown away**: the per-item similarity scores, the candidate count before filtering, whether the
query's key terms even existed in the system's relation vocabulary. Meta-memory is very often not a
missing capability but a discarded one. That is exactly its status here
([§5.5](#55-meta-memory)).

The second failure shape is negative evidence. If the audit trail records only hits — and most do,
because the audit row is written inside the query that matched a node — the system can never learn
**which questions it repeatedly fails**. The misses are the roadmap, and they are usually unrecorded.

**When it is the wrong tool.** Over-application is hedging. An agent that reports uncertainty on
every recall is unusable. Calibration only pays if it **changes behaviour at a threshold**: ask a
clarifying question, call a tool, decline. Confidence that never crosses a decision boundary is UI
decoration.

A related anti-pattern: a trust level used only to *bypass* a check is an allowlist wearing
meta-memory's clothes. Trust has to be able to act as a **floor** and not only as a fast path, or the
ordering on the trust enum is unexercised.

**How you would know it works.** **Selective prediction.** Plot task accuracy against the system's
own confidence and check monotonicity; compare the top-half-confidence slice against the bottom half.
If they are equal, the confidence number is noise. Then the sharper metric: **abstention precision**
— of the answers the system declined to give, what fraction would have been wrong? Above base rate
means calibration is real.

### 3.6 Agent-episodic memory (reasoning traces)

**Answers:** *Have I attempted something like this before, and how did it go?*

**Fails without it.** An agent reconciling a data export. Last week: tool A, rate limit at 10,000
rows, worked around by chunking, eight minutes lost. That episode is invisible to every other memory
type — it is not a fact about the world (semantic), the user never saw it (episodic), and it was
never generalised (procedural). Without a trace layer the agent pays the same eight minutes again.

What makes traces distinct: they record **the agent's own behaviour, including its failures**, and
the failures are the highest-value records, because they are the only ones that say what *not* to do.

**When it is the wrong tool.** As a substitute for semantic memory. A trace answers "how did that
go," never "what is true." Retrieval by task similarity surfaces *trajectories*, and a trajectory
rendered into a context window is narrative — expensive per token and low in factual density. Traces
are also the highest-volume writable memory (a row per step, per tool call) and the fastest to go
stale, since they are pinned to a tool surface that changes.

**The specific trap.** A trace layer without a captured outcome signal is worse than none. If the
recording API cannot express success — typically because the completion call has no such parameter —
every trace is unlabeled. Downstream, unlabeled usually renders as *failed*, so the model is shown a
wall of failed precedents; and filtering to successes returns nothing at all. Compounding it, if
retention evicts by recency alone, good traces are deleted alongside the noise. **Any promotion path
needs a matching exemption in the eviction path.** This library has the first half of that trap
today; see [§5.6](#56-agent-episodic-reasoning-traces).

**How you would know it works.** **Precedent lift**: split tasks by whether a trace above the
similarity threshold was retrieved, and compare steps-to-completion and failure rate across the
split. Prerequisite metric, checked first: **outcome-label coverage** — the fraction of stored traces
carrying a non-null success value. And a blunt one worth running before any of this: *does your
evaluation corpus contain traces at all?*

---

## 4. Cross-cutting properties

These separate a memory system from a store. None can be added later without rewriting the read path.

### 4.1 Provenance

*Why do I believe this?* Provenance is what makes a memory system auditable, correctable, and
evaluable. Without it you cannot show a user where a claim came from, cannot retract everything
derived from a poisoned source, and cannot measure extraction quality at all.

**Resolution is the entire game.** A provenance edge linking a fact to *the batch it was extracted
from* is not provenance; it is a receipt. If each fact points at a dozen source messages, then any
metric of the form "was this extracted from the right message?" is satisfied by construction and can
never fail.

Provenance must also name the **extractor**, not just the source. When you change extraction models,
the question you need to answer is "which of my beliefs came from the model I no longer trust?"

> **Our status.** `EXTRACTED_FROM` is written for facts, entities and preferences — broader label
> coverage than a message-only edge — but at **batch resolution**. A single
> `extraction.SourceMessageIds` list is captured once per extraction call
> ([`PersistenceStage.cs:138`](../src/AgentMemory.Core/Extraction/PersistenceStage.cs)) and applied
> to every item produced by that call (lines 278, 304, 425, 441, 572). Measured over the evaluation
> corpus, a fact links to a mean of 12 source messages, maximum 30. Any gold-coverage metric derived
> from that edge cannot fail. This is a known defect, not a design choice.

### 4.2 Forgetting and decay

Forgetting is not a storage optimisation — see position 4 in [§2](#2-what-makes-a-memory-system-great).
The activation shape that works combines a prior with usage and time. The **log damping is
load-bearing**: linear reinforcement produces a rich-get-richer loop in which whatever ranked highly
once ranks highly forever.

A warning about the reinforcement signal itself: **usage counts derived from your own retrievals are
self-confirming.** "This item was surfaced often" measures your ranker, not the world.

> **Our status.** The retention score is
> `confidence + min(AccessBoostFactor × ln(1 + access_count), MaxAccessBoost)` attenuated by an
> exponential with a 30-day half-life
> ([`MemoryDecayOptions.cs`](../src/AgentMemory.Abstractions/Options/MemoryDecayOptions.cs);
> `DecayQueries.cs`). The damping and the cap were added deliberately: the boost was linear and
> undamped until a bug fix, which let one recall hold a memory above the prune threshold permanently.
> Pruning is non-destructive by default and runs only when explicitly invoked — there is no
> auto-prune-on-extraction. Decay and access tracking cover `Entity`/`Fact`/`Preference` only
> ([`MemoryNodeKind.cs`](../src/AgentMemory.Abstractions/Domain/MemoryNodeKind.cs)); reasoning traces
> receive neither.

### 4.3 Contradiction handling

A store appends. A memory system reconciles. A write has four possible dispositions: **add, update,
no-op, invalidate-the-predecessor.** A system supporting only "add" will, within a year, hold "the
user lives in Berlin" and "the user lives in Lisbon" with equal standing, and return whichever
happens to embed closer to the query.

State the limit honestly: detecting contradiction requires knowing that two statements are about the
same thing *and* are mutually exclusive. Same-subject/same-predicate over a normalised vocabulary is
the tractable case, and it is not most cases.

> **Our status.** Contradiction resolution is non-destructive: the losing fact is stamped with
> `invalidated_at` *and* `valid_until` in one `SET` and linked to the winner by `SUPERSEDED_BY`
> ([`FactQueries.cs`](../src/AgentMemory.Neo4j/Queries/FactQueries.cs), `Supersede`). Nothing in the
> memory path uses `DETACH DELETE` on a superseded fact. Supersession is implemented for
> `Fact → Fact` and `Preference → Preference`. Re-asserting a fact clears `invalidated_at`, so a
> present-time positive assertion restores live recall (`FactQueries.Upsert`, `UpsertBatch`).

### 4.4 Isolation

Not a security afterthought; a **correctness property**. Cross-owner leakage in a memory system is
worse than in a database, because the leaked content is injected into a model's context and restated
in the assistant's own voice as something it knows.

Three levels get conflated and should not be: **owner** (whose), **store/tenant** (which
application), **session** (which conversation). They are not a clean hierarchy — a fact should
outlive its session, a preference should cross sessions but never owners.

**The most under-appreciated failure mode in the field:** isolation implemented as a **post-filter on
a global vector search silently destroys recall.** Ask the index for a global top-K, drop everything
the querying owner does not own, and the owner's effective K is divided by the number of tenants
holding similar content. The query succeeds. No error is raised. The tests pass.

> **Our status — this is measured, and it is the most important number in this document.**
> Neo4j's vector index is global, so an owner filter can only be applied *after* the index has chosen
> its top-K. Measured on 2026-08-10 against a sealed 50-question base: 26,236 facts across 50 owners,
> with an over-fetch of `max(limit×5, limit+50)` = **60** candidates at `MaxFacts = 10`. Probing with
> each owner's own message, the owner's own facts inside that global top-60 came to a **mean of 7,
> minimum 1** — 88% of the budget consumed by other tenants — and one real question retrieved
> **zero** from a graph holding 504 of its own facts, all live, all embedded, all above the
> similarity floor. Full write-up in
> [`OwnerVectorOverFetch.cs`](../src/AgentMemory.Neo4j/Repositories/OwnerVectorOverFetch.cs).
>
> **Isolation itself was never in question — no foreign row is ever returned.** What degrades
> silently is *recall*, and it degrades further with every tenant added. A bounded escalation exists
> (one wider retry, capped at 2,000, only when the first scoped pass returned **zero**), because a
> short-but-non-empty result still answers the question while zero is total failure.
>
> Note the contrast: the fulltext retriever applies its owner `WHERE` *before* `LIMIT`
> ([`FulltextRetriever.cs`](../src/AgentMemory.Neo4j/Retrieval/Internal/FulltextRetriever.cs)), so it
> cannot starve. The starvation is specific to the vector index, which cannot pre-filter on a
> property.

### 4.5 Temporal validity

Two clocks, genuinely different:

- **Valid time** — when the fact holds in the world.
- **Transaction time** — when the system believed it.

"The user's address as of June 1" and "the address as our system knew it on June 1" are different
questions, and only the second reconstructs why the agent did what it did. **Transaction time is the
one you cannot skip**, because it is the debugging axis; valid time is the one that makes the memory
*correct*.

Two traps, both common enough to check for by default:

- **Valid time honoured only on the time-travel path.** If ordinary recall filters on "not
  invalidated" but not on "currently valid," a fact with a future validity start is returned today
  and a fact whose validity expired is returned forever. The property exists, the index exists, the
  tests pass, and the semantics are absent. **Read the live query, not the schema.**
- **No writer ever populates it.** A temporal model is only as good as its most careless write path.

> **Our status — we have both traps.** The transaction clock is enforced everywhere: live fact search
> filters `node.invalidated_at IS NULL`
> ([`FactQueries.SearchByVector`](../src/AgentMemory.Neo4j/Queries/FactQueries.cs), line 216), and
> `RecallAsOfAsync` reconstructs prior belief across entities, facts, preferences and traces.
> The valid-time clock is honoured **only on the as-of path**:
> [`TemporalQueries.SearchFactsAsOf`](../src/AgentMemory.Neo4j/Queries/TemporalQueries.cs) lines 57-58
> apply `valid_from`/`valid_until`; `FactQueries.SearchByVector` lines 211-218 apply `score`,
> `invalidated_at`, and owner — and nothing else. And no extractor populates the fields: every
> `ExtractedFact` construction site omits them, so `PersistenceStage` faithfully copies two values
> that are always null. `Preference` carries no valid-time window at all
> ([`TemporalQueries.cs:76-77`](../src/AgentMemory.Neo4j/Queries/TemporalQueries.cs)).
>
> The practical consequence today is small only by accident: the sole facts carrying `valid_until`
> are supersession losers, and those are stamped `invalidated_at` in the same `SET`, so the existing
> transaction-clock filter already excludes them. The gap is real; its blast radius is currently
> zero rows.

### 4.6 Retrieval budget

Four consequences of position 1 in [§2](#2-what-makes-a-memory-system-great):

1. **Every memory type is a claimant on a shared channel** unless you give it its own.
2. **Rerankers reorder survivors.** Fix the recall ceiling first.
3. **Budgets must be per-section and truncation must be visible.** "40 preferences truncated to 5" is
   information the caller needs. Silent dropping is how a memory system loses the one item that
   mattered and never finds out.
4. **Diversity beats similarity at the margin.** Five near-identical restatements of one fact are one
   fact occupying five slots.

> **Our status.** Budgets are per-section and configurable:
> [`RecallOptions`](../src/AgentMemory.Abstractions/Options/RecallOptions.cs) — `MaxRecentMessages 10`,
> `MaxRelevantMessages 5`, `MaxEntities 10`, `MaxPreferences 5`, `MaxFacts 10`, `MaxTraces 3`,
> `MaxGraphRagItems 5`, `MinSimilarityScore 0.7`. There are **six vector indexes**, each with its own
> independent budget: `message`, `entity`, `preference`, `fact`, `task` (traces), and
> `reasoning_step` ([`SchemaQueries.BuildVectorIndexes`](../src/AgentMemory.Neo4j/Queries/SchemaQueries.cs)).
> That per-index separation is why the crowding in [§4.4](#44-isolation) is a *fact-channel* problem
> rather than a global one — but it also means a capability that writes more `:Fact` rows makes the
> already-starved channel worse.
>
> Two honest qualifications:
> - **Both memory-path rerankers ship off.** The default profile is `MemoryProfile.Parity` ⇒ recency
>   weight 0 and structural γ 1.0 ⇒ semantic-only ranking
>   ([`MemoryRankingOptions.cs`](../src/AgentMemory.Abstractions/Options/MemoryRankingOptions.cs)).
>   BUILT and WIRED; not enabled by default; not measured.
> - **Reciprocal-rank fusion and BM25 exist on a different channel.** `HybridRetriever` (RRF, k=60)
>   and `FulltextRetriever` serve the optional GraphRAG document source, over a host-configured index
>   (`GraphRagOptions.IndexName` / `FulltextIndexName`), not over `Fact`/`Entity`/`Preference` nodes.
>   Separately, the bootstrapper creates three fulltext indexes (`message_content`, `entity_name`,
>   `fact_content`) that **no query in `src/` references**. Lexical retrieval is not fused into
>   long-term memory recall today.
> - `RecallResult` reports `TotalItemsRetrieved` and `Truncated`, but truncation is not reported
>   per section ([`RecallResult.cs`](../src/AgentMemory.Abstractions/Domain/Context/RecallResult.cs)).

---

## 5. Our coverage today

The honest table. Read the status column strictly.

| Type | BUILT | WIRED | MEASURED | One-line summary |
|---|---|---|---|---|
| **Semantic** | yes | yes | yes | Full pipeline: `Entity`/`Fact`/`Preference`, bitemporal, decay, owner isolation, supersession. |
| **Episodic** | yes | yes (default off) | **no** | `AssistantContentMode` added 2026-08-10; default `Ignore`; no evaluation run has used a non-default mode. |
| **Procedural** | **no** | no | no | No concept in the domain. Substrate exists in the trace layer. |
| **Prospective** | **no** | no | no | No first-class concept. `valid_from`/`valid_until` exist but the live read path ignores them; nothing is time-triggered. |
| **Meta-memory** | substrate only | partial | no | Confidence, decay, access tracking, read audit, trust levels exist. Memory cannot report what it does not know. |
| **Agent-episodic (traces)** | yes | yes (defaults off in the MAF adapter) | **no** | Full graph + retrieval + budget. Outcome capture is broken on the adapter path; the evaluation corpus contains no traces. |

### 5.1 Semantic memory — BUILT, WIRED, MEASURED

The only type with end-to-end coverage.

- Node kinds: `Entity`, `Fact`, `Preference` —
  [`MemoryNodeKind.cs`](../src/AgentMemory.Abstractions/Domain/MemoryNodeKind.cs).
- Facts are subject–predicate–object with canonical `*_key` forms; the merge key is
  `{subject_key, predicate_key, object_key, owner_key}` on both the single and batch write paths, so
  a re-extracted triple collapses onto the existing node instead of creating a duplicate
  ([`FactQueries.Upsert`, `UpsertBatch`](../src/AgentMemory.Neo4j/Queries/FactQueries.cs)).
- Recall is vector search over `fact_embedding_idx` / `entity_embedding_idx` /
  `preference_embedding_idx`, owner-scoped, with the post-filter caveat of [§4.4](#44-isolation).
- Two optional completeness levers, both off by default and documented in place:
  `ExpandFactsByPredicate` (returns every fact sharing a top-K hit's canonical predicate, so an
  aggregation question is not silently answered from four of five matching facts) and
  `ResolveQueryRelations` (expands on the relations the query text itself names).
- Relation vocabulary is canonicalised: the measured graph holds `planned` (839 facts) and `plans`
  (14) as separate predicate keys, which is why matching is on `predicate_key` and never on raw text
  ([`MemoryRelationLexicon.cs`](../src/AgentMemory.Core/Memory/MemoryRelationLexicon.cs)).
- Per-phase cost is measured and reproducible — see [`performance/`](performance/README.md). Recall
  and ingestion are reported separately, never as a single "memory overhead" figure.

### 5.2 Episodic memory — BUILT, WIRED, **UNMEASURED**

Messages and conversations have always been stored; what was missing was *extraction from the
assistant's turns*, and therefore any record of what the agent itself said or proposed.

- [`AssistantContentMode`](../src/AgentMemory.Abstractions/Options/AssistantContentMode.cs) —
  `Ignore` (default), `Utterance` (record the act: `assistant | recommended | X`), `Fact` (record the
  claim as an ordinary world fact).
- **WIRED**: settable via `LlmExtractionOptions.AssistantContent`; the instruction is authored once in
  `ExtractionPromptSemantics.AssistantContentInstruction` and consumed by all three LLM extractors
  (`LlmFactExtractor`, `LlmUnifiedMemoryExtractor`, `LlmMultiSessionUnifiedMemoryExtractor`); the
  evaluation CLI exposes `--assistant-content ignore|utterance|fact`.
- The default returns the **empty string**, not a "neutral" instruction, so the prompt is byte-identical
  to before the option existed. Prompt bytes are a measured variable in this project's cost accounting.
- **UNMEASURED**: no evaluation run has been executed with a non-default mode. Nothing is known about
  its effect on answer quality, on graph size, or on the fact-channel crowding in [§4.4](#44-isolation).
- Known hazard before enabling `Fact`: trust is stamped per extraction *request*, not per message
  ([§5.5](#55-meta-memory)), so model-generated claims would be written as `UserProvided`.

### 5.3 Procedural memory — **NOT BUILT**

There is no procedural concept anywhere in the domain: no node label, no property, no option, no
vocabulary entry. A case-insensitive grep for `procedural`/`prospective` across `src/**/*.cs` returns
nothing.

What exists is substrate, and it is more complete than the absence suggests:

- A `ReasoningTrace` + ordered `ReasoningStep`s (`HAS_STEP {order}`) + `ToolCall`s **is** a procedure
  representation; `Thought`/`Action`/`Observation` is a ReAct trajectory.
- Retrieval by task similarity already exists and already composes filters —
  [`ReasoningQueries.SearchByTaskVector`](../src/AgentMemory.Neo4j/Queries/ReasoningQueries.cs) builds
  its `WHERE` from a `List<string>`.
- A tool-reliability prior exists: `:Tool` nodes aggregate `total_calls`, `successful_calls`,
  `failed_calls`, `total_duration_ms`, `last_used_at`, maintained on every tool-call write.
- A detection hook exists: `ConsolidationOptions.DetectLongTraces` (threshold 20 steps) already counts
  summarisation candidates and reports `LongTraceCandidates` — **detection only**, excluded from
  `TotalChanges`.
- `reasoning_step_embedding_idx` is a provisioned, dimension-matched vector index that **nothing
  populates automatically and no query reads** — a retrieval channel already paid for.

See [§7](#7-what-is-deliberately-not-built-and-what-would-trigger-building-it) for what promotion
would take and what would trigger it.

### 5.4 Prospective memory — **NOT BUILT**; substrate present, read path incomplete

No first-class concept. `planned` is not a schema element — it is an emergent predicate produced by
extraction (839 facts in the measured graph). Nothing treats it differently from any other relation.

The substrate and its gap are covered in [§4.5](#45-temporal-validity): `valid_from`/`valid_until`
are real properties, written on both fact paths and writable through the public
`AddFactAsync(Fact, …)` surface, honoured by the as-of path, and **ignored by live recall**. No
extractor populates them, and no MCP tool or facade method exposes them.

Firing is absent by construction. There is exactly one hit for `IHostedService|BackgroundService|PeriodicTimer`
in `src/`, and it is a comment stating that the background enrichment queue deliberately uses a fixed
pool of worker tasks instead
([`BackgroundEnrichmentQueue.cs:19`](../src/AgentMemory.Core/Enrichment/BackgroundEnrichmentQueue.cs)).
**Nothing in this system is time-triggered. All recall is query-triggered.**

### 5.5 Meta-memory — SUBSTRATE ONLY

Everything needed to *build* meta-memory exists. The reporting layer does not.

**What is present:**

- `Fact.Confidence`; a six-level ordered `MemoryTrustLevel`
  (`Untrusted < UserProvided < ModelGenerated < ToolDerived < VerifiedExternal < ApplicationTrusted`).
- ACT-R-style retention scoring, computed identically in Cypher and C# ([§4.2](#42-forgetting-and-decay)).
- `access_count` / `last_accessed_at`, plus a `:MemoryReadAudit` row per recall hit
  ([`DecayQueries.cs`](../src/AgentMemory.Neo4j/Queries/DecayQueries.cs)).
- Lifecycle history (`IMemoryHistoryService`) with access count, read-audit count, invalidation time
  and supersession chain — for `Entity`/`Fact`/`Preference`
  ([`MemoryHistory.cs`](../src/AgentMemory.Abstractions/Domain/History/MemoryHistory.cs)).
- `MemoryContext.ResolvedQueryRelations` — the closest thing in the codebase to "did my vocabulary
  even contain this?"

**What is missing, precisely:**

- **Retrieval diagnostics are computed and then discarded for four of five sections.**
  `RecallOptions.IncludeDiagnostics` (default off) populates `RankedItems` for `RelevantMessages`
  **only** ([`MemoryContextAssembler.cs:323-336`](../src/AgentMemory.Core/Services/MemoryContextAssembler.cs)).
  Facts, entities, preferences and traces all carry the `RankedItems` field, and their repositories
  return `(item, score)` tuples — the assembler drops the score.
- **`RecallResult` cannot express thinness.** It carries `TotalItemsRetrieved` and `Truncated`. It
  cannot distinguish "0 facts because none exist" from "0 facts because `MinSimilarityScore = 0.7`
  excluded them" from "0 facts because owner post-filtering starved the top-K" — the measured case in
  [§4.4](#44-isolation). Three different failures, one indistinguishable output.
- **Misses are unrecordable by construction.** The audit node is created inside
  `MATCH (n:{label} {id: $id})`, so `:MemoryReadAudit` rows exist **only for hits**. There is no record
  anywhere that an owner asked about something and memory had nothing.
- **Trust is stamped per request, not per message.** `request.TrustLevel ?? _options.DefaultTrustLevel`
  is resolved once per extraction call
  ([`MemoryExtractionPipeline.cs:66`](../src/AgentMemory.Core/Services/MemoryExtractionPipeline.cs),
  `.Batch.cs:75`) and applied uniformly in `PersistenceStage`. The default is `UserProvided`. On the
  Neo4j extraction path, `ModelGenerated` is therefore unreachable — the enum has exactly the right
  member and nothing can assign it. (It *is* assigned on the NAMS recall path, where provenance is
  derived per message role: `NamsRecallService.ProvenanceForRole`.)
- **Trust is a bypass and a demotion, never an admission floor.** `MinimumTrustForAdmissionBypass`
  defaults to `ApplicationTrusted` — the maximum — so nothing bypasses injection screening;
  `MinimumTrustForSystemRole` defaults to `Untrusted` — the minimum — so nothing is demoted. Both
  defaults are deliberately inert. There is no "admit nothing below level L" gate for memory items.

### 5.6 Agent-episodic (reasoning traces) — BUILT and WIRED, **UNMEASURED**, with a broken outcome path

The graph layer is the most structurally developed part of the system after semantic memory:

- Labels `ReasoningTrace` / `ReasoningStep` / `ToolCall` / `Tool`; edges `HAS_STEP`, `USES_TOOL`,
  `INSTANCE_OF`, `TOUCHED`, `HAS_TRACE`, `INITIATED_BY`, `TRIGGERED_BY`.
- `ReasoningTrace` carries `Task`, `TaskEmbedding`, `Outcome`, `Success (bool?)`, `OwnerId`, start and
  completion timestamps
  ([`ReasoningTrace.cs`](../src/AgentMemory.Abstractions/Domain/Reasoning/ReasoningTrace.cs)).
- Task text is auto-embedded on trace creation; recall is owner-scoped vector search over
  `task_embedding_idx` with the same over-fetch anti-starvation as facts, plus an as-of variant.
- Retrieval is budgeted (`RecallOptions.MaxTraces = 3`), delivered as `MemoryContext.SimilarTraces`,
  and rendered by the MAF adapter.
- `RecallOptions.SuccessfulTracesOnly` exists and **is** forwarded on the live recall path
  ([`MemoryContextAssembler.cs:267`](../src/AgentMemory.Core/Services/MemoryContextAssembler.cs)). Its
  XML documentation still says automatic recall passes a hardcoded null — that is now stale for the
  live path and still true for the as-of path, which passes `null` at line 416. Two recall paths, two
  behaviours, one option.

**What does not work, stated plainly:**

- **The MAF recorder cannot record success.** `AgentTraceRecorder.CompleteTraceAsync(traceId, outcome,
  cancellationToken)` has **no `success` parameter**
  ([`AgentTraceRecorder.cs:182-203`](../src/AgentMemory.AgentFramework/AgentTraceRecorder.cs)) and calls
  the service without one, although `IReasoningMemoryService.CompleteTraceAsync` does accept `bool?
  success`. Every MAF-recorded trace therefore has `success = null`.
- **Null renders as failure.** The query facade prints `t.Success == true ? "✓" : "✗"`
  ([`MemoryQueryFacade.cs:203`](../src/AgentMemory.Core/Services/MemoryQueryFacade.cs)), so an
  unlabeled trace is shown to the model as a *failed* precedent.
- **`SuccessfulTracesOnly = true` would return zero MAF traces**, because the predicate is
  `node.success = $successFilter` and that is false for null.
- **Traces are outside the maintenance machinery.** No access tracking, no `:MemoryReadAudit`, no
  decay, no history — `MemoryNodeKind` and `MemoryHistoryKind` both have exactly three members. A
  trace cannot be reinforced by use.
- **Retention evicts by age alone.** `PruneSessionTraces` orders by `started_at DESC` and deletes past
  `$keep`, driven by `ReasoningMemoryOptions.MaxTracesPerSession`. No confidence, no access count, no
  success. Any future promotion mechanism needs a matching exemption here or it is silently undone.
- **Off by default in the adapter.** `AgentFrameworkOptions.PersistReasoningTraces = false`;
  `ContextFormatOptions.IncludeReasoningTraces = false`.
- **Not measured.** The evaluation corpus contains no reasoning traces, and the harness's graph probe
  counts only `Entity`, `Fact`, `Preference` and their relationships
  ([`LongMemEvalGraphProbe.cs`](../tools/AgentMemory.LongMemEval/LongMemEvalGraphProbe.cs)). The trace
  surface has never been exercised end to end by a quality run, no matter how many unit and
  integration tests cover it.

---

## 6. Comparison with upstream `neo4j-labs/agent-memory`

AgentMemory is an independent .NET implementation inspired by the Python
[`neo4j-labs/agent-memory`](https://github.com/neo4j-labs/agent-memory) reference project. Two
mechanisms keep that claim honest, and both are executable rather than aspirational:

- **Static schema parity.** `agentmemory schema-parity` compares the .NET schema descriptor against an
  embedded snapshot of the upstream schema and classifies every divergence. A unit test fails the build
  when the report is not compatible. The intentional divergences are enumerated in code —
  [`SchemaParityPolicy.cs`](../src/AgentMemory.Neo4j/Schema/Parity/SchemaParityPolicy.cs).
- **Behavioural conformance.** `tools/AgentMemory.TckBridge` implements the upstream
  [`agent-memory-tck`](https://github.com/neo4j-labs/agent-memory-tck) protocol: **178/178** across
  Bronze, Silver and Gold. Details in [`neo4j-memory-ecosystem.md`](neo4j-memory-ecosystem.md).

Upstream organises around **three layers** — short-term, long-term, and reasoning — rather than the
six-type taxonomy used in this document, and it maps them onto cognitive terms itself. Its reasoning
layer is described as holding *procedural knowledge* and, a few lines later, as the agent's *episodic
memory for problem-solving*; the layer was originally named procedural and the `ProceduralMemory`
alias for `ReasoningMemory` survives. That double-labelling is worth knowing before comparing
vocabularies: **the word "procedural" upstream refers to the trace layer, not to stored reusable
skills.** Neither implementation has procedural memory in the "stored, retrievable, parameterised
skill" sense.

### Type-by-type

| Type | Upstream | Ours |
|---|---|---|
| Semantic | `Entity`/`Fact`/`Preference` + `RELATED_TO`; fixed relation vocabulary | Same labels; canonicalised predicate keys; facts included in assembled context |
| Episodic (messages) | `Conversation`/`Message`, extraction not gated by role | Same labels; extraction gated by `AssistantContentMode`, default `Ignore` |
| Procedural | absent as stored skills (the name is applied to traces) | absent |
| Prospective | absent | absent |
| Meta-memory | confidence, provenance via `:Extractor`, review status on dedup candidates | plus decay, access tracking, `:MemoryReadAudit`, `MemoryTrustLevel` |
| Reasoning traces | first-class; similar-trace search defaults to successful-only | first-class; `SuccessfulTracesOnly` defaults to *no filter* |

### Where we deliberately diverge

Each item below is an entry in the parity policy or a documented decision in code, not an informal
claim.

1. **Multi-tenancy.** We scope reads and writes by a scalar `owner_id` (plus `owner_key`), indexed on
   `Fact`/`Entity`/`Preference`/`ReasoningTrace` and on the `RELATED_TO` edge. Upstream's schema has a
   `:User` label; our parity policy lists it under `UpstreamOnlyLabels` with the note ".NET scopes via
   the `owner_id` property". Our own limitation is separate and stated in [§4.4](#44-isolation): the
   scope is enforced as a post-filter on a global vector search, which is airtight for isolation and
   lossy for recall.
2. **A second temporal axis.** `invalidated_at`, `last_accessed_at`, `access_count`, `memory_id` and
   `read_at` are listed as `NetSupersetProperties` — .NET additions for the transaction-time clock and
   the read-audit trail. Upstream's temporal model is valid-time.
3. **Three extra relationship types.** `HAS_FACT`, `HAS_PREFERENCE`, `IN_SESSION`, allowlisted as
   `NetOnlyRelationshipTypes`.
4. **Zero .NET-only node labels.** `NetOnlyLabels` is empty, deliberately: adding a label is a
   conscious act that must be recorded in the policy before the parity test will pass.
5. **A typed failure taxonomy we do not model.** Upstream's trace carries `error_kind`; we have
   free-text `Outcome` plus `bool? Success`. `error_kind` sits in our `UpstreamOnlyProperties` list.
   This matters for any future procedural work, because "why it failed" is the only thing that makes a
   failed trace instructive.
6. **Opposite defaults on trace outcome filtering, on purpose.** Upstream treats successful-only as
   correctness and defaults to it. We default `SuccessfulTracesOnly` to `null` — no filter — and the
   reasoning is written into the option itself: nothing becomes a default here before it is measured,
   and the trace surface has never been measured at all
   ([`RecallOptions.cs:78-89`](../src/AgentMemory.Abstractions/Options/RecallOptions.cs)). Given
   [§5.6](#56-agent-episodic-reasoning-traces), upstream's default is the safer one for a host to
   adopt today, and switching ours is gated on fixing outcome capture first, not on a preference.
7. **A set of interop-critical property names that must never drift.** `id`, `name`, `type`,
   `embedding`, `confidence`, `subject`/`predicate`/`object`, `valid_from`/`valid_until`,
   `task`/`task_embedding`, `thought`/`action`/`observation`, `tool_name`/`status`/`duration_ms` and
   others are pinned by the parity policy: a rename on either side is a build failure, which is exactly
   how a silent divergence gets caught.

---

## 7. What is deliberately not built, and what would trigger building it

Nothing in this section is scheduled. Each entry states what exists, what is missing, and the concrete
signal that would justify the work.

### 7.1 Fix outcome capture on reasoning traces

**Missing:** a `success` value on the adapter's completion path ([§5.6](#56-agent-episodic-reasoning-traces)).

**Cost:** one overload on `AgentTraceRecorder.CompleteTraceAsync` — an overload rather than an added
optional parameter, because the method is public and the API surface is locked under SemVer.

**Why it comes first:** it is the prerequisite for *everything* built on traces. Today, filtering to
successful precedents returns nothing, and unfiltered recall shows the model a wall of ✗ marks.

**Trigger:** any host enabling `PersistReasoningTraces`. This is a defect, not a feature request.

### 7.2 Procedural memory as trace promotion

**Exists:** the representation, the retrieval, the delivery path, a tool-reliability prior, a detection
hook, and a free second vector channel — all enumerated in [§5.3](#53-procedural-memory--not-built).

**Missing:** (a) the outcome signal above; (b) a **filterable** procedure marker. It has to be a real
property, not a `Metadata` entry: metadata round-trips as a single serialised JSON string, so a marker
inside it is invisible to Cypher and both the recall filter and the prune exemption would degrade to
full label scans. This is the one place the project's "land a speculative field in `Metadata` first"
convention does not apply.

**Two traps that make a naive implementation self-defeating:**

- **Promotion without a prune exemption.** `PruneSessionTraces` evicts by age alone; a promoted
  procedure would be deleted by recency.
- **A filter that is not opt-in.** The TCK exercises `get_similar_traces`. A new predicate must default
  to *inactive* so the emitted Cypher for existing callers stays byte-identical.

**Retrieval-budget note, and it is the argument in favour:** promoted procedures would arrive through
`task_embedding_idx` with its own budget (`MaxTraces = 3`) and a current occupancy of zero. Unlike
episodic fact extraction, this adds **no** claimant to the starved fact channel.

**Trigger:** a repeated multi-step task workload where same-task second-attempt cost can be measured
([§3.3](#33-procedural-memory)). Conversational QA cannot measure this — the workload is not in the
corpus. Building the harness is part of the cost, and should be honestly counted as such.

### 7.3 Prospective memory as a valid-time gate

**Exists:** the properties, the write path, and the exact filter expression — on the as-of path.

**Missing:** the same two clauses on the live path, and any writer that populates the fields.

**Cost:** the read gate is two conditional `AND`s in `FactQueries.SearchByVector`, copied from
`TemporalQueries.SearchFactsAsOf`. Because no extractor writes valid-time and the only rows carrying
`valid_until` are already excluded by the transaction-clock filter, **turning the gate on changes the
result set for zero currently-existing rows** — which makes it safe to ship and impossible to measure
on its own. Real measurement needs the extraction side too, and that changes the graph.

**Explicitly out of scope:** firing. That is a new hosting component with delivery guarantees and
retry semantics, and it belongs to the orchestrator unless there is a specific reason it cannot
([§3.4](#34-prospective-memory)).

**Trigger:** either (a) a correctness complaint — an expired fact returned forever is a live bug the
gate fixes — or (b) enough date-bearing questions in an evaluation corpus for the arm to detect an
effect. Count them before spending a rebuild.

### 7.4 Meta-memory that reports its own sufficiency

**Exists, and is discarded:** per-item scores on four of five sections; the pre-owner-filter candidate
count; vocabulary coverage ([§5.5](#55-meta-memory)).

**Cost of the first increment:** none of it is storage. Populate `RankedItems` on the fact, entity,
preference and trace sections under the existing `IncludeDiagnostics` toggle, reusing the existing
builder; then derive per-section `TopScore`/`Count` on `RecallResult`. No schema change, no extra
query, no extra round trip.

**Why it ranks first on usefulness:** it is the instrument. Without it, the effect of every other
change on the measured 7-of-60 in [§4.4](#44-isolation) is unobservable.

**Second increment, harder:** the candidates-seen-before-owner-filter count. The over-fetch happens
inside Cypher and the `LIMIT` is applied after filtering, so the pre-filter count is discarded in the
database and needs a widened projection.

**Deferred:** negative-evidence records. `:MemoryReadAudit` cannot be extended to cover misses because
it is keyed on a matched `memory_id`; a miss has none. That is a new node label, and it must ship with
a growth story from the first commit. The read-audit precedent is the warning: a recall writes roughly
25 audit rows, so an unindexed lookup over that label degraded **with time rather than with data
size** — a store fast on day one and slow on day ninety with an unchanged graph. That one was fixed
with an index (`memory_read_audit_memory_id_idx`); a per-recall miss record needs retention as well.

**Trigger:** any product decision that depends on abstention ("say I don't know instead of guessing"),
or any attempt to measure the effect of a retrieval change.

### 7.5 Message-level provenance

**Missing:** per-message (ideally per-offset) `EXTRACTED_FROM` resolution ([§4.1](#41-provenance)).

**Why it is not cosmetic:** it gates the honest evaluation of extraction quality, and it gates any
salience signal derived from how often something was genuinely mentioned. A reinforcement signal
derived from our own retrievals instead would measure the ranker, not the world.

**Trigger:** the first time an extraction-quality metric needs to be able to fail.

### 7.6 Isolation pushed into the index

**Missing:** pre-filtered, partitioned, or per-tenant vector retrieval.

**Why it outranks ranking work:** every reranker reorders survivors. At a mean of 7 usable candidates
out of a 60-row budget, a reranker reorders seven items and reports success. This is the one
constraint in this document that is *measured* rather than argued, and it bounds the achievable gain
from every other retrieval change.

**Trigger:** it is already triggered. What is missing is a mechanism, not a justification — Neo4j's
vector index cannot pre-filter on a property, so the options are partitioning by tenant, a different
index strategy, or a hybrid candidate generator whose lexical half (which *can* filter before
`LIMIT`) compensates.

---

## Checking these claims yourself

```bash
# Schema divergences from upstream, classified (no database needed)
agentmemory schema-parity

# Does a live database actually have every constraint and index?
agentmemory schema-check

# Deterministic memory-quality checks: persistence, retrieval, ranking,
# isolation, temporal history, provenance, latency
agentmemory evaluate
```

Related reading: [`architecture.md`](architecture.md) · [`schema.md`](schema.md) ·
[`performance/README.md`](performance/README.md) ·
[`neo4j-memory-ecosystem.md`](neo4j-memory-ecosystem.md) ·
[`security/threat-model.md`](security/threat-model.md)

---

*Last verified against the codebase on 2026-08-10. Line numbers drift; symbol names and file paths are
the durable references. If a claim here disagrees with the code, the code is right and this document is
a bug.*
