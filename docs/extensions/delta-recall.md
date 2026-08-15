# `delta-recall`

**What changed since I last looked?**

An agent resuming work re-receives everything it already processed. Full recall re-assembles the same
facts at every session start, and there was no way to ask for the difference. Every ingredient already
existed and was already enforced on the live write path — `created_at` stamped on create only,
`invalidated_at` stamped idempotently, `SUPERSEDED_BY` edges, `valid_from`/`valid_until` — and nothing
read them as a diff.

This extension makes those clocks *seekable*. It adds nothing to remember.

## Shape

**No labels. No relationship types. No properties.** The extension declares seven RANGE indexes and
nothing else:

| Migration | Contents |
|---|---|
| `0001_clock_indexes.cypher` | `fact_created_at_idx`, `fact_invalidated_at_idx`, `fact_valid_from_idx`, `fact_valid_until_idx`, `preference_created_at_idx`, `preference_invalidated_at_idx`, `entity_created_at_idx` |

The checkpoint is a **caller-held token**, not a stored node — which is why there is no
`:MemoryCheckpoint` label here. A stored checkpoint would be a real parity-allowlist entry for a need no
host has yet. The API shape (`Since` on the request, `TakenAtUtc` on the response) is deliberately
designed so a stored-checkpoint phase can be added later without changing it.

### Why these indexes are seekable

`invalidated_at IS NULL` is famously *un*indexable in Neo4j, and a reader who knows that will expect
these indexes to be dead weight. They are not, and the reason is the same fact seen from the other side:
a Neo4j range index stores no nulls, which is exactly why a `NULL` check cannot use one. The delta
predicates are the opposite shape — range predicates over **non-null** values (`invalidated_at > $since`)
— which a range index serves directly. The owner clause's `owner_id IS NULL` disjunct does not
disqualify the plan, because the time range supplies the seek.

## Cypher

Five fact queries, two preference queries, one entity query. The window is **half-open on both ends** —
strictly `> $since`, inclusively `<= $until` — everywhere, without exception. That is what makes
consecutive deltas partition time exactly, so every change appears **exactly once by construction**
rather than by hope.

```cypher
// New: the transaction clock, never valid time, never updated_at.
MATCH (f:Fact)
WHERE f.created_at > datetime($since) AND f.created_at <= datetime($until)
  AND f.invalidated_at IS NULL
RETURN f ORDER BY f.created_at ASC LIMIT $limit

// Superseded: paired old -> new, so "updated" reads as an update and not as a deletion plus a creation.
MATCH (old:Fact)-[:SUPERSEDED_BY]->(new:Fact)
WHERE old.invalidated_at > datetime($since) AND old.invalidated_at <= datetime($until)
RETURN old, new ORDER BY old.invalidated_at ASC LIMIT $limit

// Expired validity: real-world validity closed, still live on the transaction clock.
MATCH (f:Fact)
WHERE f.valid_until IS NOT NULL
  AND f.valid_until > datetime($since) AND f.valid_until <= datetime($until)
  AND f.invalidated_at IS NULL          // <-- the exactly-once gate; see below
RETURN f ORDER BY f.valid_until ASC LIMIT $limit
```

### The `invalidated_at IS NULL` on the expiry query

This is the single most consequential line in the extension and the easiest to delete during a cleanup
pass. Supersession stamps **both** clocks — `invalidated_at` *and* `valid_until`. Without this gate a
superseded fact appears as a superseded pair **and** as an expiry, in the same delta, and the
exactly-once invariant the whole feature rests on becomes quietly false while every test that checks
"is it present?" keeps passing.

There is a dedicated integration test named after this exact failure
(`ASupersededFactAppearsONLYAsAPairAndNotAlsoAsExpiredValidity`), and it has been verified to fail — and
to be the *only* failure — when the gate is removed.

## Semantics

Eight buckets, disjoint by construction:

| Bucket | Meaning | Clock |
|---|---|---|
| `NewFacts` | newly known | `created_at` in window |
| `SupersededPairs` | replaced, old → new | `invalidated_at` in window, successor exists |
| `InvalidatedFacts` | retracted, no successor | `invalidated_at` in window |
| `ExpiredValidity` | stopped being true | `valid_until` in window, still live |
| `NewlyDueProspective` | became true, known before | `valid_from` in window, `created_at` before it |
| `NewPreferences` | newly known | `created_at` in window |
| `SupersededPreferences` | replaced, old → new | `invalidated_at` in window |
| `NewEntities` | newly known | `created_at` in window |

**Never `updated_at`.** Every restatement bumps it, so an `updated_at`-based novelty rule replays
restatements as "new" forever.

**A fact both created and becoming due inside the window is reported as new only.** "New" is the more
informative of the two, and reporting both would double-count.

**Truncation is reported, never silent.** Each bucket is capped (`MaxItemsPerSection`, default 20) and a
capped bucket is named in `TruncatedSections` *and* in the rendered text. A caller told nothing would
reasonably believe they had seen every change.

**A future or present checkpoint throws.** Returning "nothing changed" for a nonsensical window would be
a reassuring fabrication, which is the failure mode this project treats as worse than an error.

**The upper bound is read from the clock once** and handed back as `TakenAtUtc`. A write landing during
the read with `created_at > until` falls into the *next* delta rather than being lost to read skew.

### Owner isolation

`RecallChangedSinceAsync` resolves its scope through `IMemoryIsolationPolicy`, exactly as recall does.
A delta reads the repositories directly — the assembler, which does this for every other read, is not in
the path — so it must resolve its own scope, and passing a caller's `Scope` straight through would hand
a caller who supplied only a `UserId` an unfiltered cross-owner answer.

### Rendering

The block renders through the same admission check and the same `<recalled_memory>` delimiter as every
other recalled category. A delta is recalled memory, not a system announcement; rendering it with more
authority than a recalled fact would grant extraction output a promotion it has not earned. A superseded
pair is admitted at the **lower** of its two items' trust levels, because the rendered line contains
both.

The block contains no `->` arrows, contrary to what an earlier draft of the design showed: the delimiter
escapes every angle bracket in its content — that is how a recalled item is stopped from forging its own
closing tag — so an arrow would reach the model as `-&gt;`.

## Conformance

**TCK: Gold-safe with the extension ON.** Two independent reasons, either sufficient:

1. A RANGE index changes query *plans*, never query *results*. Nothing a conformance run can observe
   distinguishes an indexed graph from an unindexed one.
2. The new repository members are called by no bridge endpoint. The TCK exercises the upstream-parity
   surface; delta recall is not on it.

## Parity delta

**Empty** (`SchemaParityDelta.Empty`).

An index is invisible to the parity verifier by construction: the verifier compares labels,
relationship types and properties, and this extension declares none of the three. There is nothing to
allowlist because nothing diverges — which is the strongest form this section can take, and worth
stating explicitly rather than leaving as an absence.

## Host wiring

Off by default, and the off state is byte-identical: no query, no state-bag read, no message.

| Option | Default | Meaning |
|---|---|---|
| `InjectDeltaOnSessionResume` | `false` | master flag |
| `DefaultDeltaCheckpointKey` | `"memory_delta_checkpoint"` | state-bag key |
| `MinimumDeltaGap` | 30 minutes | how stale a checkpoint must be to count as a resume |
| `MaxDeltaItemsPerSection` | `20` | per-bucket cap |

| Situation | Signal | Behaviour |
|---|---|---|
| Brand-new session | no checkpoint | full recall only; checkpoint stamped after the turn |
| Resume | checkpoint older than `MinimumDeltaGap` | delta injected **plus** normal recall |
| Mid-session turn | checkpoint younger than the gap | normal recall only; checkpoint still advances |

The gap heuristic is deliberate. There is no session lifecycle in this system — a session is a string —
so "resume" cannot be detected from a close event that does not exist. An age threshold is
deterministic, needs no state beyond the token, and is wrong only in the benign direction: a long pause
inside one sitting yields a small, accurate delta.

**Advancing the checkpoint is an acknowledgement, not a read receipt.** It advances after a turn
completes successfully, never at the moment the delta is fetched, and it advances to the delta's own
`TakenAtUtc` rather than to "now" — the interval between reading the delta and finishing the turn was
never reported to the agent, and that interval contains a model call. A turn that threw advances
nothing, so its delta is replayed. Replaying a change set costs tokens; losing one loses knowledge.

## Rejected alternatives

- **Diff of two full recalls.** Non-deterministic under top-K, and double the cost.
- **A `:MemoryReadAudit`-derived checkpoint.** Conflates *read* with *acknowledged*, advances per
  recall, and is hits-only.
- **A stored `:MemoryCheckpoint` label now.** Real parity cost for a need no host has yet.
- **`updated_at`-based novelty.** Restatements replay forever.

## Known gaps

- A fact invalidated *before* the window and revived *inside* it appears in no bucket. Accepted for v1;
  a `ReassertedFacts` bucket is the candidate fix if fixture data shows it matters.
- Reasoning traces and promoted procedures are not in the delta ("a new procedure is available since you
  last ran" is attractive), blocked on traces having no invalidation semantics.
- No MCP surface yet. An MCP host would hold the token itself; deferred until the MAF path is measured.
