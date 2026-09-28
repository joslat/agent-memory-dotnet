# Memory options: dates, changes of mind, shared knowledge, relationships

The switches that decide what an agent sees about *when* something was true, whether it is still
true, whose it is, and how the people in it relate. Each was found and checked in simulated
conversations. Dates in the prompt and the shared-knowledge budget are on by default. Relationships
in recall are opt-in, and so are the switches that change what the extractor is asked to write,
because their effect depends on the model.

## Where each option lives

- `MemoryOptions` and its nested `Recall`, `Extraction` and `WorkingMemory`: the `configureMemory`
  lambda of `AddNeo4jAgentMemory`, or a `new MemoryOptions { ... }` (`Recall` is init-only, so set it
  with `RecallOptions.Default with { ... }`).
- `LlmExtractionOptions`: the `configureLlm` lambda of the meta-package `AddNeo4jAgentMemory`.
- `ContextFormatOptions`: `AgentFrameworkOptions.ContextFormat` in `AddAgentMemoryFramework`.
- `MemoryContextFormatterOptions` (Core) and `MemoryRecallSecurityOptions` (Semantic Kernel): the
  renderer of each surface.

## The switches

| Option | Default | What it does |
|---|---|---|
| `ContextFormatOptions.IncludeDates` | `true` | Agent Framework renderer: a fact's dates at the precision they were stated (`(since 2024-03)`, `(until 2027-06)`, `(2024 to 2027)`), an event's day (`(on 2026-09-26)`), and the day of a recalled turn from another session (`[2026-09-20] …`). `false` renders without dates. |
| `MemoryContextFormatterOptions.IncludeDates` | `true` | The same rule in the Core formatter. |
| `MemoryRecallSecurityOptions.IncludeDates` | `true` | The same rule in the Semantic Kernel adapter. |
| `MemoryOptions.WorkingMemory.IncludeDates` | `true` | The same rule in the profile block ("about this user"). |
| `MemoryOptions.SharedRecallBudget` | `3` | Shared (owner-less) entities, facts and preferences get their own recall budget, searched separately from the owner's own rows, and render under a "shared knowledge, not about the user" label. `0` recalls no shared items; `null` keeps one budget for both and renders as before. |
| `RecallOptions.MaxRelationships` | `0` | How many live relationships touching the recalled entities to include, rendered `Rosa — best friend → Carmen`. `0` reads none. Live recall only. |
| `LlmExtractionOptions.TemporalValidity` | `TemporalValidityMode.Ignore` | `Extract` asks the model for validity dates where the conversation states them, at the precision stated, and for the day a one-off event happened (`Fact.OccurredOn`); each turn reaches the extractor with its timestamp. |
| `ExtractionOptions.SupersedeReplacedFacts` | `false` | A new value of a single-valued relation closes the one it replaces: every present-state form ("works for" replaces "works at"), a stated age, `favourite <thing>`, an entailed state ("moved to" a place → "lives in"), relationship edges. Only values that hold now replace or are replaced. |
| `LlmExtractionOptions.MarkCorrections` | `false` | The extractor marks what a correction replaces ("actually Arcade Fire, not Radiohead"); with `SupersedeReplacedFacts`, the write closes the fact or preference it names. |
| `LlmExtractionOptions.OwnPreferencesOnly` | `false` | A preference is only the user's own stated taste; someone else's taste is a fact about them, and a request is not a preference. |
| `ExtractionOptions.ResolveUserToName` | `true` | Once the user's name is known, what they say about themselves is stored under that name, and no entity called "user" is created beside the person. |
| `LlmExtractionOptions.CaptureUserName` | `true` | Asks the extractor for a `user \| is named \| <name>` fact when the user states their name; `ResolveUserToName` reads it. |
| `LlmExtractionOptions.IgnoreQuestions` | `true` | A question states nothing, including what it takes for granted ("When did I move to Lyon?" is not stored as a move). |
| `MemoryOptions.WorkingMemory.RecentTopicsDays` | `0` | "Lately": the profile block ends with one line naming what the person talked about most in the last this-many days (`Lately (7 days): marathon (5 mentions), Ana (2 mentions)`), counted by the facts extracted from what the person said; never the person themselves. `MaxRecentTopics` (3) and `MinRecentTopicMentions` (2) shape it. `0` leaves the block as it was. |
| `AgentFrameworkOptions.DeferQuestionTurns` | `false` | A turn in which the user only asks is not extracted on its own: it waits, marked on its stored message for its owner, and is extracted with the owner's next turn that tells something, in any session (or once `ExtractionOptions.MaxDeferredTurns`, 3, wait). Nothing is lost; asking to be reminded or to have something remembered is never held. Other hosts set `ExtractionRequest.DeferIfOnlyAsking`. |

Behaviour with no switch:

- A shared write (`ExtractionRequest.ShareWithEveryone`) stores no preferences: shared knowledge has
  no user, so nothing it states is the user's taste. Its facts are kept.
- An object the model repeated at the end of its predicate is stored once ("is a chef | chef" becomes
  "is a | chef").
- Entities carry `owner_key` (`"*"` when shared), so entity resolution seeks shared candidates by
  index; existing stores are backfilled at bootstrap. See [schema.md](../schema.md).

## Example

```csharp
builder.Services.AddNeo4jAgentMemory(
    configureMemory: memory =>
    {
        memory.Extraction.SupersedeReplacedFacts = true; // changes of mind close the old value
    },
    configureNeo4j: neo4j => { /* ... */ },
    configureLlm: llm =>
    {
        llm.TemporalValidity   = TemporalValidityMode.Extract; // dates and event days
        llm.MarkCorrections    = true;
        llm.OwnPreferencesOnly = true;
    });
```

To restore the earlier prompt, set `IncludeDates = false` on the renderer you use and
`SharedRecallBudget = null`.

## Known limits

- A fact has one validity window: a value said again after it was replaced keeps its earlier end.
- A plan that begins does not yet hand over from the value it was meant to replace.
- Relationships are read on live recall only; as-of recall does not reconstruct them.

More detail: supersession in [architecture.md §3.2.7](../architecture.md#327-valid-time-recall-and-prospective-memory-gating),
per-memory-type behaviour in [memory-map.md](../memory-map.md).
