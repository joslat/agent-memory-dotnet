# The time model: what was true, and what was known

AgentMemory keeps two clocks on a fact, and answers questions on either.

| Clock | Question it answers | Stored as |
|---|---|---|
| **Valid time** | When was it true in the world? "Where did I live in 2020?" | `valid_from`, `valid_until` (and `occurred_on` for an event) |
| **Transaction time** | When did memory know it? "What did the record say on 1 March?" | `created_at`, `invalidated_at` |

Live recall reads the present on both clocks: what is believed now. A past question reads one moment on each.

## How a value is closed

When a new value replaces an old one (supersession, `ExtractionOptions.SupersedeReplacedFacts`), the old fact is closed,
never deleted: it gets `invalidated_at` and a `SUPERSEDED_BY` edge to its replacement. How the closing is recorded
depends on `ExtractionOptions.BitemporalChanges` (off by default):

| | `BitemporalChanges` off (default) | `BitemporalChanges` on |
|---|---|---|
| **A change** ("I moved to Madrid in March") | A retraction: belief withdrawn, valid time ended when it was written. A past question read with today's belief loses the old value. | Valid time ends when the new value began (its stated start, else when it was said), and the old value **stays believed** for its time: "Where did I live in 2020?" still answers. When that end was learned is recorded (`valid_until_recorded_at`), so belief before it sees the old value open. |
| **A correction** ("Sorry, I meant Madrid, not Bilbao"; or a new value for the same period) | The same retraction. | **Belief withdrawn**, valid time left alone: the old value was never right. A correction of a value a change had already made history withdraws it too; saying the change again does not. |
| **An undated new value** | Has no start, so it counts as valid at every past moment. | Starts when it was said (`valid_from_inferred`). |
| **Why it closed** | Not recorded. | `invalidated_reason`: `change` or `correction` (decay writes `decay`). Read in the history (`MemoryHistoryRecord.ClosedAs`, MCP `memory_lineage`). |

Live recall is the same either way: a closed fact is not recalled now. The difference is only in past questions.

**Stores written before keep their closings.** Facts closed before `BitemporalChanges` was turned on carry no reason and
read exactly as before (retractions). Turning the option on changes only the closings written from then on.

## Asking about the past

- **`RecallAsOfAsync(request, validAsOf, systemAsOf)`**: an explicit moment on each clock. Recent and relevant
  messages of the session are kept to those said by `systemAsOf`.
- **A date in the question** (`MemoryOptions.ResolveTemporalQueries`, on in the Conversational preset): "Where did I
  live 6 years ago?" is recalled as of that time, with today's belief (`TemporalQueryClocks` chooses).
- **An owner's memory as known at a moment** (`MemoryHistoryQuery.AsOf`, `agentmemory history --as-of`): every row with
  its window, why it closed and what replaced it.
- **One clock for everything** (`IClock`): every time the memory path writes or compares comes from it, so a host or a
  replay sets "now" once.

## What reads valid time today

Live recall does **not** filter by valid time by default (`RecallOptions.ValidTime = Ignore`, also in the Conversational
preset): a fact past its window is still recalled now. `ValidTime = Current` changes that, and reminders
(`ProspectiveFiring`) need it. Changing the preset's default is an open decision.

## What is not modelled

- **Preferences and entities** have transaction time only (a changed taste is closed; when it was true is not kept).
- **Relationship edges** close on valid time only.
- **Closing** acts on the reviewed single-valued relations ("lives in", "works at", …). A value stated under another
  phrasing is not closed until extraction maps it onto one.

## The NAMS backend

A store on Neo4j Agent Memory as a Service has neither as-of reads nor supersession: nothing on this page applies to it.
