# The Memory Map

A reference for what agent memory is, what a memory system has to decide that a store does not, and
**exactly** which of those decisions AgentMemory makes today.

This is not a feature page. Several sections below describe things this library does not do, does
partially, or does in a way that has never been measured. That is the point: a memory map that only
lists strengths is a brochure, and you cannot plan against a brochure.

**If you arrived looking for "short-term, long-term and reasoning memory," start at
[§2](#2-two-vocabularies-three-memory-layers-six-memory-types).** This document uses two vocabularies
side by side: the three **layers** that name the public API, and six **types** that name capabilities.
§2 maps them onto each other, says where the mapping breaks, and corrects the claims about this
library that the code does not support.

---

## How to read the status labels

Three words are used throughout, and they mean three different things:

| Label | Means |
|---|---|
| **BUILT** | The type, query, or option exists in the codebase. |
| **WIRED** | It is reachable from configuration or from a first-party read/write path — not just present. |
| **MEASURED** | Its effect has been observed in an evaluation run, and the number is written down. |

Most published memory claims — in this project and elsewhere — answer the first question and are
presented as though they answered the third. Everything in [§6](#6-our-coverage-today) is labelled.

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

These six are *not* the three memory layers this project ships and publishes. They overlap, they do not
line up one to one, and neither vocabulary subsumes the other —
[§2](#2-two-vocabularies-three-memory-layers-six-memory-types) maps them.

---

## 2. Two vocabularies: three memory layers, six memory types

This project publishes **two** vocabularies for one system, and until this section existed the document
used only one of them. A reader arriving from the README or a public write-up looks for *short-term /
long-term / reasoning* and finds none of those words below. That was a documentation defect, and this
section is the bridge.

- **Three memory layers** — *short-term*, *long-term*, *reasoning*. This is the **product vocabulary**.
  It is the public API surface (`ShortTermMemoryService`, `ILongTermMemoryService`,
  `IReasoningMemoryService`), the namespace layout (`Domain/ShortTerm`, `Domain/LongTerm`,
  `Domain/Reasoning`), and the framing used in [`README.md`](../README.md) and
  [`architecture.md`](architecture.md). It is also **upstream's** vocabulary — `neo4j-labs/agent-memory`
  groups its Python package into `short_term.py` / `long_term.py` / `reasoning.py` under a module
  docstring naming exactly those three, and its README presents them as three columns. Keeping it is an
  interop and TCK decision, not a stylistic one, and it is locked under SemVer at 1.3.0.
- **Six memory types** — semantic, episodic, procedural, prospective, meta-memory, agent-episodic
  ([§1](#1-what-an-agent-memory-system-is-for)). This is the **analysis vocabulary**, used everywhere
  else in this document. It appears nowhere in the code, deliberately: it exists so that *"this system
  has no procedural memory"* is a sentence that can be said at all.

The layers answer **where memory is kept**. The types answer **what memory can do**. Neither replaces
the other, and the rest of this document uses both.

### 2.1 The three layers, as the code defines them

| Layer | Public entry point | Node labels | Automatic producer | Status |
|---|---|---|---|---|
| **Short-term** | `ShortTermMemoryService` | `Conversation`, `Message` | yes — `Neo4jChatHistoryProvider` persists every turn | BUILT, WIRED, MEASURED |
| **Long-term** | `ILongTermMemoryService` | `Entity`, `Fact`, `Preference` (+ `Extractor`, `Schema`) | yes — the extraction pipeline | BUILT, WIRED, MEASURED |
| **Reasoning** | `IReasoningMemoryService` | `ReasoningTrace`, `ReasoningStep`, `ToolCall`, `Tool` | **no** | BUILT, WIRED, **UNMEASURED** |

**Short-term memory** is durable, session-scoped message storage.
`(:Conversation)-[:HAS_MESSAGE]->(:Message)`, written by
[`ShortTermMemoryService`](../src/AgentMemory.Core/Services/ShortTermMemoryService.cs) over
[`MessageQueries`](../src/AgentMemory.Neo4j/Queries/MessageQueries.cs) and
[`ConversationQueries`](../src/AgentMemory.Neo4j/Queries/ConversationQueries.cs). Two independent read
paths, each with its own budget: chronological (`GetRecentBySession`, ordered by `timestamp DESC`,
`MaxRecentMessages = 10`) and semantic (`SearchByVector` over `message_embedding_idx`,
`MaxRelevantMessages = 5`). A "session" here is a `session_id` **string** denormalised onto nodes —
there is no session node, no lifecycle, no open or close. Two ordering edges are written,
`FIRST_MESSAGE` and `NEXT_MESSAGE`, and the source says plainly that neither is ever read or traversed
(`MessageQueries.LinkNextMessage`, doc comment); order is always recovered from the `timestamp`
property. **Nothing in this layer expires** — see [§2.4](#24-claims-in-circulation-that-the-code-does-not-support).

**Long-term memory** is the knowledge graph: `Entity` / `Fact` / `Preference` — exactly the three
members of [`MemoryNodeKind`](../src/AgentMemory.Abstractions/Domain/MemoryNodeKind.cs), which is also
the boundary of every maintenance mechanism in the system. Decay, access tracking, `:MemoryReadAudit`,
lifecycle history and supersession all stop at that boundary; messages and traces receive none of them.
This is the only layer with an automatic producer — the extraction pipeline
([`PersistenceStage.cs`](../src/AgentMemory.Core/Extraction/PersistenceStage.cs)) fills it with no
application code at all.

**Reasoning memory** is a ReAct-trajectory graph:
`(:ReasoningTrace)-[:HAS_STEP {order}]->(:ReasoningStep)-[:USES_TOOL]->(:ToolCall)-[:INSTANCE_OF]->(:Tool)`
([`ReasoningQueries.cs`](../src/AgentMemory.Neo4j/Queries/ReasoningQueries.cs),
[`ToolCallQueries.cs`](../src/AgentMemory.Neo4j/Queries/ToolCallQueries.cs)). Recall is owner-scoped
task-text vector search over `task_embedding_idx`, budgeted at `MaxTraces = 3` and delivered as
`MemoryContext.SimilarTraces`. It is structurally the second-most developed part of the schema and the
least exercised part of the product: it has **no automatic producer**, and it is off by default in the
only agent-framework adapter. Full detail in [§6.6](#66-agent-episodic-reasoning-traces--built-and-wired-unmeasured).

### 2.2 The mapping

Layers to types. Coverage words are the status labels from the top of this document.

| Layer | Cognitive type it implements | Coverage |
|---|---|---|
| **Short-term** | **Episodic** — the storage half only | PARTIAL. Turns are stored with `role` and `timestamp`, and nothing mines them. Order is stored and never *returned as order*: the recency leg gives an unordered top-10 by timestamp, the relevance leg an unordered top-5 by cosine, and the two ordering edges are never traversed. |
| **Long-term** | **Semantic** | FULL — BUILT, WIRED, MEASURED. [§6.1](#61-semantic-memory--built-wired-measured) |
| | **Episodic** — the assistant-originated half | BUILT, WIRED, **default off**, **MEASURED** (2026-08-10). `AssistantContentMode.Utterance` stores `assistant \| recommended \| X` as an ordinary `:Fact`. Capture +42% facts; retrieval **32.3% of the structured budget in 33/50 questions**; cost **+23.1% prompt tokens**; accuracy unmoved. [§6.2](#62-episodic-memory--built-wired-measured) |
| | **Meta-memory** — substrate only | Confidence, `MemoryTrustLevel`, `access_count`, `:MemoryReadAudit`, `IMemoryHistoryService` — all scoped to the three long-term kinds. [§6.5](#65-meta-memory--substrate-only) |
| | **Prospective** — expression only | `valid_from` / `valid_until` exist on `Fact`; live recall ignores them and no extractor writes them. [§5.5](#55-temporal-validity) |
| **Reasoning** | **Agent-episodic** | BUILT, WIRED (task-similarity recall only), **UNMEASURED**. [§6.6](#66-agent-episodic-reasoning-traces--built-and-wired-unmeasured) |
| | **Procedural** — substrate only | NOT BUILT. The ordered-step representation, the `:Tool` reliability prior and a spare vector index exist; the concept does not. [§6.3](#63-procedural-memory--not-built) |

And the reverse view, which is where it stops being tidy:

| Cognitive type | Product layer that owns it |
|---|---|
| Semantic | Long-term. The only clean one-to-one. |
| **Episodic** | **Split across short-term and long-term.** See [§2.3](#23-where-the-mapping-is-imperfect). |
| Procedural | *none* |
| Prospective | *none* (properties live in long-term; no gate, no writer) |
| Meta-memory | *none named* (substrate lives in long-term; the README files it under "Memory Governance") |
| Agent-episodic | Reasoning. One-to-one in name. |

### 2.3 Where the mapping is imperfect

Four places, stated plainly.

**1. Episodic memory is split across two layers, and the layer name sends you to the wrong one.**
Ask an agent *"what did you recommend last time?"*. The product vocabulary points at **short-term
memory** — it is conversational, recent, session-scoped, everything the name suggests. The verbatim
turns are indeed there, and nothing mines them. The mechanism that can actually answer lives in
**long-term memory**: `AssistantContentMode.Utterance` emits a `:Fact` triple with `assistant` as the
subject, retrieved through the *semantic* vector index, against the *semantic* budget
(`MaxFacts = 10`) — and its default is `Ignore`, so out of the box the answer is in neither layer.
Measured on 2026-08-10 and recorded in
[`AssistantContentMode.cs`](../src/AgentMemory.Abstractions/Options/AssistantContentMode.cs) lines
6-17: given a turn where the assistant recommended a specific film, the stored memory was
`User asked about …` / `User is interested in …` and the recommendation existed nowhere in the graph.

**2. Three of the six types have no layer at all.** Procedural, prospective and meta-memory are not
absent because they were assigned somewhere unhelpful — there is no product word for them. A reader
given only "three memory layers, not one" has no vocabulary in which to notice the absence. That is
the difference between a taxonomy with gaps and a taxonomy that hides them, and it is the reason this
document keeps the six-type vocabulary.

**3. GraphRAG is classified by neither taxonomy.** It has its own budget (`MaxGraphRagItems = 5`), its
own context section (`MemoryContext.GraphRagContext` / `GraphRagItems`) and its own retrievers
(`HybridRetriever` with RRF, `FulltextRetriever`). It is not one of the three layers and it is not one
of the six types. A retrieval channel with an index, a budget and a context section that no vocabulary
owns is a channel whose quality nobody owns.

**4. The three layers are not peers, and "layers" implies they are.** Long-term has an automatic
producer, decay, access tracking, a read audit, trust levels, lifecycle history, supersession,
bitemporal recall and measured evaluation. Short-term has a write path and two read queries. Reasoning
has the richest schema of the three and no producer at all. Presenting them as three equal layers is
the mechanism by which "reasoning memory" reads as a shipped capability when what shipped is a schema.

### 2.4 Claims in circulation that the code does not support

Each item below is a phrase that appears in this project's own published material or follows directly
from it. Each is corrected against the code. Where a claim is *partly* true, the true part is kept.

**"Short-term memory."** The adjective is a storage-tier claim, and the tier does not exist. There is
no TTL, no eviction, no retention window, no size cap, and no participation in decay — `Message` and
`Conversation` carry none of `confidence`, `access_count`, `last_accessed_at` or `invalidated_at`, and
`DecayQueries.BuildPrune` is called only for entities, facts and preferences. Nothing summarises or
compresses messages. `ShortTermMemoryOptions` has exactly two numeric knobs
(`DefaultRecentMessageLimit = 10`, `MaxMessagesPerQuery = 100`) and both are **read-time only**.
Messages are retained permanently until an application calls `ClearSessionAsync`, which is a hard
`DETACH DELETE` of the whole session. The only bound that functions is a 10-item read window over an
unbounded log. **Accurate phrasing: "conversation memory — durable, session-scoped message storage
with a configurable recall window."**

**"Sessions," as a lifecycle.** A session is a string. `SessionInfo`
(`Domain/ShortTerm/SessionInfo.cs`) is the only type modelling session close, via `EndedAtUtc`, and a
repo-wide search finds no producer and no consumer for it anywhere in `src/` — it is a dead type.
`ISessionIdGenerator` is registered in DI, and its `GenerateSessionId` has **zero production callers** —
only the interface declaration, the implementation and a unit test — so the `PerDay` and
`PersistentPerUser` values of `SessionStrategy` are BUILT and unreachable.

**Conversation archival, as a working hygiene pass.** `ConsolidationQueries.ArchiveExpiredConversations`
sets `c.archived = true`, and no recall query filters on it: `GetRecentBySession`, `GetAllBySession`,
`GetByConversation`, `SearchByVector`, `ConversationQueries.GetBySession` and `ListSessions` contain no
`archived` predicate. An archived conversation stays fully recallable; the flag is read back only by
the record mapper (`Neo4jConversationRepository`) and by the archival query's own
already-archived guard. `SchemaQueries.ConversationArchivedIndex` creates `conversation_archived_idx`,
which backs zero queries. The pass also has no hosted service and no timer — the only production
caller of `ConsolidateAsync` in the repository is the CLI `memory consolidate` verb, and
`ConsolidationOptions.DryRun` defaults to `true`, so nothing mutates without `--apply`. And its cutoff predicate is
`c.updated_at < datetime($cutoff)`, while `updated_at` is bumped only by `ConversationQueries.Upsert`;
all three message-write paths MERGE the conversation with `ON CREATE SET` only, so writing a message
never touches it. The predicate therefore measures conversation *age*, not inactivity. BUILT; not
WIRED to any automatic path; UNMEASURED — the consolidation tests assert returned counts, never that
archiving changes what recall returns.

**"A POLE+O model."** POLE+O is real as a *default prompt vocabulary* and as the shape of the
persisted `:Schema` document. It is not a model in the sense of a constraint. **Nothing validates an
entity's type.** `Entity.Type` is a `required string` that reaches Neo4j as free text.
[`EntityType`](../src/AgentMemory.Abstractions/Domain/LongTerm/EntityType.cs) — the canonical POLE+O
constant list, with `IsKnownType` and `Normalize` — has **zero callers in `src/` and `tools/`**.
`DefaultSchemas.GetPoleoEntityTypes()` is a faithful port of the upstream catalogue but is only
serialised into `:Schema` nodes; no write path reads it back to check anything.
`LlmExtractionOptions.EntityTypes` defaults to the five POLE+O names and is a user-replaceable
`IReadOnlyList<string>` that goes straight into a prompt; the only post-processing is a four-entry
synonym map, and an off-model type is never rejected. A fourth list,
`Neo4jEntityRepository.ValidEntityLabels`, decides which types and subtypes become Neo4j labels — 21
flat names that disagree with `DefaultSchemas` in both directions: of PERSON's six declared subtypes
only `INDIVIDUAL` survives, and `ANIMAL`, `BUILDING`, `CONFERENCE` and `GROUP` appear in no schema.
The parity verifier does not cover this: it gates labels, relationship types and property *names*, not
dynamic entity labels or type/subtype validity.

**"Facts with provenance," read as per-statement attribution.** `EXTRACTED_FROM` is written for facts,
entities and preferences, at **batch** resolution — the full mechanism and its measured mean-12
fan-out are in [§5.1](#51-provenance). Two further limits belong here: `EXTRACTED_BY`, the edge naming
which extractor produced an item, is implemented
(`IExtractorRepository.CreateExtractedByRelationshipAsync`) and has **zero production callers**, so
`EntityProvenance.Extractors` is structurally always empty; and the confidence, character-offset and
context arguments that `IEntityRepository`'s provenance overload accepts are never supplied by
`PersistenceStage`, so span-level provenance is BUILT end-to-end and never populated. The fact and
preference provenance methods do not have those parameters at all.

**"Temporal validity," read as a shipped behaviour.** The *transaction* clock is real and enforced
everywhere. The *valid-time* clock is inert end to end: no extractor populates `valid_from` or
`valid_until` — `LlmFactDto` carries only `source_session`, `subject`, `predicate`, `object` and
`confidence` — and live recall does not filter on them.
Full treatment in [§5.5](#55-temporal-validity). Until a writer exists, this should not appear in a
feature list.

**"Decay," unqualified.** Both forms ship **off**. Decay-based *ranking* requires a profile above the
`MemoryProfile.Parity` default, which sets recency weight 0 and structural γ 1.0. Decay-based
*forgetting* runs only when explicitly invoked; `MemoryDecayOptions` states it in the source. What *is*
on by default is the ACT-R **input** maintenance — `last_accessed_at` and `access_count` are updated on
every recall and consumed by nothing unless you opt in. The honest word is **"(opt-in)"**.

**"Optional geo enrichment."** This is the clearest mismatch found. Geo *storage* is real: `Entity`
carries `Latitude`/`Longitude`, they are written as a Neo4j `point({latitude, longitude})`, read back
WGS-84-correct, and `entity_location_idx` is a genuine point index created at bootstrap. Geo
*enrichment* does not exist. `IGeocodingService.GeocodeAsync` has **zero callers in `src/`, `tools/`
and `samples/`** — the only invocations in the repository are its own unit tests. `WithEnrichment(...)`
registers the Cache → RateLimit → Nominatim chain into DI where nothing resolves it. The one component
that could have connected them,
[`BackgroundEnrichmentQueue`](../src/AgentMemory.Core/Enrichment/BackgroundEnrichmentQueue.cs), takes
`IEnumerable<IEnrichmentService>` rather than `IGeocodingService`, writes only `Description`, is
`internal sealed`, and is **never registered in DI anywhere in the repository**. The geo *query*
surface is orphaned too: `SearchByLocationAsync` and `SearchInBoundingBoxAsync` exist on
`IEntityRepository` and in Cypher, are not exposed on `ILongTermMemoryService`, are not exposed by any
MCP tool, and have no caller outside unit tests. **Accurate phrasing: "geospatial storage and
radius/bounding-box queries on entities, populated by the caller."** The word *enrichment* implies
automatic population, and there is none.

**"Reasoning traces are first-class citizens," read as default behaviour.** The schema is first-class;
the behaviour is opt-in and, from a host, one-directional. There is no interceptor, middleware or
pipeline hook that starts a trace — the only production callers of `StartTraceAsync` are the MAF
recorder and the MCP tool, both of which an application author must call explicitly.
`AgentFrameworkOptions.PersistReasoningTraces` defaults to `false`, and with it off `AgentTraceRecorder`
returns synthetic in-memory objects and never contacts Neo4j; `ContextFormatOptions.IncludeReasoningTraces`
defaults to `false`, so a persisted trace is discarded before it reaches the model anyway. The MCP
server's reasoning surface is **write-only** — `memory_start_trace`, `memory_record_step`,
`memory_record_tool_call`, `memory_complete_trace`, and no read tool for traces, steps or tool calls;
the only read is the `similarTraces` array inside `memory_search` / `memory_get_context`, which returns
traces without their steps. `GetTraceWithStepsAsync` has no caller in `src/` at all. The trace-to-conversation
edges that would make a trajectory traversable (`INITIATED_BY`, `HAS_TRACE`/`IN_SESSION`,
`TRIGGERED_BY`), together with the `:TOUCHED` edge to the knowledge graph, are all implemented and
called by nothing outside the CLI evaluator and tests. **"How the agent got there" is representable in this
schema and is not, today, queryable through any shipped host surface.** The one claim that survives
intact is *similar-task retrieval* — and note that it retrieves *task titles*: the MAF renderer emits
`t.Task` and nothing else, dropping outcome and success.

**Two shipped samples report a persistence that does not happen.**
`samples/AgentMemory.Sample.MinimalAgent/Program.cs` and
`samples/AgentMemory.Sample.BlendedAgent/Program.cs` both record a trace and log
`"Trace recorded successfully."`, and neither sets `PersistReasoningTraces`, so nothing is written to
Neo4j. Their `catch` blocks, commented as "expected when no live Neo4j instance is available", are
unreachable on that path because the disabled path never contacts Neo4j. This is a defect in shipped
teaching material, not a documentation nuance.

**Measured status of this section.** The message store is measured incidentally: the corpus probe
[`k6-trace-probe.json`](../artifacts/evaluation/k6-trace-probe.json) (2026-08-09) counts 14,621
messages and 10,382 entities in the evaluation graph. In the same probe, **traces: 0, steps: 0**, with
`task_embedding_idx` and `reasoning_step_embedding_idx` both ONLINE — so the zero is real emptiness,
not index failure. Archival read-back, the unindexed-scan cost in [§2.5](#25-two-defects-this-mapping-surfaced),
the geo surface and the step/tool-call surfaces are **UNMEASURED**; only live-Neo4j integration tests
touch the last of these.

### 2.5 Two defects this mapping surfaced

Neither had been recorded anywhere before this section was written. One is in the short-term layer,
one in the reasoning layer.

**`Message.session_id` was the primary recall predicate and had no index. FIXED.**
`GetRecentBySession`, `GetAllBySession` and `DeleteBySession` all match
`(m:Message {session_id: $sessionId})`, and `SchemaQueries.PropertyIndexes` contained
`conversation_session_idx`, `message_timestamp_idx` and `message_role_idx` — and no index on
`Message.session_id`. The planner had no seek for that predicate, so the plan was proportional to the
**total number of messages in the store**, not to the session. This was a port-introduced regression:
upstream's `Message` has no `session_id` property at all and reaches messages by traversing the indexed
`Conversation.session_id`. We denormalised the property and did not index it. Combined with the absence
of any expiry mechanism, the scanned set grew without bound for the life of a deployment.

Closed by `message_session_timestamp_idx` (`SchemaQueries.MessageSessionTimestampIndex`, migration
`0007_message_session_timestamp.cypher`), a **composite** on `(session_id, timestamp)`: `session_id`
leads so its prefix serves all three queries above, and the trailing column is pushed into the index
for `TemporalQueries.GetRecentMessagesAsOf`, which adds a `timestamp <= $asOf` range to the same
equality. Note the failure mode this had, because it generalises: the fallback plan was not always a
full label scan — the planner could also walk `message_timestamp_idx` backwards until `$limit` matches
accumulated, which is **fast for the session just written to and unboundedly slow for an idle session
in a busy store**. A defect that is bimodal on data distribution rather than uniformly slow is one that
benchmarks on a fresh store will not reproduce.

**`:ToolCall` nodes are orphaned by every deletion path, and `:Tool` counters drift upward
permanently.** `ReasoningQueries.DeleteBySession` and `PruneSessionTraces` both `DETACH DELETE` the
trace and its `ReasoningStep` children, and neither touches the `ToolCall` nodes hanging off those
steps — no query in the repository `DETACH DELETE`s a `ToolCall`. After `ClearSessionAsync` or any
retention prune, those nodes survive, invisible to `ToolCallQueries.GetStats` (which traverses from the
trace) yet still counted in the `:Tool` aggregate, whose `total_calls` / `successful_calls` /
`failed_calls` counters are only ever incremented. Any future tool-reliability prior built on `:Tool`
inherits a monotonically drifting denominator.

---

## 3. What makes a memory system great

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
foundation — see [§5.4](#54-isolation) for the measured case in this library.

**4. Forgetting is about attention, not storage.** Storage is cheap. Attention is not. You are not
deciding what to delete, you are deciding what to **rank down** — which implies decay should be
non-destructive by default. The cost of a wrong deletion is unbounded; the cost of a wrong down-rank
is one mediocre retrieval.

**5. Reconcile on the write path, not the read path.** Read-time reconciliation pays on every query,
is nondeterministic, and leaves no record. Write-time resolution pays once and leaves an
artifact — a supersession edge is auditable; a read-time tiebreak is not.

**6. A metric that cannot fail is not a measurement.** If a provenance edge links a fact to the
*batch* it was extracted from, then "was this attributed correctly?" is satisfied by construction.
This library has exactly that defect today; it is documented in [§5.1](#51-provenance) rather than
quietly left in place.

---

## 4. The memory types in detail

### 4.1 Semantic memory

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

### 4.2 Episodic memory

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
  than per message. This library is currently one of them; see [§6.5](#65-meta-memory--substrate-only). If you cannot
  label it, do not promote it.

**How you would know it works.** Build a probe set whose answers depend on **sequence** or on
**assistant-originated content**: "what did you recommend?", "what did I change my mind about?",
"what did we decide before I mentioned the budget?" Score that slice separately from fact recall.
Second signal, and it is a trap-detector: check the fan-out of your provenance edges
([§5.1](#51-provenance)).

### 4.3 Procedural memory

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

### 4.4 Prospective memory

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

### 4.5 Meta-memory

**Answers:** *How much should I trust what I just recalled — and do I actually know this, or did I
merely find something nearby?*

**Fails without it.** An agent is asked for a customer's contract renewal date. Retrieval returns
three loosely related items at similarity 0.42, and the agent confidently synthesises a date. The
correct behaviour was "I don't have that" plus a tool call.

The frustrating part is that **every input needed to make that call is usually computed and then
thrown away**: the per-item similarity scores, the candidate count before filtering, whether the
query's key terms even existed in the system's relation vocabulary. Meta-memory is very often not a
missing capability but a discarded one. That is exactly its status here
([§6.5](#65-meta-memory--substrate-only)).

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

### 4.6 Agent-episodic memory (reasoning traces)

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
today; see [§6.6](#66-agent-episodic-reasoning-traces--built-and-wired-unmeasured).

**How you would know it works.** **Precedent lift**: split tasks by whether a trace above the
similarity threshold was retrieved, and compare steps-to-completion and failure rate across the
split. Prerequisite metric, checked first: **outcome-label coverage** — the fraction of stored traces
carrying a non-null success value. And a blunt one worth running before any of this: *does your
evaluation corpus contain traces at all?*

---

## 5. Cross-cutting properties

These separate a memory system from a store. None can be added later without rewriting the read path.

### 5.1 Provenance

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

### 5.2 Forgetting and decay

Forgetting is not a storage optimisation — see position 4 in [§3](#3-what-makes-a-memory-system-great).
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

### 5.3 Contradiction handling

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

### 5.4 Isolation

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

### 5.5 Temporal validity

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

### 5.6 Retrieval budget

Four consequences of position 1 in [§3](#3-what-makes-a-memory-system-great):

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
> That per-index separation is why the crowding in [§5.4](#54-isolation) is a *fact-channel* problem
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

## 6. Our coverage today

The honest table. Read the status column strictly. The **layer** column is the product vocabulary from
[§2](#2-two-vocabularies-three-memory-layers-six-memory-types), so a reader who arrived with those
words can find their way in.

| Type | Layer | BUILT | WIRED | MEASURED | One-line summary |
|---|---|---|---|---|---|
| **Semantic** | long-term | yes | yes | yes | Full pipeline: `Entity`/`Fact`/`Preference`, bitemporal, decay, owner isolation, supersession. |
| **Episodic** | short-term *and* long-term | yes | yes (default off) | **no** | Turns live in short-term and are never mined; assistant-originated content is admitted into long-term by `AssistantContentMode` (added 2026-08-10, default `Ignore`). No evaluation run has used a non-default mode. |
| **Procedural** | *none* (substrate in reasoning) | **no** | no | no | No concept in the domain. |
| **Prospective** | *none* (properties in long-term) | **no** | no | no | No first-class concept. `valid_from`/`valid_until` exist but the live read path ignores them; nothing is time-triggered. |
| **Meta-memory** | *none named* (substrate in long-term) | substrate only | partial | no | Confidence, decay, access tracking, read audit, trust levels exist. Memory cannot report what it does not know. |
| **Agent-episodic (traces)** | reasoning | yes | yes (defaults off in the MAF adapter) | **no** | Full graph + retrieval + budget, and **no automatic producer**. The evaluation corpus contains no traces. |

Two rows are worth reading twice. **Episodic** is the only type split across two layers, and the split
misroutes its own flagship question ([§2.3](#23-where-the-mapping-is-imperfect)). **Procedural**,
**prospective** and **meta-memory** have no product layer at all — which is precisely why this document
keeps a second vocabulary.

### 6.1 Semantic memory — BUILT, WIRED, MEASURED

**Layer:** long-term. The only type with end-to-end coverage.

- Node kinds: `Entity`, `Fact`, `Preference` —
  [`MemoryNodeKind.cs`](../src/AgentMemory.Abstractions/Domain/MemoryNodeKind.cs).
- Facts are subject–predicate–object with canonical `*_key` forms; the merge key is
  `{subject_key, predicate_key, object_key, owner_key}` on both the single and batch write paths, so
  a re-extracted triple collapses onto the existing node instead of creating a duplicate
  ([`FactQueries.Upsert`, `UpsertBatch`](../src/AgentMemory.Neo4j/Queries/FactQueries.cs)).
- Recall is vector search over `fact_embedding_idx` / `entity_embedding_idx` /
  `preference_embedding_idx`, owner-scoped, with the post-filter caveat of [§5.4](#54-isolation).
- Two optional completeness levers, both off by default and documented in place:
  `ExpandFactsByPredicate` (returns every fact sharing a top-K hit's canonical predicate, so an
  aggregation question is not silently answered from four of five matching facts) and
  `ResolveQueryRelations` (expands on the relations the query text itself names).
- Relation vocabulary is canonicalised: the measured graph holds `planned` (839 facts) and `plans`
  (14) as separate predicate keys, which is why matching is on `predicate_key` and never on raw text
  ([`MemoryRelationLexicon.cs`](../src/AgentMemory.Core/Memory/MemoryRelationLexicon.cs)).
- Per-phase cost is measured and reproducible — see [`performance/`](performance/README.md). Recall
  and ingestion are reported separately, never as a single "memory overhead" figure.

### 6.2 Episodic memory — BUILT, WIRED, **MEASURED** (default off)

> **Measured 2026-08-10.** Capture: `Utterance` added 3,048 relations, raising total facts 42%
> (25,668 → 36,489) with no cannibalisation of user-centric facts. Retrieval: **935 of 2,898 retrieved
> facts (32.3%) were episodic, across 33 of 50 questions** — and retrieval slightly *under*-selects
> them (32.3% retrieved vs 36.3% present), so the crowding comes from capture, not from a ranking
> bias. Cost: semantic facts retrieved fell ~29%, and answer prompts grew **+23.1% in tokens for only
> +3.8% more items**, because the retrieval budget is counted in *items* and an utterance is a wordier
> fact than a preference.
>
> **Accuracy did not move**, and LongMemEval structurally cannot show otherwise: it asks what the
> *user* said and did, so episodic recall can only ever be charged for and never rewarded. That is why
> the default stays `Ignore`. Verified reaching the model, not merely the context object — prompts
> grew 6,621 → 8,153 characters with `truncated = 0/50`.
>
> Marker check: `assistant` appears as a fact subject 13,251 times under `Utterance` and **17 times
> (0.07%) under `Ignore`**, so the signal is not an artefact of the counting rule.

**Layer:** short-term *and* long-term — the only type split across two, and the split misroutes its own
flagship question ([§2.3](#23-where-the-mapping-is-imperfect)).

Messages and conversations have always been stored in the short-term layer; what was missing was
*extraction from the assistant's turns*, and therefore any record of what the agent itself said or
proposed. That mechanism landed in the **long-term** layer, as ordinary `:Fact` rows.

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
  its effect on answer quality, on graph size, or on the fact-channel crowding in [§5.4](#54-isolation).
- Known hazard before enabling `Fact`: trust is stamped per extraction *request*, not per message
  ([§6.5](#65-meta-memory--substrate-only)), so model-generated claims would be written as `UserProvided`.

### 6.3 Procedural memory — **NOT BUILT**

**Layer:** none. Substrate sits inside the reasoning layer; the concept has no product name.

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

See [§8](#8-what-is-deliberately-not-built-and-what-would-trigger-building-it) for what promotion
would take and what would trigger it.

### 6.4 Prospective memory — **NOT BUILT**; substrate present, read path incomplete

**Layer:** none. The `valid_from`/`valid_until` properties live on long-term `Fact` rows; nothing else
does.

No first-class concept. `planned` is not a schema element — it is an emergent predicate produced by
extraction (839 facts in the measured graph). Nothing treats it differently from any other relation.

The substrate and its gap are covered in [§5.5](#55-temporal-validity): `valid_from`/`valid_until`
are real properties, written on both fact paths and writable through the public
`AddFactAsync(Fact, …)` surface, honoured by the as-of path, and **ignored by live recall**. No
extractor populates them, and no MCP tool or facade method exposes them.

Firing is absent by construction. There is exactly one hit for `IHostedService|BackgroundService|PeriodicTimer`
in `src/`, and it is a comment stating that the background enrichment queue deliberately uses a fixed
pool of worker tasks instead
([`BackgroundEnrichmentQueue.cs:19`](../src/AgentMemory.Core/Enrichment/BackgroundEnrichmentQueue.cs)).
**Nothing in this system is time-triggered. All recall is query-triggered.**

### 6.5 Meta-memory — SUBSTRATE ONLY

**Layer:** none named. The substrate is long-term-scoped; in the product vocabulary the pieces are
filed under "Memory Governance", which is a compliance heading for what is really calibration.

Everything needed to *build* meta-memory exists. The reporting layer does not.

**What is present:**

- `Fact.Confidence`; a six-level ordered `MemoryTrustLevel`
  (`Untrusted < UserProvided < ModelGenerated < ToolDerived < VerifiedExternal < ApplicationTrusted`).
- ACT-R-style retention scoring, computed identically in Cypher and C# ([§5.2](#52-forgetting-and-decay)).
- `access_count` / `last_accessed_at`, plus a `:MemoryReadAudit` row per recall hit
  ([`DecayQueries.cs`](../src/AgentMemory.Neo4j/Queries/DecayQueries.cs)).
- Lifecycle history (`IMemoryHistoryService`) with access count, read-audit count, invalidation time
  and supersession chain — for `Entity`/`Fact`/`Preference`
  ([`MemoryHistory.cs`](../src/AgentMemory.Abstractions/Domain/History/MemoryHistory.cs)).
- `MemoryContext.ResolvedQueryRelations` — the closest thing in the codebase to "did my vocabulary
  even contain this?"

**What is missing, precisely:**

- **Retrieval diagnostics now reach every section; the *summary* of them still does not.**
  `RecallOptions.IncludeDiagnostics` (default off) populates `RankedItems` for all five sections —
  messages, facts, entities, preferences and traces — on **both** recall paths, through the single
  `BuildRankedItems` join
  ([`MemoryContextAssembler.cs`](../src/AgentMemory.Core/Services/MemoryContextAssembler.cs)). The
  scores are the repositories' existing `(item, score)` tuples, recovered through the internal
  `IScoredLongTermSearch` / `IScoredTraceSearch` contracts, so no section costs a second query and the
  flag-off path is unchanged. Two gaps remain: `RecallResult` still has no per-section
  `TopScore`/`Count`, so a caller that does not walk `RankedItems` itself cannot see how thin a recall
  was; and facts arriving from predicate expansion have no comparable score and are deliberately
  absent from `RankedItems` rather than carrying a placeholder.
- **`RecallResult` cannot express thinness.** It carries `TotalItemsRetrieved` and `Truncated`. It
  cannot distinguish "0 facts because none exist" from "0 facts because `MinSimilarityScore = 0.7`
  excluded them" from "0 facts because owner post-filtering starved the top-K" — the measured case in
  [§5.4](#54-isolation). Three different failures, one indistinguishable output.
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

### 6.6 Agent-episodic (reasoning traces) — BUILT and WIRED, **UNMEASURED**

In the product vocabulary this is the **reasoning memory** layer
([§2.1](#21-the-three-layers-as-the-code-defines-them)). The graph layer is the most structurally
developed part of the system after semantic memory:

- Labels `ReasoningTrace` / `ReasoningStep` / `ToolCall` / `Tool`; edges `HAS_STEP`, `USES_TOOL`,
  `INSTANCE_OF`, `TOUCHED`, `HAS_TRACE`, `INITIATED_BY`, `TRIGGERED_BY`.
- `ReasoningTrace` carries `Task`, `TaskEmbedding`, `Outcome`, `Success (bool?)`, `OwnerId`, start and
  completion timestamps
  ([`ReasoningTrace.cs`](../src/AgentMemory.Abstractions/Domain/Reasoning/ReasoningTrace.cs)).
- Task text is auto-embedded on trace creation; recall is owner-scoped vector search over
  `task_embedding_idx` with the same over-fetch anti-starvation as facts, plus an as-of variant.
- Retrieval is budgeted (`RecallOptions.MaxTraces = 3`), delivered as `MemoryContext.SimilarTraces`,
  and rendered by the MAF adapter.
- `RecallOptions.SuccessfulTracesOnly` exists and **is** forwarded on *both* recall paths — live
  ([`MemoryContextAssembler.cs:267`](../src/AgentMemory.Core/Services/MemoryContextAssembler.cs)) and
  as-of (line ~424). The as-of path passed a hardcoded `null` until the two were reconciled; the same
  option now means the same thing whichever path runs, and the default is still `null` (no filter) on
  both. Other `RecallOptions` members *are* still live-path-only, by construction rather than by
  decision: `Intent` (the ranking override) and `ExpandFactsByPredicate` / `MaxExpandedFacts` /
  `ResolveQueryRelations` have no effect under `AssembleContextAsOfAsync`. `IncludeDiagnostics`
  (`RankedItems`) *is* honoured on both paths, for the four sections the as-of snapshot retrieves;
  its `RelevantMessages` stays empty because that path runs no semantic message search at all.
  `MaxRelevantMessages` and the GraphRAG blend are excluded there deliberately and say so in the
  source.

**What does not work, stated plainly:**

- **There is no automatic producer.** This is the difference that makes the three layers non-peers.
  Long-term memory is filled by the extraction pipeline with no application code; reasoning memory has
  no interceptor, no middleware and no pipeline hook. The only production callers of `StartTraceAsync`
  are `AgentTraceRecorder` and the MCP `memory_start_trace` tool, both of which an application author
  must invoke by hand. `MemoryService` never starts a trace.
- **No host can read a trajectory back.** `GetTraceWithStepsAsync` has **no caller in `src/`** — only
  the CLI evaluator, the TCK bridge and tests. `IReasoningMemoryService` exposes no tool-call read
  method at all. The MCP reasoning surface is write-only (`memory_start_trace`, `memory_record_step`,
  `memory_record_tool_call`, `memory_complete_trace`); the only read is the `similarTraces` array
  inside `memory_search` / `memory_get_context`, which returns traces **without** their steps. Even
  `GetTraceWithStepsAsync` returns steps without their tool calls.
- **Traces are orphan subgraphs in production.** `CreateInitiatedByRelationshipAsync` (trace→message),
  `CreateConversationTraceRelationshipsAsync` (`HAS_TRACE`/`IN_SESSION`) and
  `CreateTriggeredByRelationshipAsync` (tool-call→message) are all implemented and called by nothing
  outside the CLI evaluator and tests, as is `RecordTouchedEntitiesAsync` (`:TOUCHED`, the one edge
  that would tie reasoning to the knowledge graph). A live trace links to its conversation only by a
  `session_id` string property: you cannot traverse from a `:Conversation` to its reasoning.
- **Outcome capture was broken and is now fixed on the adapter — with a legacy path that still writes
  null.** `AgentTraceRecorder.CompleteTraceAsync` now has a `success` overload
  ([`AgentTraceRecorder.cs`](../src/AgentMemory.AgentFramework/AgentTraceRecorder.cs)), added as an
  overload rather than an optional parameter because the surface is locked under SemVer. The original
  three-argument form is retained for source compatibility and forwards `success: null`, so any host
  that has not migrated still stores unlabeled traces. Two consequences follow for those traces, and
  both are still live: the query facade prints `t.Success == true ? "✓" : "✗"`
  ([`MemoryQueryFacade.cs`](../src/AgentMemory.Core/Services/MemoryQueryFacade.cs)), so an unlabeled
  trace is shown to the model as a *failed* precedent; and `SuccessfulTracesOnly = true` excludes them
  entirely, because the predicate is `node.success = $successFilter` and in Cypher `null = true` is
  null.
- **Several recorded fields are unreachable from either host.** `ToolCall.DurationMs` and
  `ToolCall.Error` are not parameters of MCP `memory_record_tool_call` or of
  `AgentTraceRecorder.RecordToolCallAsync`, so `ToolCallStats.total_duration_ms` is always 0 for any
  host-recorded workload. `ReasoningTrace.Metadata` is not a parameter of either host's start-trace
  path. `ToolCall.Description` is never written to the `ToolCall` node at all — it is forwarded only to
  the `:Tool` aggregate, and `IReasoningMemoryService.RecordToolCallAsync` has no description
  parameter, so it is always null on create.
- **Step and tool-call timestamps are server-assigned.** `ReasoningQueries.AddStep` and
  `ToolCallQueries.Add` both hardcode `timestamp: datetime()`; a caller-supplied `TimestampUtc` is
  silently ignored. The domain types document this correctly.
- **`reasoning_step_embedding_idx` is provisioned and dead.** The index is created on every bootstrap,
  nothing populates step embeddings automatically, and no query reads it.
- **Traces are outside the maintenance machinery.** No access tracking, no `:MemoryReadAudit`, no
  decay, no history — `MemoryNodeKind` and `MemoryHistoryKind` both have exactly three members. A
  trace cannot be reinforced by use.
- **Deletion leaks `:ToolCall` nodes and inflates `:Tool` counters permanently.** See
  [§2.5](#25-two-defects-this-mapping-surfaced).
- **Retention evicts by age alone.** `PruneSessionTraces` orders by `started_at DESC` and deletes past
  `$keep`, driven by `ReasoningMemoryOptions.MaxTracesPerSession`. No confidence, no access count, no
  success. Any future promotion mechanism needs a matching exemption here or it is silently undone.
- **Off by default in the adapter, twice, and the recall is paid for anyway.**
  `AgentFrameworkOptions.PersistReasoningTraces = false` — with it off, `AgentTraceRecorder` returns
  synthetic in-memory objects and never contacts Neo4j. `ContextFormatOptions.IncludeReasoningTraces =
  false` — so a trace that *was* persisted is dropped before it reaches the model. Meanwhile the
  default recall policy returns `AutomaticRecallCategories.All`, which leaves `MaxTraces = 3` in place,
  so every MAF turn pays for a `task_embedding_idx` vector query whose result the renderer then
  discards. (`AutomaticRecallCategories.Default` deliberately excludes traces — but `Default` is not
  the default; `All` is.)
- **What is rendered is a task title, not a trajectory.** `MafTypeMapper` emits `t.Task` and nothing
  else: no outcome, no success flag, no steps, no tool calls. The richer `[✓|✗] task: outcome` form
  exists only in `MemoryQueryFacade`, which is an explicit tool call rather than automatic recall.
- **Two shipped samples report a persistence that does not happen.**
  `samples/AgentMemory.Sample.MinimalAgent/Program.cs` and
  `samples/AgentMemory.Sample.BlendedAgent/Program.cs` both record a trace and log
  `"Trace recorded successfully."` without ever setting `PersistReasoningTraces`, so nothing reaches
  Neo4j; their `catch` blocks, commented as expected when no live database is available, are
  unreachable on that path. This is a defect in shipped teaching material.
- **Not measured.** The evaluation corpus contains no reasoning traces. The corpus probe
  [`k6-trace-probe.json`](../artifacts/evaluation/k6-trace-probe.json) (2026-08-09) reports
  **traces: 0, steps: 0** against 10,382 entities and 14,621 messages, with `task_embedding_idx` and
  `reasoning_step_embedding_idx` both ONLINE — the zero is real emptiness, not index failure. The
  LongMemEval graph probe counts only `Entity`, `Fact`, `Preference` and their relationships
  ([`LongMemEvalGraphProbe.cs`](../tools/AgentMemory.LongMemEval/LongMemEvalGraphProbe.cs)). The perf
  harness seeds 8 traces but calls only `StartTraceAsync` + `CompleteTraceAsync`, so it creates **zero
  steps and zero tool calls** ([`PerfFixture.cs`](../tools/AgentMemory.Cli/Perf/PerfFixture.cs)).
  Task-similarity recall is therefore the only part of this layer any quality or performance run has
  ever exercised; step persistence, tool-call persistence, step retrieval, tool-call retrieval and tool
  stats are covered by live-Neo4j integration tests and by nothing else.

---

## 7. Comparison with upstream `neo4j-labs/agent-memory`

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

**Vocabulary note, corrected.** An earlier version of this document said upstream organises around
three layers "rather than the six-type taxonomy used in this document," which was inaccurate by
omission: **the three-layer framing is this project's published vocabulary too** — the README, the
architecture doc and public write-ups all lead with it, and it is the public API surface. The
difference is not upstream-versus-us; it is product vocabulary versus analysis vocabulary, and
[§2](#2-two-vocabularies-three-memory-layers-six-memory-types) now maps them onto each other. The
three names *originate* upstream: `neo4j-labs/agent-memory` groups its package into `short_term.py`,
`long_term.py` and `reasoning.py`, and its README presents them as three columns whose captions
("Conversations & messages", "Entities, preferences, facts / POLE+O", "Reasoning traces & tool usage /
Similar task retrieval") are the source of the wording used in this project's own material.

Upstream maps its three layers onto cognitive terms in one page of its own documentation, calling
short-term the agent's *working memory*, long-term its *semantic memory*, and reasoning its *episodic
memory for problem-solving*. Its reasoning layer is also described as holding *procedural knowledge*;
the layer was originally named procedural and the `ProceduralMemory` alias for `ReasoningMemory`
survives. That double-labelling is worth knowing before comparing vocabularies: **the word "procedural"
upstream refers to the trace layer, not to stored reusable skills.** Neither implementation has
procedural memory in the "stored, retrievable, parameterised skill" sense.

### Type-by-type

Both projects use the same three layer names, so the layer column applies to both sides.

| Type | Layer | Upstream | Ours |
|---|---|---|---|
| Semantic | long-term | `Entity`/`Fact`/`Preference` + `RELATED_TO`; fixed relation vocabulary | Same labels; canonicalised predicate keys; facts included in assembled context |
| Episodic (messages) | short-term | `Conversation`/`Message`, extraction not gated by role | Same labels; extraction gated by `AssistantContentMode`, default `Ignore` |
| Procedural | *none* | absent as stored skills (the name is applied to traces) | absent |
| Prospective | *none* | absent | absent |
| Meta-memory | *none named* | confidence, provenance via `:Extractor`, review status on dedup candidates | plus decay, access tracking, `:MemoryReadAudit`, `MemoryTrustLevel` |
| Reasoning traces | reasoning | first-class; similar-trace search defaults to successful-only | first-class; `SuccessfulTracesOnly` defaults to *no filter*; **no automatic producer** |

Two entity-model divergences sit **outside the parity verifier's scope**, which gates labels,
relationship types and property *names* — not dynamic label casing or type/subtype pairing. Both are
locked in on our side by passing tests, which is what makes them worth writing down.

- **Label casing.** Upstream's `graph/query_builder.py` routes every dynamic label through a
  `to_pascal_case` helper and emits `:Person`; we call `.ToUpperInvariant()` and emit `:PERSON`,
  asserted by `SchemaParityP1Tests.BuildDynamicLabels_ValidType_ReturnsUppercaseLabel`. A Cypher
  `MATCH (:Person)` written against upstream will not match our nodes. **Caveat on this one:** the
  upstream source read here is a v0.1.0 checkout, while the committed parity snapshot describes
  v0.5.0 and states upstream writes uppercase. The snapshot's own confidence note
  (`strategy/reference/schema-parity-assessment.md`) rates its non-DDL sections MEDIUM because
  `graph/queries.py` was summarised rather than read line by line, and the uppercase claim matches an
  upstream *docstring* rather than upstream's implementation. **This is unresolved for v0.5.0 and
  needs a v0.5.0 checkout to settle.**
- **Subtype validation.** Upstream validates against a **per-type dictionary** (`VALID_SUBTYPES`,
  keyed by parent type) and rejects a subtype that does not belong to its parent; ours is a flat
  21-name set, so an entity typed `PERSON` with subtype `CITY` receives both labels. Our set is also
  materially smaller than the POLE+O catalogue we ship in `DefaultSchemas`: of PERSON's six declared
  subtypes only `INDIVIDUAL` becomes a label. Conversely, upstream lets a *custom* type become a
  label and ours drops it (`BuildDynamicLabels_UnknownType_ReturnsEmptyList`).

### Where we deliberately diverge

Each item below is an entry in the parity policy or a documented decision in code, not an informal
claim.

1. **Multi-tenancy.** We scope reads and writes by a scalar `owner_id` (plus `owner_key`), indexed on
   `Fact`/`Entity`/`Preference`/`ReasoningTrace` and on the `RELATED_TO` edge. Upstream's schema has a
   `:User` label; our parity policy lists it under `UpstreamOnlyLabels` with the note ".NET scopes via
   the `owner_id` property". Our own limitation is separate and stated in [§5.4](#54-isolation): the
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
   [§6.6](#66-agent-episodic-reasoning-traces--built-and-wired-unmeasured), upstream's default is the safer one for a host to
   adopt today, and switching ours is gated on fixing outcome capture first, not on a preference.
7. **A set of interop-critical property names that must never drift.** `id`, `name`, `type`,
   `embedding`, `confidence`, `subject`/`predicate`/`object`, `valid_from`/`valid_until`,
   `task`/`task_embedding`, `thought`/`action`/`observation`, `tool_name`/`status`/`duration_ms` and
   others are pinned by the parity policy: a rename on either side is a build failure, which is exactly
   how a silent divergence gets caught.

---

## 8. What is deliberately not built, and what would trigger building it

Nothing in this section is scheduled. Each entry states what exists, what is missing, and the concrete
signal that would justify the work.

### 8.1 Give the reasoning layer a producer and a read path

**Done since this entry was written:** the outcome gap. `AgentTraceRecorder.CompleteTraceAsync` now has
a `success` overload — an overload rather than an added optional parameter, because the method is
public and the API surface is locked under SemVer. The three-argument form is retained for source
compatibility and still forwards `success: null`, so hosts must migrate to get labelled traces.

**Still missing, and it is the larger half** ([§6.6](#66-agent-episodic-reasoning-traces--built-and-wired-unmeasured)):

- **A producer.** No interceptor, middleware or pipeline hook starts a trace. Every trace in existence
  requires hand-written application code. This is why the corpus holds zero of them and why the layer
  is not a peer of the other two.
- **A host-side read path.** Neither MCP nor the MAF adapter can read back a trace's steps or its tool
  calls; `GetTraceWithStepsAsync` has no caller in `src/`, and no service method returns tool calls at
  all. A trajectory that can be written and not read is not yet memory.
- **The graph edges.** `INITIATED_BY`, `HAS_TRACE`/`IN_SESSION`, `TRIGGERED_BY` and `:TOUCHED` are all
  implemented and never called in production, so traces are orphan subgraphs joined to their
  conversation only by a string property.

**Cheapest first increments, in order:** stop paying for a discarded trace query on every MAF turn
(either default `IncludeReasoningTraces` on or exclude traces from the default recall categories); fix
the two samples that log a persistence they do not perform; delete `:ToolCall` nodes in
`DeleteBySession` and `PruneSessionTraces` ([§2.5](#25-two-defects-this-mapping-surfaced)).

**Trigger:** any host enabling `PersistReasoningTraces`. The sample defect and the tool-call leak are
defects, not feature requests.

### 8.2 Procedural memory as trace promotion

**Exists:** the representation, the retrieval, the delivery path, a tool-reliability prior, a detection
hook, and a free second vector channel — all enumerated in [§6.3](#63-procedural-memory--not-built).

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
([§4.3](#43-procedural-memory)). Conversational QA cannot measure this — the workload is not in the
corpus. Building the harness is part of the cost, and should be honestly counted as such.

### 8.3 Prospective memory as a valid-time gate

**Exists:** the properties, the write path, and the exact filter expression — on the as-of path.

**Missing:** the same two clauses on the live path, and any writer that populates the fields.

**Cost:** the read gate is two conditional `AND`s in `FactQueries.SearchByVector`, copied from
`TemporalQueries.SearchFactsAsOf`. Because no extractor writes valid-time and the only rows carrying
`valid_until` are already excluded by the transaction-clock filter, **turning the gate on changes the
result set for zero currently-existing rows** — which makes it safe to ship and impossible to measure
on its own. Real measurement needs the extraction side too, and that changes the graph.

**Explicitly out of scope:** firing. That is a new hosting component with delivery guarantees and
retry semantics, and it belongs to the orchestrator unless there is a specific reason it cannot
([§4.4](#44-prospective-memory)).

**Trigger:** either (a) a correctness complaint — an expired fact returned forever is a live bug the
gate fixes — or (b) enough date-bearing questions in an evaluation corpus for the arm to detect an
effect. Count them before spending a rebuild.

### 8.4 Meta-memory that reports its own sufficiency

**Exists, and is discarded:** the pre-owner-filter candidate count; vocabulary coverage
([§6.5](#65-meta-memory--substrate-only)). Per-item scores on the other four sections were in this
list and no longer are — see below.

**First increment — done.** `RankedItems` is populated on the fact, entity, preference and trace
sections (alongside messages) under the existing `IncludeDiagnostics` toggle, reusing the one
`BuildRankedItems` join, on both the live and the as-of recall path. No schema change, no extra query,
no extra round trip; unchanged when the flag is off. **Still outstanding from this increment:** the
derived per-section `TopScore`/`Count` on `RecallResult`.

**Why it ranks first on usefulness:** it is the instrument. Without it, the effect of every other
change on the measured 7-of-60 in [§5.4](#54-isolation) is unobservable.

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

### 8.5 Message-level provenance

**Missing:** per-message (ideally per-offset) `EXTRACTED_FROM` resolution ([§5.1](#51-provenance)).

**Why it is not cosmetic:** it gates the honest evaluation of extraction quality, and it gates any
salience signal derived from how often something was genuinely mentioned. A reinforcement signal
derived from our own retrievals instead would measure the ranker, not the world.

**Trigger:** the first time an extraction-quality metric needs to be able to fail.

### 8.6 Isolation pushed into the index

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
