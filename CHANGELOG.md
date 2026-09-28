# Changelog

All notable changes to this project will be documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **A corrected name renames what it named** (`ExtractionOptions.RenameOnCorrectedName`, off by default; needs
  `SupersedeReplacedFacts`). "It's Priya, not Pruya" used to close the old naming fact but leave "Pruya" as a second
  person, with what was said under that name still attached. Now, when a persist closes a naming fact (`is named`,
  `is called`, any subject: the user, a person, a pet) in favour of a new name, the owner's entity with the old name is
  merged into the one with the new name (created if missing): relationships move, the old name stays as an alias so it
  is still recognised, and the old entity is closed. The live facts said about the old name are restated under the new
  one, each superseding its original. Facts that mention the old name as their object are not rewritten.

- **The people a recalled fact names bring their relationships** (with `RecallOptions.MaxRelationships` set). Recall
  already added the relationships between the recalled entities; now also those of the entities the recalled facts
  name, by name or alias, in the same single query. "What does my manager's husband do?" gets "Priya Nair — married
  to → Daniel" beside "Daniel works as chef", even when neither person was recalled by itself.
  `ILongTermMemoryService.GetRelationshipsAroundAsync` / `IRelationshipRepository.GetLiveAroundAsync` (defaults fall
  back to the id-only reads).

- **A held question turn says so.** `ExtractionResult.Deferred` (and the public `ExtractionResult.DeferredMetadataKey`)
  tell a host that the turn was held for the next one that tells something, rather than extracted with nothing in it;
  the `memory.ingestion.operations` counter tags it `status=Deferred`, apart from successes.

- **A turn in which the user only asks waits for the next turn that tells something**
  (`AgentFrameworkOptions.DeferQuestionTurns`, off by default; `ExtractionRequest.DeferIfOnlyAsking` for other hosts;
  `ExtractionOptions.MaxDeferredTurns` 3). In simulated conversations 43 % of extraction calls returned nothing, almost
  all on question turns. The waiting turn is marked on its stored message (`IMessageRepository.SetExtractionDeferredAsync`
  / `GetExtractionDeferredAsync`, default members: a store without them extracts every turn at once), held for its
  owner (the session when there is none), so it survives a restart, stays in its store, is never released into
  another owner's extraction, is gone when forgotten, and is extracted with the owner's next telling turn in any
  session (or once the maximum waits); the marks clear only when that extraction succeeds, and a store that fails
  while deferring extracts the turn at once. Asking to be
  reminded or to have something remembered is never held. The extraction context window now reaches up to the last
  target, so what the agent said between a waiting question and the turn that released it resolves references.

- **"Lately": what the person has talked about most recently** (`WorkingMemoryOptions.RecentTopicsDays`, off by default;
  `MaxRecentTopics` 3, `MinRecentTopicMentions` 2). The profile block ends with one line, `Lately (7 days): marathon
  (5 mentions), Ana (2 mentions)`: entities of the owner named by live facts, counted by the distinct facts extracted
  in the window from what the person said (live user messages), never the person themselves. What the agent said or
  recalled does not count; ties break by name so the block stays byte-stable, and it is rebuilt at least daily so the
  window slides. One read per rebuild, only when enabled.

- **`SupersededFact.ValidUntilPrecision`** (and `EffectiveDatePrecision`), so a predecessor's end prints at the
  precision it was stated.

- **`RecallOptions.MaxRelationships`: how the recalled people and things relate reaches the agent.** The live
  relationships touching the recalled entities are read in one query (`IRelationshipRepository.GetLiveAmongAsync`,
  `ILongTermMemoryService.GetRelationshipsAmongAsync`, both names included, as `RecalledRelationship`) and rendered
  by both renderers as `Rosa — best friend → Carmen` (`MemoryContext.RelevantRelationships`; shared ones labelled like
  every shared item). Relationships had no section in the recalled context at all, so "Carmen is my best friend",
  stored as a relationship, never reached the agent ("is she a colleague or a friend?"). 0 (the default) reads none.
  Live recall only: an edge has no transaction clock to reconstruct a past instant with.
- **`IRelationshipRepository.EndAsync`**: ends a relationship by setting its `valid_until` (the first end is kept;
  the edge stays for history). New members have default implementations that throw `NotSupportedException`.

- **`LlmExtractionOptions.MarkCorrections`: a correction closes what it replaces.** Every extractor is asked to mark
  what a correction replaces ("actually Arcade Fire, not Radiohead" adds `"replaces": "Radiohead"`;
  `ExtractedFact.Replaces` / `ExtractedPreference.Replaces`), and with `SupersedeReplacedFacts` the write closes the
  live fact or preference that stated it: a fact of the same subject whose object is the replaced value, or states
  the same relation and contains it as whole words; a preference of the same category that names it. Preferences had
  no supersession at all, and a plan ("the full marathon instead of the half") has no single-valued relation, so both
  values stayed live. Off (the default) every prompt is byte-for-byte what it was.
- **`Preference.InvalidatedAtUtc`**, projected as it is on `Fact`, so a caller can tell a superseded preference from a
  live one.

- **An event carries the day it happened (`Fact.OccurredOn` / `OccurredOnPrecision`, stored as `occurred_on` /
  `occurred_on_precision`).** Under `TemporalValidityMode.Extract` the extractor is asked to give a one-off event that
  already happened ("yesterday I went hiking") the date it happened instead of a validity period, and every renderer
  reads it "(on 2026-09-26)". Before, the model wrote the day as `valid_from` alone, which read "since", or as
  `valid_from` = `valid_until`, which dropped the event from the profile block the day after. An event keeps no
  validity of its own; told again without a date it keeps its day. "Moved to" a place entails living there since the
  move's day. As-of recall, derivation (ordering, durations), the conflict block, the MCP projection and
  `MemoryHistory.OccurredOnUtc` read it; two events on different days are never merged as one statement.

- **Dates reach the prompt, at the precision they were stated.** A fact's validity dates now keep how precisely
  they were written (`Fact.ValidFromPrecision` / `ValidUntilPrecision`, the new `DatePrecision` enum, stored as
  `valid_from_precision` / `valid_until_precision` on every fact write path); the extractor records it, because only
  the parser can tell "2024-03" from "2024-03-01". With the new `IncludeDates` switch
  (`ContextFormatOptions` / `AgentFrameworkOptions.ContextFormat`, and `WorkingMemoryOptions` for the profile block)
  relevant facts render their dates by one rule (the conflict and supersession blocks too; Semantic Kernel through
  `MemoryRecallSecurityOptions.IncludeDates`) (`Rosa moved to Lyon (since 2024-03)`, `(until 2027-06)`,
  `(2024 to 2027)`, `(on 2026-09-26)`), and a recalled turn from another session carries the day it was said
  (`[2026-09-20] I went hiking yesterday.`). Found in simulated conversations: the date of a move was extracted and
  stored, and the agent answered "I don't have the date", because relevant facts rendered as `subject predicate
  object` only. Off (the default) the prompt is byte-for-byte what it was. Due and expiring reminders, which always
  printed a date, now print it at its precision too (unchanged for dates stored before precision was).

- **`MemoryOptions.SharedRecallBudget`: shared knowledge gets its own recall budget and its own label.** With one
  budget, a large shared corpus (a book, a catalogue, a manual) competes with a person's own memories for the same
  top k and wins by numbers: measured on four embedding models, shared items took 7 to 8 of 10 fact slots on
  questions about the person and pushed out answers the person had given. A similarity floor cannot fix that (the
  shared items are relevant by score, just not about the person); separate budgets do: own top k plus shared top n
  cut shared items per question from 7.6 to 3.0 with every answer kept or better, on every model tested. When set,
  an owner's recall that includes shared memory searches its own rows and the shared rows separately (live,
  point-in-time and fan-out legs alike; predicate expansion and derived facts read the owner's own rows), and
  `MemoryContext.SeparatesSharedKnowledge` tells the renderers, which then show owner-less entities, facts and
  preferences under a "shared knowledge, not about the user" label instead of "Known facts" / "User preferences".
  A long-term search called directly then returns up to its limit of own rows plus up to the budget of shared ones.
  Null (the default) keeps one budget and renders as before.
- **`LlmExtractionOptions.OwnPreferencesOnly`: a preference is the user's own stated taste.** Every extractor is
  told that someone else's taste ("my brother hates cilantro", a character who "does not like raw eggs") is a fact
  about that person and that a request ("recommend some music") is not a preference. Found live: a taught book's
  characters' tastes, and a request, were stored as the user's preferences, and a preference always renders as the
  user's. Off (the default) every prompt is byte-for-byte what it was.

- **Forget a message without deleting it: `IShortTermMemoryService.InvalidateMessageAsync`.** The message
  stops being recalled or read back (recent messages, message search, whole-session and conversation reads, source
  quotes, session previews) but is kept, with its provenance, for history: as-of reads of times before it was
  forgotten still return it, and `Message.InvalidatedAtUtc` says when it was forgotten (date grounding still reads
  its timestamp). The unscoped message search now over-fetches as the filtered search did and still returns at most
  `limit`, so forgotten messages do not leave it short (unless more than the over-fetch margin of them outrank every
  live match); a session with nothing live left lists by when it was created. The facts learned from it are
  separate memories and stay. Found live: after forgetting a person and every fact about them, the agent still
  answered from the message that named them. New members have default implementations that throw
  `NotSupportedException` (`IMessageRepository.InvalidateAsync` too), so existing implementations still compile.
- **`ExtractionRequest.ShareWithEveryone`: general knowledge, recalled by everyone.** A book, a manual or a
  policy is taught once and stored **shared** (no owner): every owner's recall finds it (shared is included by
  default) and nobody's profile block lists it (the profile reads only the owner's own memories). It is an
  explicit administrative write, the deliberate way to make the owner-less write that strict multi-tenant
  isolation refuses from a tenant operation. Its entities are resolved against **shared entities only**, so a
  book's “Alice” never attaches to a user's private “Alice”, and a tenant's writes never replace or aggregate
  shared facts (a tenant's extraction may still add an alias to a shared entity it matches, as before). Cannot be
  combined with `UserId`. Found live: a book taught
  into a person's own memory put its facts in that person's profile, and three of eight agent models then
  called the user “Alice”.
- **`AgentFrameworkOptions.RecalledMemoryBeforeQuestion` (off): the memory before the question.** MAF appends
  a context provider's messages after the request, so the model reads the question and then the recalled
  memory. Llama 3 models (llama3.2 3B, llama3.1 8B, measured by replaying one prompt both ways) read a system
  message in last place as the end of the turn and answer with nothing; with this on they answer from the
  memory. Off by default: every other model's prompt layout is unchanged. Both the Neo4j and the NAMS
  providers honour it.
- **`ExtractionOptions.SkipPlainQuestions` (off): a question that states nothing costs no extraction.**
  With `IgnoreQuestions` the model returns nothing for “Where does my brother live?”, yet the call cost
  1.0–1.1 s and ≈450 prompt tokens per question (measured). A turn is skipped only when every sentence is a
  question and nothing in it could be new: no digits, no time words (“next week”, “in October”), no names,
  and no “I” beyond the opening (“Do I…”); “What does Dana do?” and “…my trip next week?” are still
  extracted. Precision over recall, like `SkipUninformativeTurns`.

- **`AgentFrameworkOptions.ExtractInBackground` (dark): the answer does not wait for memorising.**
  Extraction is a model call plus resolution and writes; inline, every run waited for it although the
  reply was already complete. On, the turn's messages are still stored inline and extraction goes to
  the new `IBackgroundExtraction` (registered by `AddAgentMemoryFramework`): in order per session,
  concurrently across sessions (`BackgroundExtractionConcurrency`, default 4), carrying the turn's
  owner and store scope, drained on shutdown for up to `BackgroundDrainTimeout` (30 s). All three
  entry points that extract on persist (the context provider, the chat-history provider, the facade)
  share one dispatcher, so they behave the same. Register your own `IBackgroundExtraction` to hand the
  work to another scheduler. Queued work resolves its services from a fresh scope (the turn's, an ASP.NET
  request say, has usually ended), runs on the thread pool rather than the caller's context, and is its
  own trace linked to the turn's. With `ExtractionContextTurns`, the read-only context is only what came
  before the turn: extracted later, the newest stored messages can be the next turns. Measured, 10 live turns per arm with a 6 s pause between turns: median
  answer 3.5 s → 1.5 s (−57%), p95 9.7 s → 4.5 s, same facts stored. The trade-off: a fact stated in
  one turn may not be recallable in the next if that turn starts before extraction finishes.
- **`ExtractionOptions.ResolveUserToName` + `LlmExtractionOptions.CaptureUserName` (dark): "user" is
  the owner.** The extractor calls the speaker "user", so one person was stored under two names ("Dana |
  lives in | Porto" beside "user | is learning | cello" and "Lena | is sister of | the user"), and the
  profile listed both. `CaptureUserName` asks every extractor rung for a `user | is named | <name>` fact
  when the user states their name; `ResolveUserToName` then stores facts whose subject or object is
  "user" (or "I", "me") under that name, taken from the same extraction or, in a later session, from
  the owner's latest live stored naming fact (one keyed read, `IFactRepository.FindLatestObjectAsync`,
  whatever casing or separators the extractor used; a failed read leaves the words used). Rewritten:
  "user"/"I"/"me" as a subject, "user"/"the user" as an object; never what the assistant said about
  itself, never the naming fact itself, which is also never merged away. A fact now stored under the
  name also supersedes (for single-valued relations) what was stored under "user" before the name was
  known. Until a name is known nothing changes; the words used are kept as `subject_surface` /
  `object_surface`; with `CanonicalFactSubjects` the name resolves to the known person's full name. The
  Relationships too: "user -WORKS_AT-> Fabrikam" was dropped at extraction ("source entity 'user' not
  resolved"); with the option it starts at the user's person entity (the one named in the same turn,
  else the stored one, found live: never a name merged into another person or invalidated, via the new
  `IEntityRepository.FindLiveByNameAsync`; no edge from a node to itself). Live: "I'm Ana, I work at Fabrikam in Madrid; my brother Tiago lives in Lyon"
  stored all three relationships from Ana, none skipped. The
  instruction only adds the naming fact: an earlier wording ("refer to the person speaking as user") made
  the model return nothing for book passages ("fictional text; no user memory"), 6 of 18 chunks; now 1 of
  18, as without the option. Measured on one two-session conversation: without it 3 facts about "Dana" and 5
  about "user"; with it all 8 under "Dana", including the second session's.
- **`AgentFrameworkOptions.ExtractFromUserMessagesOnly` (dark): learn from what the user said.** With
  auto-extraction on, the agent's reply was extracted too, so every time the agent repeated a fact back
  ("you live in Porto, right?") the fact's mention count went up, and a summary reply re-learned the
  profile as new rephrased facts. On, only the turn's request messages are extracted; the reply is still
  stored as a message. Applied by every entry point that extracts on persist (the context provider, the
  chat-history provider, the facade). Measured on one six-turn conversation: 11 facts with the reply (three rephrased
  duplicates, "lives in Porto" at 3 mentions from echoes alone) against 8 facts, one mention each,
  without. Off by default because an agent that states new facts itself (tool results, lookups) would
  no longer have them extracted.
- **`ExtractionOptions.DeduplicateWithinExtraction` (dark): one fact per statement.** A single message
  produced "moved to | analytics team" and "moved to | analytics": same fact, two nodes. On, a fact at
  least `WithinExtractionDuplicateThreshold` (0.93) similar to another fact of the same extraction is
  dropped in favour of the more confident one. The threshold is measured: duplicate phrasings scored
  0.912–0.996 with bge-m3, the closest distinct pair ("mentor of" / "manager of" one person) 0.879.

  Similarity alone is not enough: two facts merge only when they share a subject, differ in one of
  predicate and object, carry the same numbers and negations, and have no conflicting validity window
  ("has 2 kids" / "has 3 kids" and "is vegetarian" / "is not vegetarian" score as near-identical). The
  user's phrasing is kept over the assistant's, then the more confident; it takes over a date or source
  turn only the other had, and the dropped one is reported as a `Skipped` outcome
  (`MemoryErrorCodes.FactMergedWithinExtraction`).
- **`ExtractionOptions.CanonicalFactSubjects` (dark): one person, one fact subject.** Extraction writes
  the words used, so "Tomás | moved to | analytics team" and facts about "Tomás Silva" had two subjects
  for one person. On, a fact's subject and object are stored by the resolved entity's name (the words
  used are kept as `subject_surface` / `object_surface`), so mentions merge into one fact and keep their
  ABOUT edges. Names that resolved to no entity are kept as written.

- **`AgentMemoryChatHistory`: chat history without the injected memory.** MAF's default
  `InMemoryChatHistoryProvider` stores the memory a context provider injected, so every turn's recalled
  blocks were sent again on every later turn (measured: 17 messages instead of 8 on the third call).
  `AgentMemoryChatHistory.CreateInMemoryProvider()` is that history without them;
  `ExcludeInjectedContext` is the filter on its own, and `Neo4jChatHistoryProvider` stores through it.
  The samples use it.
- **`EntityResolutionOptions.IndexedCandidates` (dark): resolution that scales with the owner's
  entities.** By default every live entity of a mention's type is loaded with its vector on every
  extraction: an owner with 5,000 people cost 0.8–1.2 s per resolution. On, the string matchers get the
  candidates without vectors (`IEntityRepository.GetByTypeWithoutEmbeddingAsync`, a default-bodied
  addition), the semantic matcher gets the entity vector index's nearest neighbours
  (`SemanticCandidateLimit`, default 20), and a match is read back in full before anything writes it.

- **A local embedding model is one variable away.** `AgentMemory.Inference` gains an `ollama` provider
  (`http://127.0.0.1:11434/v1`, no key, `bge-m3` by default, `OLLAMA_MODEL` for chat; used only when
  named, never auto-detected), and `AI_EMBEDDING_PROVIDER` alone now moves embeddings to another
  provider, filling what is not set from that provider's own variables and defaults (never from the
  chat provider; with `AI_EMBEDDING_ENDPOINT` set, the key comes only from `AI_EMBEDDING_API_KEY`, so
  a key only goes to the host it was issued for). `AI_EMBEDDING_PROVIDER=ollama` puts
  embeddings on a local bge-m3 while chat stays remote; `=bitdeer` moves them back. Ollama's `bge-m3`
  returns the same vectors as `BAAI/bge-m3` (measured cosine 1.0000), so an existing store keeps
  working. Measured per short text: 52 ms local (GPU), 70 ms CPU-only, 1.3 s typical for the hosted call.

- **`AgentMemory.Inference` (new package).** One environment contract for every inference host:
  `AI_INFERENCE_PROVIDER` selects `azure`, `bitdeer`, `openai`, `foundry` or `openai-compatible`,
  and a resolver turns it into chat and embedding clients over two protocol families. The library
  packages still construct no model client and are unchanged; this is for hosts. Use it with
  `services.AddAgentMemoryInferenceFromEnvironment()`, which fails at registration rather than at
  first use.
- **Bitdeer support.** `BITDEER_API_KEY` alone configures chat (`zai-org/GLM-5.3-Flash`) and
  embeddings (`BAAI/bge-m3`).
- **Embedding dimensions are resolved, never guessed.** The width is store-defining — it builds the
  Neo4j vector index — so an unknown model fails closed with the reason rather than defaulting.
  `AI_EMBEDDING_DIMENSIONS` sets it explicitly and overrides the shipped table.
- **Run identity is `model@provider`** (`model@provider/dims` for embeddings) everywhere a measured
  number is written, extending the provenance stamp added in 1.5.0. The same model id served by two
  hosts is not the same measurement.
- Docs: [`docs/configuration/inference-providers.md`](docs/configuration/inference-providers.md) is
  the operator contract.
- **Partial-name entity resolution (opt-in).** `EntityResolutionOptions.EnablePartialNameMatch`
  resolves a partial person name to the **one** known person whose name contains it as whole words,
  or is contained by it: "Priya" → "Priya Nair". Before, a first name alone matched nothing (not
  equal, token-sort ratio 67 < 85, a one-word embedding below 0.8), so every later mention created a
  second person. The matcher never guesses: with two Priyas known it declines, records a
  `memory.resolve.partial_name_ambiguous` activity event so a host can ask which one was meant, and
  leaves the decision to the remaining matchers exactly as with the option off.
  People only by default (`PartialNameMatchTypes`): "Microsoft" and "Microsoft Research" are
  different organizations. Reported as `EntityMatchType.PartialName` (new member, value 4) at
  `PartialNameMatchConfidence` (default 0.9, the SAME_AS band: resolves without rewriting aliases).
  Off by default.
- **Embedding cache (opt-in).** `MemoryOptions.EmbeddingCacheCapacity` remembers the vectors of the
  most recently embedded texts (LRU, container-wide, in memory). Measured on a live turn: the user's
  message was embedded twice (as the recall query and again when stored), and known entity names were
  re-embedded every turn they were mentioned, each a ~1 s round trip for an identical vector. `0`, the
  default, disables it.
- **A complete, correlated trace per turn** ([`docs/observability.md`](docs/observability.md)). One
  vocabulary, `MemoryTelemetry`, names every span and attribute. New spans: `memory.hook.recall` and
  `memory.hook.ingest` (the PRE/POST hooks, with session and conversation, and only *whether* the turn
  was owner- and app-scoped: those ids are tenant data; the session id is read from W3C baggage when a
  host propagates one), `memory.route` (the recall policy's
  decision), `memory.compose` (admission and formatting) and `memory.embed` (inputs, cache hits, sent).
  Recall legs carry `memory.type` and `memory.results.count`. Neo4j spans carry `db.system`,
  `db.operation.name`, `db.rows`, the server's own timings when the caller consumes the summary, and
  `db.attempts` / `db.retries` (managed-transaction retries were invisible). Failures record the exception
  *type* (`error.type` + an `exception` event), never its message. Background access tracking and
  enrichment run in their own traces **linked** to the recall or ingestion that queued them. AgentMemory's
  own queries that no marker recognised are named `unregistered:<index-or-label>:<hash>` (literals and
  numbers normalised) instead of one `unknown` bucket (a live session had 340 of them; now 0); Cypher passed
  to the graph-query service is never given a structural name. `memory.compose` counts admission flags, exclusions and dedup drops.
  Nothing is recorded without a listener.
- **`memory.extract.attempt` spans.** Every extraction model call is a span tagged with its outcome
  (`ok`, `salvaged`, `syntax_error`, `schema_error`, `truncated`, `filtered`), the provider's finish
  reason, output tokens, items kept and dropped, and the error in words. A failed extraction is no
  longer only "raw length 2,354" in a log line. `LlmExtractionOptions.LogRawResponseOnFailure` (off)
  additionally logs the first 2,000 characters of an unusable reply.

### Changed

- **Dates and the shared-knowledge budget are on by default.** `IncludeDates` is now true on every renderer (Agent
  Framework `ContextFormatOptions`, Core `MemoryContextFormatterOptions`, Semantic Kernel `MemoryRecallSecurityOptions`,
  `WorkingMemoryOptions`) and `MemoryOptions.SharedRecallBudget` is 3: in simulated conversations both kept every answer
  and improved the dated ones. A store without shared memory of a kind is no longer searched for it (the split had
  searched twice per memory type everywhere; now one cached existence check per store and kind). Shared knowledge
  written in the same process is recalled at once; one written by another process, within 30 seconds. Set
  `IncludeDates = false` or `SharedRecallBudget = null` for the earlier prompt. The model-dependent extraction switches
  (`TemporalValidityMode.Extract`, `MarkCorrections`, `OwnPreferencesOnly`) and `SupersedeReplacedFacts` stay opt-in.

- **Entity resolution reads shared candidates by an index seek.** Entities carry `owner_key` (`"*"` when shared) on
  every write path, with an index and a backfill at bootstrap for existing stores, and the "own or shared" candidate
  read seeks the shared half by `owner_key = '*'`. It read every entity of the type across all owners to find the
  shared ones (`owner_id IS NULL` cannot be sought): 1,122 database hits on a store of 868 entities, growing with the
  whole store.
- **A preference stating a single-valued relation replaces its previous value**, marked as a correction or not:
  "Favourite band is Arcade Fire" closes "Favourite band is Radiohead" in the same category (with
  `SupersedeReplacedFacts`). Found in simulated conversations: the model did not always mark "not Radiohead".

- **A relationship said again is the edge already stored, and a single-valued relation ends its previous edge.**
  Extraction reuses the live edge with the same source, relation and target, so "lives in Lyon" stated twice is one
  edge (it was two: the store merges on id and every extracted relationship had a fresh one); the restatement keeps
  the stored edge's sources (adding its own), validity, description and attributes. With
  `SupersedeReplacedFacts`, a new edge of a single-valued relation ends the previous one from the same source,
  in any of its present-state forms ("lives_in Copenhagen" ends "lives_in Hamburg"; "employed_by" a new firm ends
  "works_at" the old one), and only once the new edge is stored. Found in simulated conversations: the facts were
  replaced and both residence edges stayed.
- **`favourite <thing>` is one relation however it is written**: "has favourite band", "favourite band is" and
  "favorite band" are replaced by a new favourite band.
- **A marked correction closes the fact it names, conservatively, before supersession runs**: among the live facts of
  the subject that mention the replaced value (either containing the other, as whole words), those stating the new
  fact's relation; failing that, the one fact that mentions it, only if it is the only one and the mention is more
  than one coincidental word. So "works for a wind energy firm, replaces the shipping company" closes "works at a
  shipping company" and leaves "left the shipping company" (a true event), and "age 7, replaces 6" never closes
  "weighs 6 kg". A correction on a shared write closes shared facts (it closed nothing before).

- **The speaker is the named person, not an entity called "user".** With `ResolveUserToName` (on by default), once
  the user's name is known an extracted "user" (or "the user", "I", "me", "myself") entity is written under that name,
  or not at all when the person is already an entity, and a relationship from a self word lands on the person: the
  prompt calls the speaker "the user", and the model listed "user" among the people, so a "user" node stood beside
  the person's own. Until the name is known it is stored as before, so no relationship hanging from it is lost.
- **A question's presupposition is not a statement** (`IgnoreQuestions`, on by default): the instruction now says
  that what a question takes for granted is not stated either ("When did I move to Lyon?" was stored as a move).

- **Write-time supersession recognises a change of mind stated in other words** (with `SupersedeReplacedFacts`):
  - a new value replaces **every present-state form** of its relation, not only its own predicate: "works for"
    replaces "works at", "employed by" replaces "works for". The forms are declared in the vocabulary
    (`presentForms`); history forms ("worked at", "used to work in", "lived in") neither replace the current value
    nor are replaced by it;
  - a stated **age** is written as the single-valued `age` relation ("Bruno is 7 years old" replaces "6 years
    old"; a bare number after "is" is left alone);
  - **`favourite <thing>`** is single-valued per thing (a plural, "favourite bands are", is not);
  - an **event states the state it entails** when the vocabulary declares it: "moved to Copenhagen" also writes "lives
    in Copenhagen", which replaces the previous residence; only from a completed form ("moved to", not "moving to"),
    for an event that has happened, when the object is a place ("moved to the analytics team" states no home), and
    never over a current state the same turn states ("I moved to London in 2010; now I live in Paris"; "I lived in
    Paris" is history and does not count); two moves in one turn each state their home, the later replacing the
    earlier;
  - a value whose validity has **already ended** ("worked at Google until 2019") no longer replaces the current one.

- **A shared write (`ExtractionRequest.ShareWithEveryone`) stores no preferences** (single and batch extraction). Shared knowledge has no user,
  so nothing it states is the user's taste; its facts are kept. The dropped count is tagged on the extraction
  span (`memory.extract.shared_preferences_dropped`).
- **An owner's mention joins a shared entity by exact name or alias only.** Fuzzy, partial-name and semantic
  matching see the owner's own entities; the exact matcher sees shared ones too. Found live: a person's "Bill
  Evans" (the pianist) was merged by the partial-name matcher into a taught book's "Bill" (the lizard), and his
  relationship pointed at the lizard. A shared write, or a resolution without an owner, is unchanged.
- **Similarity thresholds say which scale they are on.** Neo4j's vector search scores `(1 + cosine) / 2`, so
  `RecallOptions.MinSimilarityScore = 0.7` is a cosine of 0.40 (and 0.55 a cosine of 0.10, which admits almost
  everything); the in-process matchers (`SemanticMatchThreshold`, `WithinExtractionDuplicateThreshold`) compare raw
  cosines. Documented on each option.

- **Owner-first vector recall (`MemoryOptions.OwnerFirstVectorThreshold`, default 500).** The vector index is
  shared by every owner and filtered afterwards, so other owners' near-identical facts could crowd a small owner
  out: measured, a 24-fact owner got 2 of its facts in the global top 60, and a stored answer (“Dana works at
  Northwind”) was reported as “nothing stored”. An owner holding at most the threshold of facts (with shared,
  when included; counted only up to the threshold + 1 and cached 30 s) is now searched by scoring its own facts
  exactly, which cannot be crowded and costs about the same (1–2 ms at 24 facts, ≈15 ms at 306, ≈0.05 ms a
  fact); larger owners keep the index and its escalation. Both the live and the as-of fact search do it, with
  every clause of the indexed query (validity, derived filter, recency re-rank, projection). 0 turns it off
  (LongMemEval pins it off). Recall spans carry `memory.vector.owner_first`.
- **Reasoning traces are searched only when they are shown.** The context provider asked recall for reasoning
  traces (`MaxTraces`, default 3) although its formatter drops them unless `IncludeReasoningTraces` is on (off by
  default): a vector query per turn whose result was thrown away (≈35 ms, measured). It now asks for none when
  it will not show them.
- **Fan-out never splits a statement.** “My brother Pablo lives in Seville and my sister Lena lives in Berlin”
  matched rule C2 (a name each side of “and”) and cost the answer path an extra embedding and two more vector
  searches per memory type (+80 ms, measured). A declarative statement (no question mark, no interrogative,
  not opening with a request or an auxiliary) is no longer fanned out; “Tell me about Pablo and Lena” still is.

- **New defaults: the answer does not wait for memorising, and memory learns what the user said.**
  Four switches that shipped dark in this release are now on by default (all measured live):
  - `AgentFrameworkOptions.ExtractInBackground`: median answer 3.5 s → 1.5 s. With a next-turn guard,
    `RecallWaitsForPendingExtraction` (2 s): recall waits, at most that long, for the same owner's
    extraction still running, so a fact stated in the last turn can be recalled in this one. One owner's
    turns are learned in order, different owners in parallel. Turn it off on hosts that freeze the
    process after replying (serverless).
  - `AgentFrameworkOptions.ExtractFromUserMessagesOnly`: the agent repeating a fact back no longer counts
    as a mention, and its paraphrases are no longer stored as new facts.
  - `ExtractionOptions.ResolveUserToName` + `LlmExtractionOptions.CaptureUserName`: what the user says
    about themselves is stored under their name, not "user".
  - New `LlmExtractionOptions.IgnoreQuestions`: a question states nothing. "What do you remember about my
    brother?" had created an entity "user's brother" and the fact "Marta has a brother brother"; with
    it, 2 of 2 runs stored nothing from the question, and book extraction was unchanged (0 empty chunks
    in 12).
  The LongMemEval harness pins all four off, so its measured path is unchanged.

- **The working-memory profile tier is on by default.** A compiled "about this user" block (stable facts,
  active preferences, salient entities; at most 300 tokens) is now built after each write and rendered
  ahead of recall. Without it a new session asked "what do you know about me?" answered "a pretty thin
  file": a generic question's embedding matches few stored facts. With it the same question returned the
  user's name, job, employer, manager, family and preferences (3 of 3 runs), at the same answer time.
  `MinFactMentionCount` defaults to 1 (at 2 a short relationship's block held no facts). Costs: about
  300 prompt tokens per turn and a rebuild after each write. `WorkingMemory.Enabled = false` restores the
  previous behaviour.
  With the tier on, the Neo4j package activates its schema extension (`working-memory`, the
  `:User.identifier` constraint that makes the per-owner write race-safe) so the two cannot disagree.
  The block keeps itself current: it records the next moment a fact in it can expire or start and is
  rebuilt on the first read after it, including a block that is empty until a future fact starts; a
  prune or a failed rebuild removes the block's text (a hard prune must not leave the removed text in
  every prompt) and marks it due, so the owner's next read rebuilds it from what remains rather than
  going without a profile until the owner writes again; a rebuild on read that fails (a read-only
  connection, a timeout) never fails recall, and serves no block (or, with
  `ClearOnRebuildFailure = false`, the stored one), and is not retried for that owner for a minute; a
  new fact reaches a block whose slots are full of facts mentioned more often (`RecentStableFactSlots`,
  4 of the 12, go to the most recently learned of the rest; 0 restores slots by mentions only); a CLI
  `invalidate` / `supersede` marks the owner's block due; MCP recall now returns the block
  (`workingMemory`), which it read and dropped; new facts reach it (the most recently touched win a
  slot, while the text keeps a stable order). Semantic Kernel renders the block even when similarity
  found nothing, which is the question it exists to answer.
  **Upgrading a database that already ran the tier** (it was opt-in before): the constraint cannot be
  created while two `:User` nodes share an `identifier`. Check with
  `MATCH (u:User) WITH u.identifier AS id, count(*) AS n WHERE n > 1 RETURN id, n` and merge any
  duplicates before `migrate`.

- **Fan-out legs are embedded in one request.** When recall fan-out splits a question into sub-queries,
  each leg's query was embedded in its own request, one after another, after the main query: three
  sequential round trips on a two-part question (measured ~110 ms against a local model, ~2.6 s against
  a hosted one). The legs now go out together; if that request fails, each leg is embedded on its own
  as before.

- **The samples, `agent-memory-mcp` and the benchmark harness read the provider contract** instead
  of `AZURE_OPENAI_*` directly. **Azure-only machines are unaffected:** Azure is first in
  auto-detect and a test pins that a machine with only `AZURE_OPENAI_ENDPOINT` / `_API_KEY` /
  `_DEPLOYMENT` resolves exactly as before.
- **Samples and `agent-memory-mcp` now set `Neo4jOptions.EmbeddingDimensions` from the resolved
  model.** They previously relied on the 1536 default matching Azure's `text-embedding-ada-002`. On a
  1024-wide model that default builds a vector index that cannot match its own writes, with no error
  at any point.
- One SDK version set across the repository (`Microsoft.Extensions.AI.OpenAI` 10.8.3,
  `Azure.AI.OpenAI` **2.1.0**, `OpenAI` 2.12.0 referenced explicitly); three consumers previously
  carried three different pairs. `Azure.AI.OpenAI` is the newest *stable* release — 2.7/2.8/2.9 are
  all prerelease, and a shipped package may not depend on one.

### Fixed

- **An object the model wrote twice is stored once.** "Daniel | is a chef | chef" rendered "Daniel is a chef chef":
  a predicate that ends with its own object's words (with or without a leading article) is trimmed at write, on every
  fact, and an article left dangling moves to the object ("Daniel | is | a chef", "lives in | the Netherlands");
  "works at | Acme" and a predicate that would be left empty are unchanged.

- **"What do I have coming up in October?", asked in September, recalled last October** (`ResolveTemporalQueries`).
  A month still ahead this year, named without a year, now resolves to last year only in a past-tense question
  ("what did I do in October?") that points nowhere ahead ("where did I say I'd be", "what was planned" stay at now);
  otherwise the recall stays at now. Found once extraction dated future facts: the
  point-in-time recall of last October hid an appointment dated this October, and the agent answered "nothing".

- **Supersession replaces only a value that holds now**: one that has ended is history (closing it hid it from as-of
  recall) and one that has not begun is a plan. The same rule for winners and for the losers the query finds.

- **Which value one extraction leaves current is decided once, the same on both write paths** (with
  `SupersedeReplacedFacts`). Two values of one single-valued relation in one turn ("I moved to Copenhagen, then to
  Oslo", two favourite bands) closed each other on the batch path, which writes both before supersession runs. Now a
  value a correction of the same extraction names is old; of one relation's values the last said that is not old is
  current; every other one supersedes nothing and is closed by the current one once everything is written. So a
  correction wins over its old value restated after it, chained corrections ("Oslo now, not Copenhagen, where I'd
  moved from Berlin") leave the newest, and favourites follow the same rule. A correction naming its own value marks
  nothing, and a correction's other-relation fallback leaves alone what this extraction created or said after it.
  A value that has ended is never current; of two dated values the later-dated one is, whatever order they were
  said in; no correction of the extraction closes its current value ("Copenhagen, not Oslo ... no, Oslo"); a
  current value that fails to write is stood in for by the latest value not corrected away; nothing is closed until
  every fact of the extraction is written, on either path; and the current value supersedes what a replaced one would
  have (under another self word, say).

- **A month was stored as a day.** The temporal instruction now asks the model to write a date only as precisely as
  it was stated ("2024-03" for "in March 2024"): it wrote "2024-03-01", the stored precision was a day, and the agent
  answered "on March 1st". The parser already kept the reduced forms.
- **Extracted dates were resolved against a guessed year.** The temporal instruction tells the model each turn
  carries its time, but only the multi-session extractor sent it; the single-session extractors (every agent turn)
  sent none, so "a half marathon in April", said in September 2026, was stored as April 2025. With
  `TemporalValidity = Extract` every extractor now prefixes each turn with its time, by one rendering.

- **"works for" never superseded "works at".** Cardinality resolved stored predicates with the question-side
  resolver, which refuses stored-only forms, so a predicate stored under a single-valued relation counted as an
  unknown, multi-valued one. Stored predicates now resolve through the stored forms.

- **Entity resolution's vector-index prefilter read `SemanticMatchThreshold` on the wrong scale.** The threshold is
  a cosine (the semantic matcher computes one); the index scores `(1 + cosine) / 2`, so the default 0.8 asked the
  index for anything above a cosine of 0.6. It is converted now. The matcher already rejected the extras, so which
  entity resolves does not change; the candidate list is the one the setting describes.

- **The fact that names the user keeps the subject `user`.** With canonical subjects on, an extraction in which
  "user" resolved to the person the user named ("Hi! I'm Dana") stored the naming fact as `Dana | is named | Dana`,
  so the name was never found again (it is looked up under `user`) and the user appeared beside themselves as a
  separate person. Words that mean the user are now renamed only by the user-name rule, never to an entity's name.
- **One embedding request for a turn's new names, on the path every turn takes.** The batched pre-embedding
  of entity names (one request for all new names instead of one per name) only ran inside the multi-session
  batch pipeline; a single extraction request (every agent turn) never opened the resolution batch it needs,
  so four new names cost four sequential embedding calls (measured: 275 ms locally, ≈4.8 s against a provider).
  A single request now opens one, or joins the one already open.
- **The owner-scoped similarity scan is owner-bounded again when shared memory is included.**
  `owner_id = $owner OR owner_id IS NULL` cannot be seeked (Neo4j indexes no nulls), so with shared included
  the fallback scan read every fact in the store (profiled: 1,119 of 1,119 rows for a 306-fact owner). It now
  filters on the indexed `owner_key` (the owner, or `*` for shared): two index seeks.

- **Every extracted memory has a path back to what the user said.** Each Agent Framework component minted
  its own id for a caller's message: `Neo4jChatHistoryProvider` stored it under one id,
  `Neo4jMemoryContextProvider` extracted from a never-stored copy under another. So no `EXTRACTED_FROM`
  edge ever reached the user's words (edges went only to the assistant's reply), and with
  `ExtractFromUserMessagesOnly` there were no edges at all and every `source_message_ids` entry pointed at
  nothing (found by an external review; confirmed on live data: 0 edges, 0 of 9 ids resolving). A request
  message now gets one id, stamped once on the `ChatMessage` (its `MessageId`, if the caller did not set
  one), and every component stores it under that id; the context provider stores the user's message
  itself, and the store MERGEs on the id, so running both providers still leaves one node per message.
  Verified by a real `ChatClientAgent` turn against Neo4j with user-only extraction on and off,
  background extraction on and off, and with and without the chat-history provider: every entity, fact
  and preference has an edge to the user's `:Message`, every id resolves, one node per message. Cost: one
  more embedding and write per turn when only the context provider is configured.

- **Constructors that gained an optional parameter keep their 1.5.0 signature.** `Neo4jMemoryContextProvider`,
  `Neo4jChatHistoryProvider`, `Neo4jMicrosoftMemoryFacade`, `AgentTraceRecorder` and
  `ExtensibleMemoryContextProvider` gained a dependency this release; an added optional parameter is a
  binary break, so the old signatures remain as overloads and code compiled against 1.5.0 still loads.

- **`AgentTraceRecorder` records traces under the turn's owner.** It passed no owner unless given one, and
  the reasoning service does not read the ambient owner scope, so under strict multi-tenant isolation every
  trace write inside a provider's turn failed with "StartTraceAsync requires an owner scope". It now falls
  back to `IMemoryOwnerContext` (an explicit owner still wins).

- **A failed extraction or persistence is visible in the trace.** Both are swallowed so the turn still
  succeeds, but their spans ended with no status: the `memory.store.extract` span (or the ingest hook,
  for a persistence failure) now records the exception type and an error status, as does an extraction
  attempt whose transport retries ran out.
- **A partial name can never auto-merge.** With `PartialNameMatchConfidence` at or above
  `AutoMergeThreshold`, "Priya" was merged into "Priya Nair" as an alias, so a later "Priya" resolved
  through the alias and the ambiguity check never ran. Validation now refuses that combination.
- **"Priya's mom" is not Priya.** A relational name ("X's mom") is no longer a partial-name candidate
  for X.

- **Background writes land in the right store.** The access-tracking and enrichment consumers start in
  their constructor, so they inherited the store routing of whichever request first built the
  singleton: in a host with several application stores, access stamps and enrichment for application B
  were written to application A's store for the life of the process. Each batch now carries the
  application id it was queued under, and the consumer applies it around the write.

- **The agent answers the user's new message, not an old one.** Recalled conversation turns were
  returned newest first, and MAF appends a context provider's messages after the request, so they came
  after the user's new message. The last user turn the model read was an old one, and a live agent
  answered it instead. Recalled turns now go before the live thread (after any leading system messages
  the host supplied), in the order they happened, behind a one-line framing message (recalled text is
  reference data, not instructions). Memory blocks stay where they were, except on hosts that render
  them at the user role: those move to just before the user's message, so the question is always the
  last user message. Applies to `Neo4jMemoryContextProvider` and `NamsMemoryContextProvider` alike (one
  shared placement rule).
- **Recalled history no longer repeats the session's own chat history.** MAF hands a context provider
  only the caller's new messages, so `DeduplicateRecalledHistory` compared recalled turns against the new
  message alone and every turn the session's history already carried was sent twice. The dedup now sees
  the whole request (`Neo4jMemoryContextProvider`; the NAMS provider has no history dedup). `docs/agent-framework.md` shows how to keep recalled memory out of MAF's in-memory
  chat history, which stores injected messages by default.

- **LLM extraction no longer discards a whole response over one partial date.** With
  `TemporalValidityMode.Extract`, a model that knows only the month correctly writes ISO-8601
  `"valid_from": "2026-08"`. System.Text.Json accepts only full dates (by design, upstream), so the
  typed parse threw, the runner re-prompted with a misleading "not valid JSON", the model repeated the
  date, and every entity, fact and preference of the turn was lost. Measured live: 2 of 4 runs of one
  sentence stored nothing. `valid_from` / `valid_until` are now read leniently: `YYYY`, `YYYY-MM`,
  `YYYY-MM-DD` and full timestamps; a month or year means the **start** of the period for
  `valid_from` and the **end** of it for `valid_until`; an unreadable value drops only that date
  (recorded as a `memory.extract.date_dropped` activity event) and keeps the fact.
- **Validity dates are UTC, and an end date includes its last day.** A date-only value was read at the
  machine's *local* midnight, so the same extraction stored different instants on machines in different
  time zones. A value without an offset now means that date/time in UTC. A full date as `valid_until`
  (`"2026-08-15"`) now means the end of that day, not its first instant. Free-text dates are read only
  when they state their year, so nothing is filled in from the machine clock.
- **One unreadable item no longer costs the whole extraction.** A reply that is valid JSON with an
  item the schema cannot hold (a confidence written as `"high"`, an unreadable date) is now salvaged
  item by item: the readable entities, facts, preferences and relations are kept, and each dropped
  item is named by its path.
- **The repair prompt says what was actually wrong, and no longer grows.** On an unusable reply the
  runner used to say "That response was not valid JSON" (it usually was valid) and echo the whole
  reply back, so each retry cost more than the last and the model repeated its mistake. It now sends
  one replaced message naming the problem ("`facts[0].confidence`: The JSON value could not be
  converted to System.Double"), without echoing the reply. **Measurement note:** this changes the
  bytes of retry attempts only; a run whose every reply parsed first time is unaffected.
- **Truncated replies get room to finish; refusals are not retried.** A reply cut off by the output
  limit (`finish_reason = length`) is retried with twice the output tokens it used (ceiling 32,768)
  instead of an identical request. A content-filter stop is not retried at all.
- **New entities in one extraction are embedded in one request.** Resolution embedded each name the
  string matchers could not resolve in its own ~1 s round trip, sequentially. Those names are now
  embedded together up front; names already known cost no request.
- **A `memory.db.query` span ends when its result is read**, not when the driver returns a cursor (before
  the server had streamed anything); a result nobody finishes reading ends when the next query starts, a
  retry begins, or its transaction ends (`db.result.read = none | partial`).
- **Background queues no longer inherit the trace of whoever created them.** The access-tracking and
  enrichment consumers started inside the constructing caller's execution context, so every later
  background write became a child span of whichever request first built the singleton.
- **An entity created by resolution keeps the vector resolution already computed for its name.** The
  semantic matcher embedded the mention and discarded the vector; persistence then embedded the same
  name again. One remote round trip saved per new entity, identical vectors. Resolution itself is
  unchanged: the stored node and the turn's candidate set see the new entity without a vector until
  persistence writes it, as before, so later mentions in the same turn match exactly as they did.

### Note

Changing embedding model changes the store: 1536-wide and 1024-wide vectors cannot share an index,
so moving from `text-embedding-ada-002` to `BAAI/bge-m3` is a rebuild, not a configuration change.

## [1.5.0] - 2026-08-26

### Added

- **Recall fan-out (opt-in, off by default).** `MemoryOptions.FanOut` enables per-memory-type
  sub-queries: a deterministic gate decides whether a query is compound, a deriver splits it into
  typed legs, each leg retrieves against the real store, and the results merge into the monolithic
  sections by id keeping the higher score. Merged sections are **re-capped at the existing `MaxX`**,
  so a fan-out changes *which* items reach the prompt, never *how many*.
  - New public types: `MemoryTypeAffinity`, `RecallSubQuery`, `SubQueryYield`, `RecallFanOutReport`;
    `RecallRequest.SubQueries` and `MemoryContext.FanOutReport`. All additive; no interface members
    changed, so no default interface methods were needed.
  - Callers may supply `SubQueries` directly, which is honoured **even with the feature disabled** —
    an explicit request is not something a global flag vetoes.
  - `FanOutReport` distinguishes three states that must not collapse: `null` (the planner never ran),
    `GateFired = false` (it ran and declined), and fired-with-zero-contributions (it ran and changed
    nothing). `VoidReason` marks a report that cannot be read as a measurement at all.
  - Neo4j-backed hosts only in practice: the merge requires the scored search contracts.

### Verified

- **Neo4j 2026.02 / Cypher 25 compatibility.** Neo4j 2026.02 defaults *new* databases to
  `db.query.default_language=CYPHER_25`, which removes some Cypher 5 features. Checked against
  2026.02.3 (community, single database): the full schema bootstraps — 53 indexes and 13 constraints,
  including all six vector indexes with their `OPTIONS {indexConfig: …}` maps — and the integration
  suite passes **479/479**, identical to the 5.26 baseline. A differential plan sweep (every statement
  under both `CYPHER 5` and `CYPHER 25`) found **no statement that passes under 5 and fails under 25**.
  `CALL { WITH … }` and `id()` remain deprecated-but-working and are slated for migration. CI
  continues to gate on 5.26; no code change was required.

### Changed

- **⚠️ `agent-memory-mcp` now targets .NET 10.** The MCP server ships as a `DotnetTool`, so its target
  framework determines which runtime you must have installed to run it. **Installing or updating this
  tool now requires the .NET 10 runtime.** The library packages are unaffected — they continue to
  multi-target `net10.0;net9.0;net8.0`, so consuming `AgentMemory.*` from a .NET 8 or .NET 9
  application is unchanged.

  Every other app, tool, test and sample in the repository moved to `net10.0` at the same time. The
  MCP host could not be held back: `AgentMemory.Tests.Unit` references it, and a `net10.0` project
  cannot reference a `net9.0` one.

  Two things surfaced during the move and are worth knowing:

  - **Three known-vulnerable transitive packages** appeared under .NET 10's dependency resolution that
    .NET 9 never pulled: `SSH.NET` 2025.1.0 (GHSA-q939-rpr3-3284), `Microsoft.Bcl.Memory` 9.0.4
    (GHSA-73j8-2gch-69rq) and `MessagePack` 2.5.192. All are fixed — `Testcontainers.Neo4j` bumped
    4.11.0 → 4.14.0, the other two pinned at patched versions rather than suppressed. None reached a
    shipped package; all were in tools, tests and a sample.
  - **No performance claim is made.** The hermetic perf harness gates on query counts, which are
    runtime-independent, and two runs of identical code on the same machine differed by 12 points on
    total wall time. The migration is verified not to have changed query behaviour; it is not verified
    to be faster.

### Added

- **Access tracking off the recall path, safely (`MemoryOptions.UseAccessTrackingQueue`).** Off by
  default. Access stamps feed decay and retention; nothing in a returned context depends on them, so a
  caller blocked on the write is blocked on nothing — at shipped defaults that was up to 25 write
  transactions before the model was even invoked.

  `DeferAccessTracking` already made the write fire-and-forget, and **its own documentation admits the
  flaw**: the write starts inside the request scope, so a host that disposes that scope on response
  completion can dispose the repository under an in-flight write, surfacing as an
  `ObjectDisposedException` in a log nobody reads while access tracking silently stops. This is the same
  optimisation done safely — a **singleton** channel owned by the root container, drained by one
  long-running consumer that takes a fresh scope per batch, so the write outlives the request by
  construction. It supersedes `DeferAccessTracking` where both are set.

  Bounded and drop-on-full: an unbounded queue turns a slow database into unbounded memory, and a
  blocking one puts the latency straight back. Dropping is right for this payload specifically — a lost
  stamp ages one memory's retention marginally against a 30-day half-life — and drops are **counted and
  logged**. It drains on dispose, which is what makes "audit rows equal at end of run" checkable.

  Two defects were found in the first draft by its own tests and are worth recording, because both
  would have shipped looking correct:

  - Under `BoundedChannelFullMode.DropWrite`, `TryWrite` returns **true** and discards the item, so the
    drop counter keyed on its return value counted zero forever while the queue silently threw work
    away — precisely the "quietly discarding its input" failure the class comment warns against. Now
    counted through the channel's `itemDropped` callback.
  - A singleton implementing only `IAsyncDisposable` makes `ServiceProvider.Dispose()` *throw*, breaking
    every host that disposes its container synchronously. It now implements both.

- **Self-consistency voting and quote-forcing in the evaluation harness** (`--answer-votes`,
  `--quote-forcing`). Defaults are one unvoted, unforced answer call — byte-identical to every archived
  run. The pre-registered primary claim is that the **band narrows** across repeat runs, not that point
  accuracy rises: with a measured 14-point spread between two identical accepted runs, a point
  comparison on n=50 is noise wearing a decimal.

  Votes get distinct seeds derived from `--answer-seed`, so a run stays reproducible from one recorded
  number. Clustering is deliberately conservative — case, whitespace, trailing punctuation, nothing more
  — because stripping articles or stemming would merge answers a judge scores differently, turning a
  real disagreement into an invented consensus. A three-way split is *reported* rather than resolved,
  since spending an LLM tiebreak costs money and must not be decided implicitly inside an aggregation
  helper.

  Quote-forcing asks for `EVIDENCE: "<verbatim quote>"` (or `EVIDENCE: NONE FOUND`) before the answer,
  and an unformatted response keeps its answer with the miss recorded — discarding it would convert a
  formatting failure into a scored memory failure. The two compose: votes cluster on the *answer*, not
  the two-line envelope, or agreeing answers citing different quotes would count as disagreement.

  **The void witness here is a live outcome, not a formality.** Proposal F assumed the provider's forced
  temperature 1.0 is the sampler; 30.1 then measured that seeding *halves* answer variance on this
  deployment. If votes are byte-identical on >80% of questions, the sampler is not sampling — that is a
  measured provider property, and the pre-registered response is to record it and stop.

- **Legible forgetting — a stated absence (`RecallOptions.LegibleForgetting`).** Off by default.
  Forgetting already worked and was **invisible**: decay pruned, recall returned less, and the agent
  answered as though it had never known — indistinguishable, to the person asking, from never having
  been told. A memory system whose gaps all look like the same gap cannot be corrected by its user,
  because they do not know there is anything to re-supply.

  On a recall whose fact section comes back **empty from a search that actually ran**, one extra
  vector probe asks what the system used to know about this and has let go. What surfaces is a
  `ForgottenTopicSummary` — topic, count, dates — and never the forgotten content, because rendering
  that would undo the forgetting outright: the decayed values would be back in the prompt, occupying
  budget, being answered from.

  **The partition is `invalidated_reason`, a new property the prune stamps.** `invalidated_at` alone
  cannot tell a fact that *decayed* from one that was *contradicted*, and reporting the second as
  forgotten is wrong in the most damaging direction — the system did not forget it, it **replaced** it,
  and the replacement is live and should be answering the question. Supersession deliberately stamps no
  reason; the null is the partition. Facts invalidated before this shipped have an unknowable reason
  and are simply never reported — a disclosed start-at-deployment limit rather than a backfilled guess.

  **Zero parity cost, zero new schema, zero migration.** The probe reuses `fact_embedding_idx`, which
  already contains these nodes: soft-invalidation keeps the embedding, and every live query filters
  them out afterwards. This inverts that filter.

  Three gates, each load-bearing: the flag; an existing query embedding, so a turn narrowed to skip
  embedding does not have one reintroduced by a diagnostic; and **thinness** — a recall that answered
  the question has nothing to apologise for, and a section that was never searched has not established
  an absence. It applies the **same** similarity floor a live search would: a tombstone clearing a
  looser bar is a confident claim about having forgotten something on an unrelated topic, which invites
  the user to re-supply information they never gave. No escalation ladder either: if the global top-K
  starves, the tombstone silently does not render, which is the correct failure direction for a surface
  whose entire job is honesty about absence.

  Precedence is resolved once, in the assembler: a tombstone suppresses the projection layer's
  no-direct-match line for the same section, since the two make overlapping claims and rendering both
  would say it twice and then disagree about how much is known.

  Like firing, it is deliberately absent from the as-of recall path, and the reason is recorded in
  `AsOfRecallDivergenceTests`: a tombstone is a statement about the **present** state of memory, and at
  the as-of instant those facts may still have been live.

- **Prospective firing — memory that volunteers (`RecallOptions.ProspectiveFiring`).** Off by default.
  Every other retrieval channel is *reactive*: it answers the question in front of it. A reminder is
  off-topic by definition — nobody asks "is there anything I should know?" — so a similarity-scored
  channel can never surface one. Firing selects by **time alone**: no query embedding, no similarity
  floor, and that absence is the specification rather than an optimisation.

  Two sections, deliberately not merged: `DueFacts` (validity just opened) and `ExpiringFacts`
  (validity closes within `ExpiringWindow`). They are different claims, and a reader who has to infer
  which from the dates is a reader who skips the block. Both render **before** everything the query
  asked for, on both surfaces — the point of volunteering is prominence, and a reminder placed after
  the relevance-ranked answer to a different question has been delivered without being received.

  **Gated twice.** The flag, and `ValidTime == Current`: firing reads a fact's valid-time window, and a
  recall that is ignoring valid time has no window to read — surfacing facts by a clock the rest of
  that recall deliberately ignores would make the two halves disagree with no way for the reader to
  tell. Its own budget (`MaxDueItems`, default 5) rather than competing with `MaxFacts`, because a
  reminder that loses a budget contest to a relevance-ranked fact has already failed at the one thing
  it exists to do. A fact that is both relevant and due renders **only** as due.

  **Zero parity cost and zero new schema**: it reads `valid_from`/`valid_until`, which already exist,
  and is served by the range indexes the `delta-recall` extension creates over the same clocks. Without
  that extension the query is still correct, just planned as an owner seek plus a filter — a disclosed
  cost, not a hidden one.

  The counter this feature would be withdrawn over is **premature surfacing**: a not-yet-valid fact in
  assembled context is a confident statement about a world that does not exist yet. It has a dedicated
  live-graph test, verified to be the only failure when the upper window bound is removed.

  Firing changes *when* a fact surfaces, never its trust: due facts go through the same delimiter and
  the same per-item admission check as every other recalled category.

  The as-of recall path deliberately does **not** fire, and that decision is now recorded in
  `AsOfRecallDivergenceTests` — the guard caught the omission before it could become a discovery. An
  as-of recall reconstructs what was known at a past instant; splicing present-tense urgency into a
  historical reconstruction would be actively misleading about which world the answer describes.

- **Arithmetic memory — the session accountant (`ExtractionOptions.DerivedMemory`).** Off by default.
  16% of LongMemEval questions have a **derived** answer: a count, a difference, a latest-of-chain, a
  duration, a list. The store holds `800` and `50`; the answer is `750`, and nothing ever wrote it
  down. Every retrieval-side idea in this project died against a saturated coverage ceiling; what
  remains alive is the class of answers retrieval structurally *cannot* produce, because they are
  properties of a **set** and retrieval returns a sample of it.

  A deterministic post-persistence pass materialises aggregates for the `(subject, predicate, owner)`
  groups each extraction batch touched. **LLM-free by design**: answer-time decomposition died 0/29 on
  perfect context and the answer model is the noisiest component in the stack, so arithmetic moves from
  a stochastic reader to a deterministic writer. Six operators — Count, Delta, Latest, SetEnumeration
  on by default; **Sum and Duration deliberately off**, the first because summing non-additive
  quantities is arithmetically perfect and semantically nonsense (so it takes an explicit predicate
  allowlist), the second because the current corpus stamps `UnixEpoch + counter` and durations computed
  there are fiction with a plausible shape.

  Every operator **refuses** rather than guesses: a group containing one unparsable object loses its
  numeric operators entirely, because the change between two values that happened to be readable is not
  the change over the chain. Nothing aggregates a single fact. The number parser — the only
  hallucination surface in the feature — strips a currency symbol and thousands separators and then
  defers to `decimal.TryParse`; it does not attempt "twice a week", "a couple" or "about 800".

  Each aggregate renders its arithmetic inline — `17 — derived: 12 (a1) + 5 (b2)` — so the model can
  **check** it. A derived number presented bare is a claim; presented with its inputs it is an argument.

  **The staleness cascade is the safety property of the whole feature, and it is same-statement.** A
  derived `750` whose input `800` was superseded is a manufactured confident-wrong answer — stored,
  embedded, recallable, wearing provenance that makes it look verified. `Supersede` and `Invalidate`
  now invalidate dependent aggregates in the same Cypher statement that retracts the input, and the
  cascade is **unconditional**: switching the accountant off must not freeze every aggregate it ever
  wrote into permanent truth.

  Ships as the `arithmetic` schema extension — one `DERIVED_FROM` relationship type and five documented
  properties on `:Fact`, **zero labels**. See [`docs/extensions/arithmetic.md`](docs/extensions/arithmetic.md)
  for why the edge earns its allowlist entry over the two parity-free alternatives.

  The marker property is `fact_kind`, **not** `kind`. Upstream already has a `kind` property meaning
  "audit-node discriminator", and overloading a name whose meaning another implementation owns is the
  changed-semantics hazard a parity check cannot catch — it compares names, not meanings. The
  `procedural` extension chose `trace_kind` over `kind` for exactly this reason; this one used `kind`
  anyway on its first draft, and the parity verifier rejected it as *"upstream caught up to .NET
  superset"*.

  Two binding guards from the TCK audit, both enforced structurally:

  - **G1 — the cascade is cardinality-safe.** `OPTIONAL MATCH` plus `WITH DISTINCT` on both sides, so a
    fact with N dependants does not multiply the row its caller counts, and a store with no derived
    facts behaves exactly as before.
  - **G2 — the fact upsert cannot merge into a derived node.** Enforced by *omission*: a derived fact
    carries no merge-key quadruple at all, so MERGE and `FindByTriple` cannot reach it. A user restating
    a number would otherwise land on an aggregate, overwriting its value while leaving its
    `DERIVED_FROM` edges and derivation string intact.

  Also in this change:

  - `--extraction-compare --vocabulary-ab` measures the predicate-vocabulary prerequisite this feature
    hard-depends on (aggregation needs two facts to agree they are instances of the same predicate;
    421 distinct predicates over ~700 facts means they never do). It runs both arms **in one process
    over byte-identical input** — the two-cold-build A/B the plan originally scheduled is not
    achievable, because the flag changes the extraction prompt and two builds of one *unchanged*
    configuration already disagree on ~86% of triples.
  - `MethodBuiltQueryStructureTests` matched labels by scanning for every `:Name` and excusing
    relationship types from a hand-written list containing exactly one entry. It now matches node
    labels and relationship types in their own syntactic positions, and checks both.

- **Delta recall — "what changed since I last looked?" (`IMemoryRecall.RecallChangedSinceAsync`).**
  Off by default at the adapter (`AgentFrameworkOptions.InjectDeltaOnSessionResume`). An agent resuming
  work re-receives everything it already processed; full recall re-assembles the same facts at every
  session start, and there was no way to ask for the difference.

  Every ingredient already existed and was already enforced on the live write path — `created_at`
  stamped on create only, `invalidated_at` stamped idempotently, `SUPERSEDED_BY` edges,
  `valid_from`/`valid_until`. Nothing read them as a diff. Eight buckets (new / superseded-as-pairs /
  invalidated / expired-validity / newly-due / new preferences / superseded preferences / new entities)
  are **disjoint by construction**: the window is half-open, `(since, until]`, everywhere without
  exception, and the upper bound is read from the clock **once** and handed back as the next
  checkpoint. That is what makes consecutive deltas partition time exactly, and it is also what makes
  the feature verifiable without a judge or a benchmark.

  The subtlest case has a test named after it. Supersession stamps **both** clocks, so a superseded
  fact would appear as a pair *and* as an expiry — two entries for one change — without the
  transaction-clock gate on the expiry query. Removing that gate was verified to fail exactly one test
  and no others.

  Ships as the `delta-recall` schema extension: **seven RANGE indexes, no labels, no properties, empty
  parity delta** — the clocks were already there, and the extension only makes them seekable. TCK
  Gold-safe with the extension on for two independent reasons: an index changes plans and never
  results, and the new members are called by no bridge endpoint. See
  [`docs/extensions/delta-recall.md`](docs/extensions/delta-recall.md).

  The checkpoint is a **caller-held token**, not a stored node — it rides the MAF session's state bag,
  so no schema pays for it. Advancing it is an *acknowledgement*, not a read receipt: a turn that threw
  advances nothing and its delta is replayed, because replaying a change set costs tokens while losing
  one loses knowledge.

  Two things found while building it, both fixed here:

  - `MemoryService` now resolves the delta's owner scope through `IMemoryIsolationPolicy`, as every
    other read does. A delta reads the repositories directly — the assembler is not in that path — so
    passing a caller's scope straight through would have handed a caller who supplied only a `UserId`
    an unfiltered, cross-owner answer.
  - The extension **documentation drift guard** was enumerating its subjects from a hand-written list
    and had therefore silently stopped covering each new extension as it was added; it now reads
    `SchemaExtensionRegistry.CreateShipped()`.

- **The working-memory tier — a compiled per-owner profile block (`MemoryOptions.WorkingMemory`).**
  Off by default. Everything else the system retrieves is probabilistic (query embedding → global
  vector top-K → owner post-filter → threshold); this is a **point-read by owner**, so it cannot be
  starved. Starvation is measured, not theoretical: an owner's own facts inside the global top-60
  averaged **7, minimum 1**, and one real question retrieved **zero** facts from a graph holding 504 of
  its own — all live, all above the floor.

  Ships as the `working-memory` schema extension, and it is the **first parity delta that removes an
  upstream-only label**: `:User` leaves `UpstreamOnlyLabels` and `NetOnlyLabels` stays empty, so
  adopting it *narrows* divergence. It is keyed by upstream's own unique property `identifier` under
  upstream's own constraint name `user_identifier` — a correction to the design, which had proposed a
  new `user_owner_unique` constraint on `owner_id`; adopting a label while keying it differently would
  make the adoption nominal, the same spelling carrying a different meaning, which is exactly what the
  parity verifier cannot catch. See [`docs/extensions/working-memory.md`](docs/extensions/working-memory.md).

  **Staleness is the kill rule.** Structured recall scores 8/9 on knowledge-update — the weakest
  measured non-episodic type — so a block asserting the *old* value of an updated fact would
  manufacture failures in exactly that type. Hence: full eager rebuild with no partial invalidation,
  awaited inline so the contract is "after the write returns, the block is current", and the block is
  **cleared** rather than left stale if a rebuild fails. A live canary asserts that superseding through
  the production path leaves the new value and not the old.

  Rendering (`ContextFormatOptions.IncludeWorkingMemory`, also off by default) goes through the same
  per-item admission and delimiting as facts — the block is compiled from extraction output, so it
  earns no trust bypass.

- **A separate similarity floor for reasoning traces (`RecallOptions.MinTraceSimilarityScore`).**
  Null by default, which resolves to `MinSimilarityScore` — today's behaviour exactly.

  **This is a safety property, not a tuning knob.** At the shared 0.7 default, procedure retrieval
  *never abstains*: a sweep found every threshold from 0.00 to 0.86 behaves identically — a measured
  dead zone — so the one setting that looks like it controls procedure precision controlled nothing
  across the whole range anyone would plausibly set. The measured knee is **0.92** (0.90 is the free
  variant, at which no correct answer was lost). An agent handed a confident wrong procedure
  *executes* it, where an agent handed nothing investigates — so recalling no procedure is a strictly
  better failure than recalling the wrong one, and at the shared default only the worse outcome was
  reachable.

  Honoured on **both** recall paths, asserted at the query rather than at the option, and raising it
  leaves the other categories on the shared floor.

  A promoted procedure now also renders its length (`(16 steps)`) when match-quality projection is on:
  replaying the archive task promoted a 16-call exploration, dead ends included, and rendered as a bare
  outcome that is indistinguishable from a tight five-step recipe.

- **The projection layer — render what the store already knows (`RecallOptions.Projection`,
  `MemoryOptions.Projection`).** Retrieval computes a similarity score for every item and every
  renderer discarded it, so a 0.72 near-miss reached the model looking exactly like a 0.99 match; the
  graph holds `SUPERSEDED_BY` edges, conflicting facts and real source dates that never reached a
  prompt; and triples drop the tense, participants and ordinals their source sentences still carry.
  Five independent opt-in features now surface each of those:

  | Flag | What it renders |
  |---|---|
  | `AnnotateMatchQuality` | `[closest match, 0.72]` per item, and one `No stored item directly matches…` line when a section's *best* score is weak |
  | `ResolveSupersessions` | `(since 2023-05-12; previously Globex)` from supersession edges live recall filters out |
  | `RenderConflicts` | `CONFLICTING MEMORY — …` when two live recalled facts disagree |
  | `AttachSourceQuotes` | `— said: "…"`, the shortest source sentence containing the fact's object |
  | `GroundDates` / `ChronologicalOrdering` | the real date an item was stated, and optional within-section ordering |

  **Every flag is off by default and off is byte-identical**, asserted by SHA256 fingerprints over all
  three render surfaces — the Core Markdown formatter, the Agent Framework `ChatMessage` mapper, and
  the benchmark answer prompt — captured before any of this code existed and never regenerated.

  One pipeline, three surfaces. Projection runs once inside the context assembler (after budgeting, so
  its reads are paid only for items that reached the prompt) and produces a surface-neutral
  `MemoryContext.Projection`; all three renderers consume it through one shared helper, so a rendering
  decision is made once and cannot drift the way a procedure-trust clause once did — fixed in the
  benchmark harness while the product shipped the contradiction.

  Costs are bounded by construction: exactly one extra read per recall per read-feature (batched,
  id-anchored, and enforced by test), a quote-length cap, a quotes-per-recall cap, and a supersession
  chain cap. **Parity impact: zero** — no new labels, relationship types, properties, indexes or
  migrations.

  New repository members are default interface methods, so no existing implementation breaks:
  `IFactRepository.GetSupersessionPredecessorsAsync` and `IMessageRepository.GetByIdsAsync`.

- **Schema extensions — optional, additive-only schema modules (`Neo4jOptions.Extensions`).** A named,
  versioned module owns its declarations, its own migration namespace, its parity divergence, and its
  entry in the ownership report. **The default is the empty set, which is the base schema,
  byte-identical** — nothing about an existing deployment changes until an id is added. An unknown id
  is rejected at startup listing the known ones, rather than ignored: a deployment that asked for an
  extension and silently ran without it is the failure the mechanism exists to prevent.

  Extension migrations live at `Schema/Migrations/ext/<id>/000N_name.cypher` and are recorded under the
  namespaced key `ext/<id>/000N_name`, with the owning id on the new `(:Migration).extension_id`
  property. The base sequence always runs first and is untouched. This exists because a linear sequence
  cannot host optional modules: two independently-written features each correctly claimed `0012` as
  "next free after 0011", and a database enabling one and later the other would have had two scripts
  fighting over one key in the unique-constrained migration bookkeeping — one silently skipped as
  "already applied", leaving an index missing with nothing to report it. A base version key never
  contains `/`, so the existing `migration_version` constraint already covers both namespaces.

  The first extension is `procedural`, a **retro-wrap**: its schema already shipped in base migration
  `0011_trace_kind`, so activating it applies nothing. It exists to give `trace_kind` and
  `trace_kind_idx` an *owner* — that property shipped with its entire rationale in a Cypher comment,
  and nothing in the parity policy, the CLI or the docs recorded which feature it belonged to.

  Verified at **178/178** on the upstream TCK with the system merged and everything off, and again at
  178/178 on the same build with the extension on. See [`docs/extensions/`](docs/extensions/README.md).

- **`agentmemory schema-check` now reports schema ownership.** Every non-base shape names its owning
  extension, and an orphan fails the check (exit 1) — a divergence no active extension declares, or an
  applied `ext/<id>/…` migration whose id this build does not have registered, which means the database
  carries schema from a module the binary cannot account for. This fails *even when every index is
  present*, which conformance alone reports as OK.

- **`agentmemory schema-parity [--extensions <id,…>]`** additionally verifies the effective policy the
  named extensions compose. Base is verified either way, so an extension cannot hide a base
  compatibility break behind the allowlist it supplied itself.

- **Recalled reasoning traces can carry their outcome (opt-in).**
  `ContextFormatOptions.IncludeTraceOutcomes`, default `false`. A recalled trace rendered its `Task`
  and dropped its `Outcome`, so on a repeated task the injected block told the agent it had done this
  before and nothing about *how* — the `Task` text is what the agent is already holding. Everything a
  promoted procedure (`TraceKind.Procedure`) knows lives in `Outcome`, which means procedural memory
  was retrievable, owner-scoped, prune-exempt and **mute** on the Agent Framework surface. Found while
  wiring PLAN 7.6's benefit measurement, where it was one of three shut gates that each produce an
  identical "no benefit" result.

  Off by default because an outcome is model-written text: enabling it changes both the prompt bytes
  and what a recalled block can influence. It is admitted and delimited like every other recalled item
  (#92 Phase 1/2) — quoted, not trusted. `IncludeReasoningTraces` still gates the block entirely.
  Renders as `"task: outcome"`; note that a procedure written with `->` arrives at the model as
  `-&gt;` because admitted blocks are HTML-escaped, so write chains in words.

- **Valid-time recall (opt-in).** `RecallOptions.ValidTime = ValidTimeMode.Current` filters facts on
  their real-world window (`valid_from`/`valid_until`) rather than only on the transaction clock.
  Default `Ignore`, which is byte-for-byte today's behaviour, and `MemoryProfile.Parity` resolves to
  `Ignore` — parity means parity. **TCK exposure is zero**: the bridge exposes neither `/search_facts`
  nor `/get_facts_about`, so no conformance surface can observe this.

  Two things write validity bounds, and only two: temporal extraction, when
  `TemporalValidityMode.Extract` asks the model for a window, and **supersession**, which stamps
  `valid_until` as it closes a fact. The second is worth knowing about because it changes nothing
  here — supersession also stamps `invalidated_at`, and the transaction-clock filter already removes
  the fact from live recall whatever this option says. **So the gate is redundant for superseded
  facts and load-bearing only for windows the conversation actually stated.** Reaching for it to hide
  superseded data solves a problem that is already solved.

- **Reasoning traces carry a trust level.** `ReasoningMemoryOptions.DefaultTraceTrustLevel`, default
  `ModelGenerated` — a trace is the agent's own record of what it did. Traces were the one recall
  category with no trust signal: caller-supplied `trust_level` is stripped (a caller must never
  self-assign a level that bypasses admission checks) and nothing then stamped one, so every trace
  read back as `Untrusted` — indistinguishable from "no signal recorded". Safe at shipped defaults:
  `MinimumTrustForAdmissionBypass` is `ApplicationTrusted`, which `ModelGenerated` does not reach.

- **Owner-starvation rescue for short results (opt-in).** `MemoryOptions.RescueShortOwnerResults`.
  Neo4j's vector index is global, so an owner filter is a post-filter on a top-K drawn from every
  tenant — measured, a mean of **7 of 60** candidates reached the querying owner. Previously only a
  *totally empty* result triggered a rescue, on the argument that a short result still answers the
  question. It does for a small tenant and does not for a crowded one: one measured question returned
  **2 facts from a 710-fact graph** with the answer present, and was answered wrongly.

  The rescue is the owner-bounded **scan**, not a wider index query — widening is another draw on the
  same global index, and a tenant losing to 50 neighbours at top-60 usually loses again at top-480.
  The scan's cost scales with one owner's rows, so the small tenant the original argument protected
  pays *less* for it than for a wider index query. Off by default, and it takes whichever result is
  larger, so a genuinely sparse owner never loses indexed rows.

### Changed

- **A greeting no longer costs a full recall.** Measured on the hermetic `PERF-R-01` scenario — a
  greeting-only turn at shipped defaults — recall issued **13 Cypher queries and 12 read transactions,
  plus an embedding round trip, to retrieve 11 items**. Ten of those were recent messages, which need
  no vector at all. The 1.4.0/1.4.1 owner-starvation rescue made it worse rather than better: with no
  entities, facts or traces to find, each of those three searches returns empty, escalates to a widened
  index query, then falls back to an owner-bounded scan — **three queries each, all guaranteed to find
  nothing, because there is nothing to find.**

  The default `IAutomaticRecallPolicy` is now `TrivialTurnRecallPolicy`. On a turn that is nothing but
  a greeting or acknowledgement it recalls **recent messages only**; on every other turn it is
  byte-identical to the previous default.

  It **narrows rather than skips** deliberately: dropping recall entirely would also drop recent
  messages, and *"ok, go ahead"* is exactly the turn where an agent most needs the conversation so far
  to know what it is agreeing to.

  **What changes for you:** on a greeting-only turn you previously received whatever
  entities/facts/preferences/traces matched; you now receive recent messages only. To restore the
  previous behaviour, register the old policy explicitly:

  ```csharp
  services.AddScoped<IAutomaticRecallPolicy, ConfiguredAutomaticRecallPolicy>();
  ```

- **An empty recall section can now say why it is empty.** `MemoryContextSection<T>.Diagnostics`
  (null unless `RecallOptions.IncludeDiagnostics` is set) records whether the section was actually
  searched, the limit it asked for, how many items came back, the top and lowest scores, and the
  similarity floor in force.

  Three causes needed opposite responses and looked identical from outside: the section was never
  searched, the store genuinely holds nothing, or candidates existed and were filtered away — by the
  floor, or by an owner post-filter applied after the vector index picked a global top-K. `Searched`
  separates the first; `SearchedAndShort` exposes the third, which is otherwise invisible because the
  starvation escalation fires only on a *total* zero.

  An unscoreable section reports `null` scores rather than zero — zero is a real score.

### Fixed

- **Procedural memory: a reasoning trace can be promoted to a reusable procedure.** `TraceKind`
  (`Episode` by default) marks a trace as a procedure; `trace_kind_idx` makes it seekable; and
  task-similarity search takes an opt-in `proceduresOnly` filter, **null by default** so existing
  Cypher is byte-identical.

  A trace and a procedure are the same record read two ways: an episode says what happened *once*,
  a procedure says what to do *next time* — they differ by retrieval key.

  **The load-bearing part is the retention exemption.** `PruneSessionTraces` orders by `started_at`
  with age as its *only* criterion and fires on every trace creation once `MaxTracesPerSession` is
  set — so without it a promoted procedure is deleted by recency and the capability does not exist.
  The exemption is NULL-safe in both directions: a trace written before `trace_kind` existed is still
  prunable (or a retention cap silently stops capping) and still visible to an episode filter.

  New migration `0011_trace_kind.cypher` brings existing databases to parity.

- **Live recall can now honour a fact's valid-time window** — `RecallOptions.ValidTime`
  (`Ignore` by default, so nothing changes unless you ask).

  `valid_from`/`valid_until` persist, are writable through the public API, and the point-in-time path
  already filtered on them. **Live recall did not.** A fact valid from six months hence was returned
  *today*, and a fact whose `valid_until` had passed was returned **forever**. The gap was harmless
  until 1.4.0 shipped `TemporalValidityMode.Extract` — the writer it had been waiting for.

  Gated on **both** live fact paths: the indexed vector query and 1.4.1's owner-scoped fallback.
  Gating only the first would have left valid time bypassed for exactly the starved multi-tenant
  owners that fallback exists to rescue.

  Honouring it also delivers the first two mechanisms of prospective memory — *expression* and
  *gating* — i.e. due-on-next-interaction semantics. Acting at a time with no query is a scheduler,
  a different risk class, and deliberately not in scope.

- **Recall misses are now observable.** `memory.recall.section.empty` and
  `memory.recall.section.short`, tagged by category. A `:MemoryReadAudit` row is created *inside*
  `MATCH (n {id: $id})`, so a row existed only for a **hit** — there was no record anywhere that an
  owner asked and memory had nothing. `…section.short` exposes the owner-starvation shape, which the
  escalation path cannot see because it fires only on a *total* zero.

  Counters rather than stored nodes: a node per miss grows without bound on exactly the workload that
  produces the most misses. Requires `RecallOptions.IncludeDiagnostics`; without it nothing is emitted
  rather than a guess, because "empty" and "never searched" are different questions.

- **A tenant identifier no longer reaches telemetry by default.** `InstrumentedMemoryService` emitted
  `memory.user_id` unconditionally — while every owner-scoped vector search in the codebase already
  tags a **boolean** (`memory.vector.owner_scoped`) rather than the value. A trace backend is usually
  less access-controlled than the database the value came from, retained differently, and often
  exported to a third party.

  Spans now carry `memory.owner_scoped` (bool), so the operational question — *was this scoped?* — is
  still answerable. Hosts that correlate traces by user opt back in:

  ```csharp
  services.AddAgentMemoryObservability(o => o.IncludeOwnerIdInTelemetry = true);
  ```

  The recall span also gains the owner dimension, which it never had.

- **`memory_start_trace` now accepts a `userId` and scopes the trace to it.** It passed no owner at
  all, so an MCP-started trace was written to the shared/global bucket — while trace *recall* goes
  through the ambient owner context. A trace written by one tenant could therefore be invisible to
  that same tenant and visible to every other one. Additive parameter; omitting it behaves as before.

- **A reasoning trace with no recorded outcome is no longer shown to the model as a failure.**
  `ReasoningTrace.Success` is `bool?` and null means *unrecorded* — and null was the common case,
  because `AgentTraceRecorder` had no success parameter at all until recently. `find_similar_tasks`
  rendered `Success == true ? "✓" : "✗"`, so every MAF-recorded trace appeared as a **failed**
  precedent. Now renders three states. A wrong precedent is acted on; an absent one is investigated.

- **The query embedding is no longer generated when nothing will read it.** Every vector search is
  gated on its own `MaxX > 0`; the embedding was not, so a policy that narrowed a turn still paid for a
  provider round trip whose result nothing consumed. Remote-shaped that is the single largest stage of a
  recall (~120 ms), which meant category narrowing **relocated** the cost rather than removing it.
  Gated in `MemoryContextAssembler` (live *and* point-in-time paths, so the Semantic Kernel adapter, the
  CLI and the facade all benefit) and in `Neo4jMemoryContextProvider`, which embeds before handing the
  request over. **Byte-identical at every shipped default**, since no default excludes all five vector
  categories.

## [1.4.1] - 2026-08-11

### Fixed

- **The 1.4.0 starvation fix had a ceiling, and a multi-tenant deployment would have hit it.** 1.4.0
  gave owner-scoped entity, preference and reasoning-trace searches a retry at a wider top-K when the
  first pass returned nothing. That retry is bounded: it multiplies the width by 8 and caps at 2,000
  candidates. **Once more than ~2,000 rows belonging to other owners outrank yours, no amount of
  widening reaches them**, and the search returns nothing again.

  Measured on a 50-owner index, an owner holding 4 facts and asking for 10:

  | competing rows | 1.4.0 | 1.4.1 |
  |---|---|---|
  | 500 | 4 of 4 | 4 of 4 |
  | 3,000 | 4 of 4 | 4 of 4 |
  | **4,000** | **0 of 4** | **4 of 4** |
  | 8,000 | 0 of 4 | 4 of 4 |

  Fixed by adding a final owner-scoped similarity scan, reached **only** when the indexed search and
  its widened retry have both returned nothing. It scores your own rows with
  `vector.similarity.cosine` — a scan, deliberately, but one **bounded by a single owner's data rather
  than by the corpus**, in the case whose alternative is returning nothing at all. Raising the cap
  instead would have moved the ceiling without removing it, and made every rescued query read more of
  other tenants' data.

  Applies to facts, entities, preferences and reasoning traces. The `as-of` variants are unchanged.
  A reasoning-trace search carries its `successFilter` into the fallback, so a filtered search that
  genuinely matches nothing still returns nothing rather than being rescued into a wrong answer.

## [1.4.0] - 2026-08-11

### Added

- **Episodic memory capture, off by default (`AssistantContentMode`).** Extraction has always modelled
  the user and discarded what the assistant said, so a question like "what did you recommend?" had
  nothing to match: the predicate was absent from the graph entirely. `ExtractionOptions.AssistantContent`
  now selects `Ignore` (default, byte-for-byte the previous behaviour), `Utterance` (records the
  assistant's conversational acts — `assistant | recommended | X` — as *what was said*, never asserted
  as true), or `Fact` (treats the assistant's statements as ordinary world facts).

  **Measured before being recommended, and it is not free.** On a 50-question corpus `Utterance` added
  3,048 relations and raised total facts 42%; at retrieval it consumed **32.3% of the structured
  budget across 33 of 50 questions** and **+23.1% answer-prompt tokens**, because the retrieval budget
  is counted in items and an utterance is a wordier fact than a preference. Accuracy did not move.
  LongMemEval asks what the *user* said and did, so it can only ever charge for episodic recall and
  never reward it — which is why the default stays `Ignore` and this is offered as a toggle rather
  than an upgrade.

- **Vector-recall yield telemetry on all eight owner-scoped vector searches.** Fact, entity (live,
  similar-by-embedding, as-of), preference and reasoning-trace searches now emit `owner_scoped`,
  `limit`, `requested_topk`, `effective_topk`, `escalated` and `returned` on the success path, guarded
  so nothing is allocated when no listener is attached. Owner-scoped vector search post-filters a
  *global* top-K, so the querying owner receives only what survives the filter; previously only the
  live fact path reported that. The telemetry was added first as observability only; the behaviour
  change it revealed is in **Fixed**, below, and was made after the measurement rather than alongside
  the instrument.

- **Prospective memory capture, off by default (`TemporalValidityMode`).** `Fact` nodes have always
  carried `valid_from` / `valid_until`, `ExtractedFact` has always exposed them, and the bitemporal
  recall path has always read them — but **no extractor ever populated them and no prompt ever
  mentioned validity**, so the columns were empty on every fact ever stored. Setting
  `LlmExtractionOptions.TemporalValidity` to `Extract` asks the model to record how long a fact holds
  where the conversation states or implies it.

  **Off by default, and the off state is byte-identical**: `Ignore` appends nothing to any prompt.
  **The instruction deliberately tells the model to omit validity rather than guess it** — live recall
  filters on these columns, so a fabricated `valid_until` does not add noise, it removes a memory from
  every future answer. All three extraction paths honour the setting.

### Changed

- **Facts are now identified by canonical keys.** `Fact` nodes carry `subject_key`, `predicate_key`,
  `object_key` and `owner_key`, and upserts MERGE on those rather than on the raw triple. This is what
  makes one relation reachable under all of its stored phrasings.

  **Upgrading an existing database:** call `ISchemaBootstrapper.BootstrapAsync()` before writing, as
  the getting-started guide and every sample already do. It backfills the keys onto existing facts
  idempotently. **If you skip it, an upsert of a fact that already exists will not match it and will
  create a duplicate** — the pre-1.4 rows have no keys to match on. `agentmemory schema-check` now
  reports this state explicitly so it is visible before it causes damage.

- `INeo4jTransactionRunner` implementations that do not also implement `INeo4jAtomicTransactionRunner`
  no longer throw at construction. Persistence degrades to pass-through and reports
  `SupportsAtomicRollback = false` instead of refusing to start.

### Fixed

- **An owner-scoped entity, preference or reasoning-trace search could return NOTHING for an owner
  whose data was present.** These searches ask the vector index for a *global* top-K and then filter to
  the querying owner, so once enough more-similar rows belonging to other owners exist, none of the
  owner's rows survive the filter. The fact path already retried an empty scoped result at a wider
  top-K; the other three did not.

  Measured on a 50-owner index with 500 more-similar foreign rows: the entity search returned **0 of
  the owner's 4 entities** while the fact search returned **4 of 4** on identical data. With the retry
  added, every path returns 4 of 4. Preference and reasoning-trace searches shared the same shape and
  received the same fix.

  The `as-of` variants still issue exactly one query and are unchanged. Their yield telemetry now
  reports the escalation honestly — `escalated` was previously a hardcoded `false` on these paths,
  which was accurate before this fix and would have been a fabricated constant after it.

- **`FindByTriple` asked a different question than the write path answered.** It matched
  `toLower(f.subject)` on all three properties while upserts MERGE on the canonical
  `{subject_key, predicate_key, object_key, owner_key}`. Those are not the same predicate:
  `MemoryTripleCanonicalizer` also collapses whitespace runs and disagrees with Cypher's `toLower` on
  U+0130, so a lookup could find a **different fact than a MERGE would collapse onto**. Now matched on
  the canonical keys. The correctness fix and the performance fix are the same change — measured on
  5.26 with 20,000 facts, the old form planned a `NodeByLabelScan` and the new one plans a
  `NodeIndexSeek` returning one row.

- **Six indexes added, four of them because a composite does not serve a prefix.** `fact_merge_key_idx`,
  `fact_owner_key_idx`, `fact_predicate_key_idx`, `message_session_idx`,
  `message_session_timestamp_idx` and `memory_read_audit_memory_id_idx`. Measured on Neo4j 5.26:
  filtering **all four** columns of a composite plans a seek, while filtering **three of the four
  contiguous** columns plans a full scan, exactly as filtering one does. That is why `owner_key` and
  `predicate_key` exist as single-column indexes despite already appearing inside the composite —
  without them, duplicate detection scans every fact.

- **Unified extraction now honours `EntityTypes`, and refuses the options it cannot honour.**
  `LlmUnifiedMemoryExtractor` read only `UseUnifiedExtraction` and `AssistantContent`, with its entity
  types hardcoded, so enabling it silently dropped a configured `EntityTypes`. It now builds the type
  list from options (byte-identical output at defaults), and enabling it together with any of the four
  per-kind prompt overrides now **fails at startup naming the property**, because one unified prompt
  cannot express four per-kind prompts and ignoring them silently is the worse outcome.


## [1.3.0] - 2026-07-19

### Added

- **Structured ingestion outcomes for partial extraction/persistence failures (#101).** `ExtractionResult`
  gains `Status` (`IngestionStatus`: `Succeeded` / `PartiallySucceeded` / `Failed`) and `Outcomes`
  (`IReadOnlyList<IngestionItemOutcome>`), so a caller can finally distinguish "everything persisted" from
  "3 facts succeeded, 1 entity failed, 2 provenance edges didn't." Best-effort ingestion (continue past
  per-item failures) remains the default via the new `ExtractionOptions.FailureMode` (`IngestionFailureMode.BestEffort`);
  set it to `FailFast` to instead throw `MemoryIngestionException` (carrying every outcome completed
  before the failure) at the first non-recoverable item. Covers extractor failures, entity
  validation/resolution failures, persistence failures, and provenance failures — the last two now
  distinctly staged, since a node can persist successfully while its `EXTRACTED_FROM` edge fails.
  Routine confidence-threshold filtering is deliberately not recorded as an outcome. New
  `memory.ingestion.operations`/`memory.ingestion.items.succeeded`/`memory.ingestion.items.failed`
  metrics (tagged by status/failure-mode/item-kind/stage only — never owner IDs or memory content).
  Fully additive: existing `ExtractionResult` properties and default behavior are unchanged.

- **Pluggable, task-aware automatic recall policy for the Microsoft Agent Framework adapter (#88).**
  `Neo4jMemoryContextProvider` previously ran the same configured `RecallOptions` for every turn with user
  text. The new `IAutomaticRecallPolicy` (`AgentMemory.AgentFramework.Recall`) decides, before anything is
  queried, whether to recall at all, which memory categories to query, and which ranking intent to use.
  `ConfiguredAutomaticRecallPolicy` (the default, registered by `AddAgentMemoryFramework`) reproduces the
  prior behavior exactly. `HeuristicAutomaticRecallPolicy` is a lightweight, deterministic, model-call-free
  alternative — skips recall for a greeting/acknowledgement-only turn, uses `RankingIntent.Latest` for
  recency-oriented phrasing, `RankingIntent.Analog` plus reasoning traces for precedent-oriented phrasing,
  and includes reasoning traces for task/troubleshooting phrasing — opt in via
  `services.AddScoped<IAutomaticRecallPolicy, HeuristicAutomaticRecallPolicy>()`, or supply a fully custom
  policy the same way. It never excludes GraphRAG (no rule about it is defined, so a host's independently
  configured `EnableGraphRag`/`MaxGraphRagItems`/`BlendMode` is always preserved), and its greeting/
  acknowledgement detection is a linear-time tokenizer rather than a regex, to avoid catastrophic
  backtracking on adversarial input. Excluding a category now also skips its `MemoryContextAssembler`
  repository call entirely (both the live and bitemporal recall paths) instead of issuing a zero-result
  query; `AssembleContextAsync` additionally warns if `RetrievalBlendMode.GraphRagOnly` is requested and
  the effective `MaxGraphRagItems<=0`, since that combination would otherwise silently return a completely
  empty context every turn. Fully additive and behavior-preserving by default.

- **Memory-context admission policy: Phase 2 of trust boundaries and prompt-injection defenses (#92).**
  Building on Phase 1's delimiting (#108), `IMemoryContextAdmissionPolicy` (`AgentMemory.AgentFramework.Security`)
  evaluates each recalled-memory ITEM (not category block — see below) across entities, facts, preferences,
  reasoning traces, and GraphRAG, before it's added to the model context. `DefaultMemoryContextAdmissionPolicy`
  runs a lightweight, deterministic `InstructionLikeContentDetector` (a fixed alternation of unambiguous
  phrasings, non-capturing groups, no repeating-group nesting — avoiding the catastrophic-backtracking
  pitfall found in #88's heuristic policy). The new `ContextFormatOptions.SecurityMode` controls the
  outcome: `Permissive` (default) still includes flagged content — every admitted item is delimited/escaped
  regardless, and is now also logged at Debug level for observability — since detection is necessarily
  heuristic and a false positive must never silently discard genuine stored information; `Strict` excludes
  flagged items entirely, logging the category and reason at Warning (never the content). Evaluation is
  per-item rather than per-block specifically so one flagged fact/entity/preference/trace can't silently
  drop every other, unrelated item joined into the same rendered category. Fully additive; default
  behavior is unchanged. Issue #92 remains open — configurable message roles and observed/inferred/verified
  knowledge distinctions are future phases.

- **Trust-metadata foundation: Phase 3 of trust boundaries and prompt-injection defenses (#92).** New
  `MemoryTrustLevel` (`AgentMemory.Abstractions.Domain`: `Untrusted` < `UserProvided` < `ModelGenerated` <
  `ToolDerived` < `VerifiedExternal` < `ApplicationTrusted`) plus `MemoryTrustMetadataExtensions.GetTrustLevel()`/
  `WithTrustLevel(...)`, which read/write the level from/to the `Metadata` dictionary every `Entity`/`Fact`/
  `Preference`/`ReasoningTrace` already carries — no new schema property, no migration; `Metadata` already
  round-trips through Neo4j as a serialized JSON property, so trust level rides along for free.
  `ExtractionOptions.DefaultTrustLevel` (default `UserProvided`) is stamped on everything `PersistenceStage`
  persists, unless a specific call's new `ExtractionRequest.TrustLevel` overrides it — e.g. importing a
  curated/verified document as `ApplicationTrusted`/`VerifiedExternal` for that one call.
  `DefaultMemoryContextAdmissionPolicy` gains a bypass: an item at or above the new
  `ContextFormatOptions.MinimumTrustForAdmissionBypass` (default `ApplicationTrusted`, the highest level) skips
  instruction-like-content evaluation entirely — this is what "applications can explicitly mark controlled
  sources as trusted" means in practice. Fully additive and behavior-preserving by default: nothing bypasses
  unless a host both raises an item's trust level and explicitly reaches the configured threshold. **Known,
  disclosed limitation:** stamping is per extraction *request*, not per extracted *item* — today's extractors
  return items with no attribution to a specific source message, so distinguishing "the user said this" from
  "the assistant said that" within the same turn needs deeper extractor changes, out of scope here.
  `ReasoningTrace` is not stamped by this phase (recorded via a separate mechanism, `AgentTraceRecorder`) and
  defaults to `Untrusted`. Trust is monotonic for entities: re-resolving onto an already-persisted entity
  (auto-merge/SAME_AS) takes the higher of its existing trust level and the current call's, so an unrelated
  later low-trust mention can never silently erase a deliberate earlier elevation (facts/preferences don't
  have this protection yet — same known limitation as the per-item stamping gap above). **Security fix
  applied before merge:** `trust_level` lives in the same `Metadata` dictionary already writable through
  pre-existing paths (`memory_add_fact`'s `metadataJson`, `IReasoningMemoryService.StartTraceAsync`), so
  without a guard, any caller of those APIs could self-assign `ApplicationTrusted` and fully bypass the
  admission policy. New `MemoryTrustMetadataExtensions.WithoutCallerSuppliedTrustLevel()` strips any
  caller-supplied `trust_level` before combining with a framework-assigned value; both call sites now apply
  it. Issue #92 remains open — configurable message roles, observed/inferred/verified knowledge
  distinctions, and richer telemetry are future phases.

- **Configurable recall message role: Phase 4 of trust boundaries and prompt-injection defenses (#92).**
  Phases 1-3 addressed what the model is told about recalled memory (delimiting, admission, trust levels)
  but not how much authority it's given: `MafTypeMapper.ToContextMessages` always rendered every admitted
  block as `ChatRole.System`, regardless of trust level. New `RecalledMemoryMessageRole` enum
  (`System`/`User`) plus `ContextFormatOptions.DefaultMemoryRole` (default `System`) and
  `ContextFormatOptions.MinimumTrustForSystemRole` (default `Untrusted`, the lowest level, so every item
  meets it and rendering is unchanged unless a host raises the threshold). A recalled item at or above the
  threshold renders at `DefaultMemoryRole`; everything else renders as `ChatRole.User` instead. Granularity
  is per-item, not per-category-block: a single `ApplicationTrusted` entity/fact/preference/trace bundled
  alongside several lower-trust ones now renders in its own message at the higher-authority role, while the
  rest render in a separate message at the lower one — the same per-item split Phase 2's admission
  evaluation already established, for the same reason (one item's trust shouldn't determine an unrelated
  item's authority). GraphRAG has no per-item trust signal and is always evaluated at `Untrusted`, so it
  only moves off `DefaultMemoryRole` when a host raises the threshold above the default. Fully additive and
  behavior-preserving by default. Bundled: a live-Neo4j end-to-end test proving the acceptance criterion
  open since Phase 1 — content stored in one session that reads as an instruction ("stored prompt
  injection") never resurfaces in a later, unrelated session as an unattributed `ChatRole.System` message,
  once a host configures `MinimumTrustForSystemRole` above the default extraction trust level. Issue #92
  remains open — per-item (not per-request) trust attribution, monotonic trust for facts/preferences,
  `ReasoningTrace` trust stamping, observed/inferred/verified knowledge distinctions, and richer telemetry
  are future phases.

- **Monotonic trust for facts on re-extraction: Phase 5 of trust boundaries and prompt-injection defenses
  (#92).** Phase 3 made entity trust monotonic but explicitly deferred the same protection for facts and
  preferences, since neither had an equivalent pre-fetch step. `PersistenceStage` now pre-fetches any
  existing fact with the same `{subject, predicate, object}` triple (via `IFactRepository.FindByTripleAsync`)
  before persisting, and takes the higher of its existing trust level and the current call's — so an
  ordinary, later, lower-trust re-extraction of an identical triple (e.g. a curated `ApplicationTrusted`
  import later re-stated in a normal chat turn) can no longer silently erase the earlier elevation.
  Deliberately scoped to **owner-scoped facts only** (a null-or-empty `ownerId` skips the pre-fetch
  entirely): `FindByTripleAsync`'s `MemoryScope?` parameter treats an owner-less lookup as "search across
  every owner" (the read/recall convention) rather than "shared bucket only" (the write convention for a
  `null` owner), so pre-fetching for a shared/global fact would risk adopting a *different* owner's trust
  level — a cross-tenant leak. No existing repository primitive supports a safe shared-bucket-only lookup,
  so shared/global facts keep today's fresh-stamp behavior; a disclosed, narrower-than-ideal limitation,
  not a silent gap. The owner-scoped pre-fetch itself also excludes shared facts (`includeShared: false`,
  the opposite of `MemoryScope.For`'s own default) — self-review caught that the default would let a
  shared fact sharing the same triple be returned instead of this owner's own record (no `ORDER BY` before
  `FindByTriple`'s `LIMIT 1`), grafting an unrelated shared record's entire metadata onto an owner-scoped
  fact. Also fixed during self-review: a same-triple, different-casing re-extraction now persists under
  the *existing* record's casing (found via `FindByTripleAsync`'s case-insensitive match) rather than the
  freshly-extracted casing, since `Upsert`'s Cypher `MERGE` key is exact-string and would otherwise create
  a duplicate node. **Preferences turned out not to need this fix**: unlike facts, their extraction-time
  MERGE key is a freshly-generated id, not a natural key, so they never collide with an existing node on
  re-extraction in the first place — correcting an earlier, less precise description that grouped facts
  and preferences together. Proven live-Neo4j (not just at the mocked-repository unit level): the same
  triple is extracted twice at two different trust levels, and the surviving node — confirmed to be the
  same node, not a duplicate — keeps the higher one. Issue #92 remains
  open — per-item (not per-request) trust attribution, `ReasoningTrace` trust stamping, observed/inferred/
  verified knowledge distinctions, and richer telemetry are future phases.

- **Semantic Kernel adapter trust boundaries: Phase 6 of trust boundaries and prompt-injection defenses
  (#92).** A post-Phase-5 holistic audit — a fresh, whole-subsystem read, not a single phase's own
  self-review, which only ever sees that phase's diff — found that `AgentMemory.SemanticKernel`'s
  `Neo4jMemoryPlugin`/`MemoryContextFormatter` had none of Phases 1-3's protections: every recalled entity/
  fact/preference/GraphRAG block rendered as plain, unescaped Markdown text, with no delimiting, no
  instruction-like-content admission, and no trust-level awareness — a completely separate code path from
  `MafTypeMapper` that no earlier phase had touched. Phase 6 closes this gap by reusing rather than
  duplicating the relevant logic: `InstructionLikeContentDetector` and a new `RecalledMemoryDelimiter`
  (extracted from `MafTypeMapper`'s escaping logic) moved from `AgentMemory.AgentFramework.Security` into
  `AgentMemory.Core.Security` — both were already adapter-agnostic and already internals-visible to both
  adapter packages, so no project references changed and this is not a public-API change.
  `MemoryContextFormatter.FormatRecallResult` gained an optional internal options parameter (`Strict`,
  `MinimumTrustForAdmissionBypass`) that delimits every recalled block (Phase 1), evaluates each item for
  instruction-like content (Phase 2), and lets a trust-level bypass survive Strict mode (Phase 3) — reusing
  the already-shared `MemoryTrustLevel`. New public surface in `AgentMemory.SemanticKernel`:
  `MemoryContextSecurityMode` (a distinct type from the Agent Framework adapter's enum of the same name —
  duplicating a two-value enum is far lower risk than relocating an already-public type across a
  SemVer-locked package boundary) and `MemoryRecallSecurityOptions`, wired into `Neo4jMemoryPlugin`'s
  constructor and `KernelMemoryExtensions.AddNeo4jMemoryPlugin` (both via additive, optional parameters).
  Role/authority (Phase 4) does not apply here — `RecallAsync` returns a plain string, not `ChatMessage`s
  with a role. Every recalled block is now wrapped in a `<recalled_memory category="...">` tag regardless
  of mode — a real, disclosed output-format change (new text that wasn't there before), even though
  non-flagged content's substance is otherwise unchanged by default. **A second recall-to-text surface
  found during self-review, after the initial fix:** `Neo4jTextSearch`'s `GetTextSearchResultsAsync`/
  `GetSearchResultsAsync` built `TextSearchResult`s directly from raw text, entirely bypassing
  `MemoryContextFormatter` — only the sibling `SearchAsync` inherited protection for free. Fixed with the
  same per-item delimiting/admission, reusing a new shared `RecalledMemoryAdmission.ShouldAdmit` helper
  (also `AgentMemory.Core.Security`) instead of a third copy of the same decision logic;
  `Neo4jTextSearch`'s constructor and `KernelMemoryExtensions.AddNeo4jTextSearch` both gained the same kind
  of optional `MemoryRecallSecurityOptions` parameter. `MemoryContextFormatter`'s three near-identical
  `AppendEntities`/`AppendFacts`/`AppendPreferences` methods were also collapsed into one generic
  `AppendCategory<T>` helper, matching `MafTypeMapper`'s existing `CategoryMessages<T>` pattern. Issue #92
  remains open — per-item (not per-request) trust attribution, monotonic trust for shared/global facts,
  `ReasoningTrace` trust stamping, observed/inferred/verified knowledge distinctions, and richer telemetry
  are future phases.

- **Recalled-message role gating: Phase 7 of trust boundaries and prompt-injection defenses (#92).** The one
  gap disclosed since Phase 1 and left open through Phases 2-6: recalled conversation history
  (`RecentMessages`/`RelevantMessages`) keeps whatever role it was persisted with, with no delimiting,
  admission, or trust gating at all. Phase 7 found this is not merely theoretical: the `memory_store_message`
  MCP tool and `Neo4jMemoryPlugin.AddMessageAsync` (a model-invokable Semantic Kernel function) both accept
  an unvalidated, caller-supplied `role` string with zero validation anywhere in the write path — a
  prompt-injected agent could call either with `role: "system"` and arbitrary content, and have it replay
  moments later, same session, as a genuine, undelimited `ChatRole.System` message (MAF) or an unescaped
  `[system]: ...` line (Semantic Kernel). Fixed on both sides: `memory_store_message`/`AddMessageAsync` now
  stamp `MemoryTrustLevel.ToolDerived` on every message they persist (matching `memory_add_fact`'s
  precedent) without restricting which role a caller may set; a new
  `AgentMemory.Core.Security.RecalledMessageRoleGate` (shared by both adapters) demotes a message's role to
  `"user"` when it's privileged (`"system"`/`"tool"` only — ordinary `"user"`/`"assistant"` turns are never
  touched) and its trust level doesn't meet a new `MinimumTrustForSystemRole` threshold
  (`ContextFormatOptions`, reusing the Phase 4 property; `MemoryRecallSecurityOptions`, new) — both default
  to `Untrusted`, so rendering is unchanged unless a host raises the threshold. Message *content* stays
  deliberately undelimited/unevaluated (a disclosed, narrower gap — this is genuine recalled transcript, not
  a "memory object"), and gating is applied only inside recalled-context assembly, never on
  `MafTypeMapper.ToChatMessage` itself (which `Neo4jChatMessageStore`/`Neo4jChatHistoryProvider` still rely
  on, unchanged, for genuine chat-history replay). **Self-review found and fixed three more issues in the
  same pass:** (1) `Neo4jMicrosoftMemoryFacade.GetContextForRunAsync` — a third call site with the exact same
  semantic-query-plus-`RelevantMessages` shape, used by the bundled samples — called the raw, ungated
  `MafTypeMapper.ToChatMessage` directly and was missed by the initial fix; now gated the same way. (2)
  `RecalledMessageRoleGate.IsPrivileged` now trims the role before comparing, closing a whitespace-bypass gap
  (`" system"` previously read as non-privileged and skipped demotion entirely). (3) **Companion fix:**
  `Neo4jTextSearch.SearchAsync` never actually passed its configured security options into
  `MemoryContextFormatter.FormatRecallResult` at all — a Phase 6 wiring gap, silently using hardcoded
  defaults regardless of host configuration; fixed alongside this phase, then corrected again when
  self-review found the fix itself cached that mapping once at construction while sibling methods read the
  live, mutable options object — now mapped fresh per call via a shared
  `MemoryRecallSecurityOptionsExtensions.ToFormatterOptions()` helper (also adopted by `Neo4jMemoryPlugin`,
  removing a second hand-duplicated copy of the same mapping). Issue #92 remains open — per-item (not
  per-request) trust attribution, monotonic trust for shared/global facts, `ReasoningTrace` trust stamping,
  observed/inferred/verified knowledge distinctions, and richer telemetry are future phases.

- **NAMS (Neo4j Agent Memory as a Service) backend support (#128) — a new, opt-in hosted-backend alternative
  to the direct Neo4j implementation.** Three new additive packages: `AgentMemory.Nams` (a dedicated `HttpClient`-based
  REST client against the NAMS SaaS API — `AddNamsAgentMemory()` — with identity/conversation-resolution,
  recall, and post-turn persistence subsystems, zero dependency on Core/Neo4j), `AgentMemory.AgentFramework.Nams`
  (`NamsMemoryContextProvider`, a dedicated Microsoft Agent Framework adapter wiring NAMS's recalled content
  through the same #92 trust-boundary protections — delimiting/escaping, admission policy, trust-level mapping
  — the direct backend already applies), and `AgentMemory.McpServer.Nams` (11 MCP tools across a two-tier read/
  write opt-in split: `nams_recall`/`nams_remember` for automatic-recall-equivalent access, plus entity-graph
  (`nams_entity_graph`/`nams_expand_graph`/`nams_create_entity`/`nams_entity_feedback`) and reasoning/provenance
  (`nams_record_reasoning_step`/`nams_record_tool_call`/`nams_list_reasoning_steps`/`nams_reasoning_trace`/
  `nams_entity_provenance`) tools resolving `INamsClient` directly). A new sample,
  `AgentMemory.Sample.NamsAgent`, demonstrates the full recall/persistence lifecycle against a real, live NAMS
  SaaS workspace with no local embedding generator or schema bootstrapper needed. Verified against the actual
  official upstream `neo4j-labs/agent-memory-tck` conformance suite via a new `tools/AgentMemory.TckBridge.Nams`
  bridge — **10/11 Platinum scenarios pass** (the 1 failure is a confirmed bug in the upstream TCK's own test
  model, not fixable by any bridge). Deliberately excluded: `nams_graph_query` (raw Cypher passthrough via MCP)
  — `INamsClient.ExecuteCypherQueryAsync` exists at the client layer, but exposing it as an agent-callable tool
  needs an explicit, separate decision, the same gate this project applies to its own preview-release cut.
  Fully additive: existing direct-Neo4j behavior is completely unchanged, and NAMS is a purely opt-in backend
  choice.

## [1.2.0] - 2026-07-15

### Added

- **`AgentFrameworkOptions.ExposeMemoryToolsFromContextProvider` — optional memory-tool exposure via
  `AIContext.Tools` (#86).** `Neo4jMemoryContextProvider` can now surface the six standard memory tools
  (`MemoryToolFactory.CreateAIFunctions()`) itself through `AIContext.Tools`, so
  `AIContextProviders = [memoryProvider]` alone is enough to give an agent LLM-callable memory tools — no
  separate `ChatOptions.Tools = [.. memoryTools]` wiring required. **Defaults to `false`**:
  `AddAgentMemoryFramework` registers `MemoryToolFactory` unconditionally, and its tools include
  write-capable ones (`remember_fact`, `remember_preference`), so exposure must stay opt-in rather than
  firing just because the factory is present in DI. Every `BuildContextAsync` branch (a recall hit, an
  empty recall, a recall failure, or no user message at all) now shares one `AIContext`-construction
  helper, so tool availability never silently drops on a quiet turn.
- **`ContextFormatOptions.MaxChatHistoryMessages`** (#91, see Fixed below) — the new name for what was
  `MaxContextMessages`.

### Fixed

- **`AddGraphRagAdapter()` could never actually resolve `IGraphRagContextSource` — `IDriver` was never
  registered in DI.** `Neo4jGraphRagContextSource` takes `IDriver` via constructor injection, but
  `AddNeo4jAgentMemory` only registered `INeo4jDriverFactory` (which wraps `IDriver` via `GetDriver()`),
  never `IDriver` itself. Any host that called `AddGraphRagAdapter()` hit
  `InvalidOperationException: Unable to resolve service for type 'Neo4j.Driver.IDriver'` the first time
  GraphRAG retrieval ran — this broke the feature for every consumer, not just the sample that surfaced
  it. Fixed by registering `IDriver` from the existing `INeo4jDriverFactory` singleton in
  `AddNeo4jAgentMemory`, so both resolve to the same underlying driver instance/lifetime.
- **`ContextFormatOptions.MaxContextMessages` renamed to `MaxChatHistoryMessages`, with the old name kept
  as an `[Obsolete]` compatibility alias (#91).** The option was documented as capping the complete
  injected context, but the implementation always preserved the context prefix and every memory-derived
  block (entities/facts/preferences/reasoning traces/GraphRAG) — only recalled chat history was ever
  truncated to fit. The renamed option's implementation now matches its name: it bounds *only* recalled
  chat history, and is no longer reduced by the prefix/memory-block count. `MaxContextMessages` still
  works (it forwards to `MaxChatHistoryMessages`) but is marked obsolete. Negative values are rejected by
  option validation; `MaxChatHistoryMessages = 0` means no recalled chat history, but memory blocks may
  still be included. `Neo4jChatHistoryProvider` (a separate, unrelated consumer of the same option field)
  is updated to reference the new name. Use `ContextBudget.MaxTokens`/`MaxCharacters` for a hard cap on
  total prompt size — a message count alone is not a reliable token budget.

### Changed

- **Every sample now calls a real Azure OpenAI chat and/or embedding model — no mocks.** Removed
  `EchoChatClient`/`ShoppingEchoChatClient`/`StubEmbeddingGenerator` from every sample
  (AgentWithMemory, RealAgent, MemoryToolsAgent, ChatHistoryProvider, ShoppingAssistant, BlendedAgent,
  MinimalAgent, McpHost, and the AspireDemo app). A new `samples/AgentMemory.Samples.Shared` project
  centralizes the wiring: `RealAzureOpenAI.TryCreate` resolves `AZURE_OPENAI_*` environment variables and
  fails fast with setup instructions if they're missing (no silent mock fallback), `MemoryTraceChatClient`
  prints the `<recalled_memory>` context the provider injects before each live model call, and
  `SampleConsole` gives each sample color-coded user/assistant/memory-action console output. The five
  agent samples now let the model decide on its own when to call memory/product tools, rather than
  scripting the calls a mock model couldn't make itself.

## [1.1.0] - 2026-07-15

### Added

- **`IMemoryIsolationPolicy` — a library-wide strict multi-tenant isolation mode (#100, Stage 1 + Stage
  2).** A new central policy abstraction with three modes via `MemoryOptions.Isolation.Mode`:
  `SingleTenant` (default, today's exact backward-compatible behavior), `WarnOnUnscoped` (structured log
  when a tenant-facing call resolves unscoped), and `StrictMultiTenant` (throws
  `MemoryOwnerScopeRequiredException` before any Neo4j call when no owner scope is present, instead of
  silently falling back to global/shared). Stage 1 wired the primary recall/extraction/reasoning Core
  services and 6 MCP entry points. **Stage 2 completes coverage of every remaining tenant-facing MCP tool**
  by routing `LongTermMemoryService` (every entity/fact/preference/relationship read and write),
  `Neo4jGraphRagContextSource` (GraphRAG retrieval), `ConversationTools`, and
  `EntityTools.MemoryGetEntityProvenance` through the same policy — all 15 MCP entry points that accept a
  `userId` now fail closed on an omitted owner under `StrictMultiTenant`. `ConversationTools`' owner check
  remains a post-hoc in-memory filter rather than Cypher-level scoping (`IConversationRepository` has no
  `MemoryScope`-aware query method) — disclosed, not silent. A real cross-owner administrative access path
  (`MemoryOperationAccess.Administrative`) is explicitly descoped from #100's acceptance criteria: it has
  zero call sites in the library today and needs a concrete design and consumer before being built, not a
  rushed addition here. See `docs/getting-started.md` and `docs/security/threat-model.md` TT-01/TT-02 for
  the exact coverage boundary.
- **`MemoryOwnerScopingAgent` / `AIAgent.WithMemoryOwnerScoping(...)` — guaranteed owner scoping across
  the complete MAF invocation (#90).** Wrapping an agent with `.WithMemoryOwnerScoping(serviceProvider)`
  guarantees the owner scope spans passive recall, the model call, the full tool-calling loop, and
  automatic persistence as one unbroken async chain — closing a real gap where `Neo4jMemoryContextProvider`'s
  own pre-run hook could not, on its own, guarantee a value it set survived into tool calls that run after
  it returns (the hook suspends on real I/O, breaking `AsyncLocal` propagation). Replaces manually
  wrapping every `agent.RunAsync(...)` call in `ownerContext.BeginOwnerScope(userId)`. Also adds
  `AgentSessionMemoryExtensions.GetMemoryIdentity(...)` (single-source-of-truth session-identity reader)
  and `IWritableMemoryStoreContext.BeginStoreScope(...)` (mirrors the existing `BeginOwnerScope`).
- **A minimal trust boundary for recalled memory rendered into MAF context (#92, Phase 1 of a larger
  issue).** Recalled entities/facts/preferences/reasoning-traces/GraphRAG content is no longer injected as
  a raw, unrestricted system message: each block is now delimited and angle-bracket-escaped
  (`<recalled_memory category="...">...</recalled_memory>`), and the default context prefix explicitly
  tells the model this content is untrusted reference data, not instructions to follow. This defeats
  boundary forgery specifically; it does not (yet) detect general instruction-like content or cover
  recalled conversation history — the full #92 issue (trust metadata, an admission policy, configurable
  message roles, instruction-like-content detection) remains open.

### Fixed

- **GraphRAG retrieval could run unscoped, or inconsistently fail, when a caller scoped recall via
  `RecallOptions.Scope` alone (#100 Stage 2, found during adversarial review).** `MemoryContextAssembler`
  resolves the authoritative owner scope once (explicit `RecallOptions.Scope` wins over `RecallRequest.UserId`)
  but GraphRAG retrieval was separately built from the raw, unresolved `UserId` — so a caller who set only
  `RecallOptions.Scope` got memory correctly scoped to that owner while GraphRAG silently searched across
  all owners (a cross-owner leak in non-strict modes) or threw inconsistently under `StrictMultiTenant`
  (since the pipeline had already accepted the recall as properly scoped). GraphRAG now uses the same
  already-resolved scope as every other recall source.
- **Native MAF recall ignored configured `RecallOptions` (#87).** `Neo4jMemoryContextProvider` built its
  `RecallRequest` without assigning `.Options`, so a customized `MinSimilarityScore`, per-section limit,
  or `BlendMode` had no effect on automatic recall even though it worked correctly for a direct
  `IMemoryService.RecallAsync` call. Configured retrieval tuning now reaches native recall; a configured
  `RecallOptions.Scope` is explicitly never allowed to override the invocation's authenticated owner.
- **Automatic extraction only saw the assistant's response, never the user's request (#89).** A turn like
  "User: I prefer window seats. / Assistant: Got it." previously only ran extraction over "Got it." — a
  user-stated preference was invisible unless the assistant repeated it. Extraction now considers the
  complete turn. `Neo4jChatHistoryProvider` (which already persisted both request and response messages)
  gained full-provenance extraction from both at no new risk. `Neo4jMemoryContextProvider` deliberately
  does not start persisting request messages as new `:Message` nodes — request-message persistence
  ownership intentionally stays solely with `Neo4jChatHistoryProvider` — so captured facts/preferences are
  correct, but their provenance link to the literal source message is best-effort in that path, not
  guaranteed; documented, not silently accepted.
- **Enabling more than one MAF message-persisting component could duplicate `:Message` nodes (#89).**
  `Neo4jChatHistoryProvider` now narrows MAF's default request-message filter to
  `AgentRequestMessageSourceType.External` only, so it no longer re-persists another configured
  `AIContextProvider`'s (e.g. `Neo4jMemoryContextProvider`'s) injected recalled-memory messages as new
  nodes every turn — a real, previously-undiscovered bug found while investigating this issue, not merely
  one of its stated acceptance criteria. On the response side, message persistence is now idempotent by
  id: `Neo4jMessageRepository`'s writes are `MERGE`-by-id instead of `CREATE` (a no-op, first-write-wins,
  for a repeated id — safe because `message_id` already has a uniqueness constraint, and no existing
  caller ever supplied a repeated id), and `Neo4jMemoryContextProvider`/`Neo4jChatHistoryProvider`/
  `Neo4jChatMessageStore` now persist a response message under a deterministic id derived from the
  underlying `ChatMessage.MessageId` when the `IChatClient` populates one — so two of these components
  observing the same response converge on one node instead of duplicating it. When the client doesn't
  populate `MessageId`, today's fresh-id behavior remains (no regression, but the gap remains for that
  client) — a disclosed limitation, not a silent one; see `docs/agent-framework.md`. Also explicitly
  documents the non-text-content policy for `Neo4jMemoryContextProvider`/`Neo4jChatHistoryProvider`: a
  message carrying only function/tool calls, function/tool results, or reasoning content is excluded from
  both persistence and extraction in those two providers (previously true as a side effect of the
  empty-text filter, now an explicit, tested contract for them specifically — the lower-level
  `Neo4jChatMessageStore`/`Neo4jMicrosoftMemoryFacade` path does not have this guard and persists every
  message it's given). #89 is now fully closed.

## [1.0.3] - 2026-07-14

### Fixed

- **The README logo did not render on the NuGet gallery.** 1.0.2 embedded it as raw HTML
  (`<p align="center"><img ... /></p>`), but NuGet.org's README renderer escapes raw HTML instead of
  rendering it — the tag showed up as literal text on the package page instead of an image. Replaced with
  standard Markdown image syntax (`![alt](url)`), which renders correctly on both GitHub and NuGet.org.
  Documentation-only; no code or API change.

## [1.0.2] - 2026-07-14

### Added

- **Multi-target net8.0 and net10.0 alongside net9.0** for all 12 publishable packages. Every referenced
  dependency (Neo4j.Driver, Microsoft.Extensions.AI.Abstractions, Microsoft.Agents.AI.Abstractions,
  Microsoft.SemanticKernel, ModelContextProtocol) ships binaries for all three TFMs at the exact pinned
  versions used here; verified with real builds and executed tests on all three, not just compiled. Lets
  consumers on .NET 8 LTS or the newest .NET 10 use the library without adopting a different runtime.
  Purely additive — no public API change.
- **NuGet package icon and README logo.**
- **A prominent "Memory Governance" section in the README** answering ownership, provenance, temporal
  history, recall auditability, invalidation/deletion, tenant isolation, and retention/privacy — each
  grounded in a real, verified mechanism (`owner_id`/`MemoryScope`, `source_message_ids` +
  `EXTRACTED_FROM`/`EXTRACTED_BY`, bitemporal valid/transaction time, `:MemoryReadAudit`,
  non-destructive-by-default decay).
- **`GoldenPathDocumentationTests`** — compiles and executes the exact MAF registration shown in
  `docs/agent-framework.md`, so a future signature change that breaks the doc sample fails a test instead
  of shipping silently.

### Changed

- **Internal planning docs moved out of the published tree.** `CONTINUE-HERE.md`, `loop.md`, and the
  `docs/ROADMAP.md`, `docs/DOING-RIGHT-NOW.md`, `docs/Improvement-Ideas-Backlog.md`, `docs/design.md`,
  `docs/nextsteps.md`, `docs/core/`, `docs/archive/`, `docs/reviews/`, and `docs/reference/` sets now
  live in a local, gitignored `strategy/` folder rather than the repository. `docs/` keeps only the
  user-facing reference set: `getting-started.md`, `architecture.md`, `agent-framework.md`, `schema.md`,
  and `specification.md`. The root README now leads with `docs/getting-started.md`. Documentation-only;
  no code or API change.
- **README rewritten as a short, marketing-led introduction.** Dropped the version-pinned Status section,
  the full package-topology table, and the isolation-model internals in favor of a concise "Why" list and
  Quick Start; deeper detail lives in `docs/` already. No claims added beyond what the docs substantiate.
- **"Port"/"ported" wording replaced with "reimplementation"** throughout the README and samples — the
  library shares no code with the upstream Python project; it's an independent .NET implementation
  verified against the same schema and compatibility kit.
- **Internal agent-orchestration machinery (`.squad/`) untracked** from the repository (152 files) and
  gitignored; files remain on disk locally. Not part of the published project.

### Fixed

- **The MAF golden-path code sample in `docs/agent-framework.md` didn't actually compile as written.** It
  imported the `AgentMemory` meta-package namespace but called the low-level single-delegate
  `AddNeo4jAgentMemory` overload (from a different namespace) plus a separate `AddAgentMemoryCore()` — the
  two same-named overloads don't mix. Corrected to the one meta-package call
  (`configureMemory`/`configureNeo4j`), which already registers Core internally. Also fixed a matching
  namespace/parameter-name mismatch in `docs/getting-started.md`'s `DatabasePerApplication` example.
- **`docs/architecture.md` listed an internal squad-persona name ("Deckard") as author** in three places;
  corrected to the real author.
- **`tools/AgentMemory.TckBridge/README.md` claimed the Gold and Platinum TCK tiers were unimplemented.**
  Gold (18/18) actually shipped in PR #74 and its endpoints exist in `Program.cs`; corrected to the real
  state (178/178 across Bronze, Silver, and Gold — only Platinum remains).

## [1.0.1] - 2026-07-13

### Fixed

- **Packaged README links now resolve on the NuGet gallery.** The README is embedded in every package, but its repo-relative links (`docs/`, `CONTINUE-HERE.md`, `LICENSE`, `CONTRIBUTING.md`, …) do not resolve on nuget.org, so they rendered as broken links on each package's gallery page. They are now absolute `https://github.com/joslat/agent-memory-dotnet/...` URLs. Documentation-only; no code or API change.

## [1.0.0] - 2026-07-13

### Fixed

- **Entity merge now re-points every typed relationship onto the surviving entity.** `MergeEntitiesAsync` previously transferred only `MENTIONS` and `SAME_AS` edges, orphaning all semantic `RELATED_TO` relationships (e.g. `WORKS_AT`, `LOCATED_IN`) on the tombstoned source. It now moves every `RELATED_TO` edge — both outgoing and incoming — onto the target with all properties (including the stable relationship id) preserved, dropping only edges that would collapse into a `target→target` self-loop. The move is non-destructive: a real relationship is never hard-deleted (duplicate same-typed edges are left for the consolidation layer, so temporally-distinct facts and their provenance survive). A self-merge (same id for source and target) is now guarded as a no-op instead of tombstoning the entity and destroying its own relationships.
- **MCP `memory_get_observations` could not resolve at runtime.** `IContextCompressor` was injected into the MCP observation tool but registered by no `AddX`. It is now bound in `AddAgentMemoryCore`, and `ContextCompressor`'s `IChatClient` dependency is optional — with no LLM registered it degrades to a verbatim passthrough, so the binding is always safe to resolve (it does not break LLM-less consumers under `ValidateOnBuild`).
- **The Agent Framework chat-message store no longer fabricates a fake success on a failed write.** `Neo4jChatMessageStore.AddMessageAsync` previously caught any persist error and returned a plausible-looking `Message`, silently hiding data loss and letting the extraction step run over messages that were never stored. It now surfaces the failure (the facade's `PersistAfterRunAsync` catches and logs it at the run boundary).
- **`Neo4jMicrosoftMemoryFacade.GetContextForRunAsync` now owner-scopes recall.** The read path never set `RecallRequest.UserId` while the write path took a `userId`; a new optional `userId` parameter threads owner scope into recall so a multi-tenant host does not recall across owners.
- **`IToolCallRepository.UpdateAsync` no longer throws on a concurrently-removed tool call.** A session clear / trace prune between a read and the update yields no row; it now returns `null` (see Changed) instead of a sequence-empty exception.
- **`GdsAnalyticsOptions.DefaultTopN` is validated (> 0) on start**, and `MemoryPageRankService.RankEntitiesAsync` rejects a non-positive `topN` — a non-positive value previously flowed unchecked into a Neo4j `LIMIT`.

### Added

- **`IMemoryHistoryService` — normalized long-term memory lifecycle/history reads.** New owner-scopeable service (`GetHistoryAsync(MemoryHistoryQuery)`) returning `MemoryHistoryRecord` rows across facts, entities, and preferences: lifecycle timestamps (created / updated / invalidated / last-accessed), status (`MemoryHistoryStatus.Live`/`Invalidated`), supersession links, provenance source-message ids, access + read-audit counts, valid-time window, and owner scope — ordered by most-recent activity. New public types `MemoryHistoryQuery`, `MemoryHistoryRecord`, and enums `MemoryHistoryKind {Entity, Fact, Preference}` / `MemoryHistoryStatus {Live, Invalidated}` in `AgentMemory.Abstractions`; backed by an internal Neo4j implementation registered by `AddNeo4jAgentMemory`.
- **Read-audit trail for long-term memory recall (`:MemoryReadAudit`).** Recall now records a `:MemoryReadAudit` node per accessed memory (`memory_id`, `kind`, `owner_id`, `read_at`, `access_count`), surfaced through `MemoryHistoryRecord.ReadAuditCount` / `LastReadAuditAtUtc`. The schema bootstrapper gains one node label (`MemoryReadAudit`), one uniqueness constraint (`memory_read_audit_id`), and one property index (`memory_read_audit_kind_idx`) — an existing store should re-run `agentmemory bootstrap` (or `migrate`) and can confirm with `schema-check`.
- **`IEntityRepository.MergeEntitiesAsync(sourceEntityId, targetEntityId, scope?, cancellationToken)` is now declared on the public interface** (previously only on the concrete `Neo4jEntityRepository`). It returns `Task<bool>` and re-points every typed relationship onto the survivor (see Fixed). Third-party `IEntityRepository` implementers must now implement it.
- **New `agentmemory` CLI verbs.** `history [--type <fact|entity|preference>] [--id <id>] [--owner <id>] [--limit N] [--live-only]` reads long-term memory lifecycle history (over `IMemoryHistoryService`; includes soft-invalidated rows by default, `--live-only` narrows). `evaluate` runs the deterministic memory-layer evaluation harness (persistence / retrieval / isolation / temporal-history scenarios), writing JSON reports under `artifacts/evaluation/`.
- **Diffbot enrichment is now registerable as a keyed `IEnrichmentService`.** `AddDiffbotEnrichment` registers Diffbot behind the `IEnrichmentService` abstraction under the new `EnrichmentServiceKeys.Diffbot` key, wrapped in the shared cache decorator and decorated for observability like the default (Wikimedia) provider. It is deliberately **opt-in**: because it registers *by key*, Diffbot does not participate in the automatic background enrichment queue (which resolves the unkeyed `IEnumerable<IEnrichmentService>`), so a paid API never fires on every enqueued entity — resolve it explicitly via `GetRequiredKeyedService<IEnrichmentService>(EnrichmentServiceKeys.Diffbot)` or `[FromKeyedServices]`. `DiffbotEnrichmentService` now depends on `IHttpClientFactory` (per-request named client) so its handler rotates on the factory's schedule instead of being pinned for the process lifetime by the keyed singleton.
- **`IReasoningMemoryService.ListAllTracesAsync` — owner-scoped, paged, cross-session trace listing.** Returns a `PagedResult<ReasoningTrace>` (newest-first, N+1 `HasNextPage`, `offset`-advanced), optionally owner-scoped (R1). Mirrored on `IReasoningTraceRepository.ListAllAsync`. Added pre-`1.0` because extending a public interface after the freeze breaks every third-party implementer.
- **`IToolCallRepository.GetStatsAsync(toolName?, scope?)` + a `ToolCallStats` record — per-tool usage aggregates.** Groups tool calls by name (total / successful / failed / success-rate / avg-duration) over calls reachable through owner-scoped reasoning traces; never reads the cross-owner global `:Tool` node. Same pre-`1.0` interface-stability rationale.
- These two additions let the upstream-TCK bridge drop its last two raw-Cypher fallbacks (`list_traces` with no session, `get_tool_stats`) in favor of the first-class services.

### Changed

- **Recall now writes a read-audit row.** The access-tracking update (`IMemoryDecayService.UpdateAccessTimestampAsync`, run after recall when the decay service is registered — the Neo4j default) now also creates a `:MemoryReadAudit` node in addition to bumping `last_accessed_at` / `access_count`.
- **All public `CancellationToken` parameters are now named `cancellationToken`** (previously ~109 were abbreviated `ct`), for a consistent library-wide convention. This is source-breaking only for callers that passed the token by name (`ct:`); positional callers are unaffected, and it is a binary-compatible change.
- **The decay/maintenance `string nodeLabel` parameters are now a closed `MemoryNodeKind` enum** (`Entity`/`Fact`/`Preference`). `IMemoryDecayService.CalculateRetentionScoreAsync`/`UpdateAccessTimestampAsync` and `IMemoryMaintenance.GenerateEmbeddingsBatchAsync` take the enum — an unsupported label is now a compile error, not a runtime `ArgumentException`, which also removes the label-injection surface in the decay Cypher. The MCP `memory_generate_embeddings` tool keeps its wire-level `nodeLabel` string and parses it to the enum (returning a clear error for an unknown value).
- **`DuplicatePair.Status` and `EntityResolutionResult.MatchType` are now enums** (`DuplicateStatus` and `EntityMatchType`) instead of strings, for compile-time safety.
- **Collapsed the two `RecallAsOfAsync` overloads (and the two `AssembleContextAsOfAsync` overloads) into one** method with an optional `DateTimeOffset? systemAsOf = null` — omit it for single-clock recall (both clocks equal), pass it for bitemporal. Same behavior; fewer overloads.
- **`IMessageRepository.SearchByVectorAsync`'s `metadataFilters` parameter is now `IReadOnlyDictionary<string, object>?`** (was `Dictionary<string, object>?`) — a source-only change for implementers, not callers.
- **Renamed `AgentMemory.McpServer.McpServerOptions` → `AgentMemoryMcpOptions`** to end the name collision (CS0104) with the MCP SDK's `ModelContextProtocol.Server.McpServerOptions`. `AddAgentMemoryMcpTools(Action<AgentMemoryMcpOptions>)` updated accordingly.
- **Renamed `EnrichmentOptions` → `WikimediaEnrichmentOptions`** (its members are Wikimedia-specific, mirroring `DiffbotEnrichmentOptions`); `AddEnrichmentServices` / the meta-package `WithEnrichment` parameter types updated.
- **Renamed `EnrichmentResult.DiffbotUri` and `RelatedEntity.DiffbotUri` → `ProviderUri`** (provider-neutral, complementing the existing `Provider` field).
- **`ToolCallStatus` now has explicit ordinals** and clarified docs distinguishing `Error` (raised an exception) from `Failure` (returned an unsuccessful result); both, with `Timeout`, classify as failed in tool-usage stats. (Status persists by enum name, so ordinals are stability-only.)
- **`IToolCallRepository.UpdateAsync` now returns `Task<ToolCall?>`** (was `Task<ToolCall>`): `null` when no tool call with that id exists (concurrent clear/prune), matching `IReasoningTraceRepository.UpdateAsync`.
- **`AgentTraceRecorder.StartTraceAsync` parameter order is now `(sessionId, task, …)`** (was `(task, sessionId, …)`), matching `IReasoningMemoryService.StartTraceAsync` — the two strings were easy to transpose.
- **`Message.ToolCallIds` is now a non-nullable `IReadOnlyList<string>` defaulting to empty** (was a nullable, undefaulted collection), matching the record's other collection members.
- **`LlmExtractionOptions.ModelId` is now `string?` defaulting to `null`** (was an empty-string sentinel); behavior is unchanged (the guard already treated null and empty identically as "use the `IChatClient` default").
- **`IEntityRepository.MergeEntitiesAsync` returns `Task<bool>`**: `true` when the merge matched and ran, `false` for a guarded / non-existent / self-merge no-op. (Newly declared on the public interface — see Added.)
- **1.0 API-surface lockdown — implementation types internalized.** Concrete implementation classes that are only ever resolved through the public Abstractions interfaces (the memory/reasoning services, Neo4j repositories/services/query holders/infrastructure, MCP tools/resources/prompts, extraction providers, enrichment decorators, GDS analytics services, merge strategies, and stubs) are now `internal`, shrinking the public surface from ~331 to ~203 types ahead of the SemVer-stable `1.0`. Accessibility-only: no behavior, signature, or DI-wiring change — DI resolves the internal types (via their still-public constructors) unchanged. The public contract is the Abstractions interfaces/records/options/enums, each package's `ServiceCollectionExtensions` + options, the Microsoft Agent Framework and Semantic Kernel adapters, and a small set of deliberate seams (`INeo4jTransactionRunner`, `ISchemaBootstrapper`, `IMigrationRunner`, `MemoryActivitySource`, `MemoryMetrics.MeterName`, the stub/clock/id helpers, `ExtractorBase`).
- **`SchemaConstants` and the schema-parity kit are now internal.** `SchemaConstants` (raw Neo4j backend label/property/edge strings) and the parity types (`SchemaParityVerifier`, `SchemaDescriptor`, `SchemaParityPolicy`, `SchemaParityReport`, `UpstreamSchemaRegistry`, `DotNetSchema`) — previously described as a reusable library component in `AgentMemory.Neo4j.Schema.Parity` — are implementation details for `1.0`. Schema-parity verification remains available through the `agentmemory schema-parity` CLI command; it is no longer a library API.

### Removed

- **Deleted the obsolete `MemoryTool` API surface** — `MemoryTool`, `MemoryToolRequest`, `MemoryToolResponse`, and `MemoryToolFactory.CreateTools()` (which was `[Obsolete]`). Use `MemoryToolFactory.CreateAIFunctions()` (MAF-compatible `AIFunction` instances) instead — the only tool surface samples use.
- **Deleted the dead `SchemaConstants.ToolCallStatusValues`** — an unused, non-authoritative string duplicate of the `ToolCallStatus` enum.

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

[Unreleased]: https://github.com/joslat/agent-memory-dotnet/compare/v1.0.1...HEAD
[1.0.1]: https://github.com/joslat/agent-memory-dotnet/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/joslat/agent-memory-dotnet/compare/v0.1.0-preview.4...v1.0.0
[0.1.0-preview.4]: https://github.com/joslat/agent-memory-dotnet/compare/v0.1.0-preview.3...v0.1.0-preview.4
[0.1.0-preview.3]: https://github.com/joslat/agent-memory-dotnet/compare/v0.1.0-preview.2...v0.1.0-preview.3
[0.1.0-preview.2]: https://github.com/joslat/agent-memory-dotnet/compare/v0.1.0-preview.1...v0.1.0-preview.2
[0.1.0-preview.1]: https://github.com/joslat/agent-memory-dotnet/releases/tag/v0.1.0-preview.1
