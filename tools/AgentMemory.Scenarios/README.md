# AgentMemory.Scenarios

A scripted history is written into each memory under test, then every memory gets the same questions. The checks are
deterministic, as the repository's evaluation boundary asks:

- **The store holds the fact:** a live fact with the right words and the right date fields. A wrong date shows as "same
  words, other dates".
- **The prompt holds the fact:** each needed fact is in **one** item of the prompt (a line, or one entry of a list joined
  with "; "). Two words that are somewhere in the prompt are not a fact.

Answers are logged for a person to read and are never graded.

Every run keeps its trace beside the report:

| File | What it holds |
|---|---|
| `calls.jsonl` | every model call: what was sent, what came back, the tokens; and every JEV judge call (purpose `judge`), its request and response bodies |
| `spans.jsonl` | the library's spans and their tags |
| `agentmemory.log` | the library's log, at Debug |
| `report.json` | the stores, each prompt, and an explained recall per question, with every candidate fact, its score and its rank. The explanation is a second recall with diagnostics on, made after the scored prompt was taken: the candidates and their ranks are the same, but the judge is asked again and may keep a different set |

## Bruna's go-live

`scenarios/bruna-go-live.json` comes from Pablo Piovano's [three-memories-one-question](https://github.com/ppiova/three-memories-one-question)
(MIT, commit `485fff9`; the licence is beside it), used with his permission. We added the store checks.

- **The history:** 49 statements in 8 sessions over three months, from Bruna, a tech lead. Her go-live is on Friday,
  11 December, on site in Córdoba. In November she mentions her sister's wedding in Bariloche, where she will be from
  9 to 13 December.
- **The three questions,** asked on 4 December in a new conversation:
  1. "When is the Acme go-live, and where does it happen?" The answer looks like the question.
  2. "What do I have on my calendar between December 7 and December 13?" A period of time.
  3. "Plan my prep for go-live week." Two facts that do not look alike collide.

## Arms

- **Baselines:** `file` (a notes file loaded from the top) and `vector` (embeddings, most similar first).
- **AgentMemory configurations:** the `graph-*` arms. Arms with the same `Writes` share one store; the first of them
  writes it and the others only recall.
- **The `-narrow` arms** let the judge see 20 candidates instead of 50. In this scenario's store of a few dozen facts,
  50 is the whole store, so a wide judge sees everything; 20 is what a larger store would leave it. It is a weak
  stand-in: in one run the trip ranked inside the top 20 anyway. The busier scenario (`bruna-go-live-busy.json`, 81 more
  ordinary statements) is the real test.

## What it measured (7 November 2026)

Each arm ran once, with GLM-5.3-Flash writing and answering, JEV judging, and bge-m3 embeddings. ✓ means every needed
fact reached the prompt, each in one item. This is one scenario, not a benchmark.

| Arm | Q1 when / where | Q2 the period | Q3 the clash |
|---|:-:|:-:|:-:|
| **Bruna's go-live** (49 statements) | | | |
| file | ✓ | ✗ | ✓ in one run, ✗ in the other: it depends on the room the file gets |
| vector | ✓ | ✓ | ✓ |
| Conversational | ✓ | ✗ | ✗ |
| Recommended, 2.0.0 (no look-ahead, no date reading) | ✓ | ✓ | ✓ |
| **Busier** (130 statements, 119 live facts) | | | |
| vector | ✓ | ✓ | ✗ |
| Recommended without look-ahead | ✓ | ✓ | ✗ |
| Recommended with look-ahead and date reading | ✓ | ✓ | ✓ |

In the small scenario, Recommended's Q3 passes for a reason worth knowing. The trip ranked 44th of 46 by similarity, but
the judge's wide search takes 50 candidates, so it saw the whole store and kept 33 facts. In the busier store, the trip is
not among the 50 candidates. Look-ahead brings it by date instead: "go-live week" resolves to 7–13 December through the
go-live's date, and the trip is dated inside that week.

## Running it

Models come from the environment, as `AgentMemory.Inference` resolves them (`docs/configuration/inference-providers.md`).
Recommended arms call the JEV judge and need `TYPESAFE_API_KEY`. Docker starts a Neo4j of the run's own unless `--uri`
is given.

```powershell
dotnet run --project tools/AgentMemory.Scenarios -- --dry-run      # the plan and the call estimate, nothing run
dotnet run --project tools/AgentMemory.Scenarios -- --max-calls 400 --output artifacts/evaluation/scenarios/my-run
dotnet run --project tools/AgentMemory.Scenarios -- --arms vector,graph-recommended --no-answers
```

`--max-calls` caps the run's chat calls; judge calls are counted apart (`calls.judge`) and are not capped. A run that
reaches the cap fails rather than finishing half-measured, and a run with any failure, including one outside a step,
exits with 1, its report written with what was measured and marked incomplete. Reports go under `artifacts/evaluation/`, which is not
committed.


## Bounded identity validation

The new explicit `--identity-validation` mode selects only `graph-identity-unified`.
It uses the unified extractor with evidence-based identity, temporal recall and a separately
recorded identity-adjudication role. It is deliberately not labelled Recommended and has no
external retrieval/update judge. The four short real cases have been executed; see the actual
outcomes and limitations below. The original full49-case profile is now compiled and executed; its strengthened quality checks
fail missing event context, despite6/6storage checks passing. See the full result below.

Four short scenarios live under `scenarios/identity-validation`: dates 3/4/12, two people
with the same name, go-live/Córdoba/required presence/Bariloche, and remote participation.
Answers remain for independent human review. The go-live prep check explicitly requires
location and presence in the prompt, beyond the earlier date-only oracle.

```powershell
dotnet run --project tools/AgentMemory.Scenarios -- --identity-validation --scenario tools/AgentMemory.Scenarios/scenarios/identity-validation/dates.json --dry-run
```

After qualification, omit `--dry-run` to execute under the fixed Bitdeer GLM-5.3-Flash/bge-m3
plan. The campaign reserves at most **1 USD total**, shared across scenario runs and reruns,
with at most 60 actual chat and 180 embedding HTTP attempts, 32 KiB request bodies,
4,096 output tokens per chat request, 45-second HTTP timeouts and zero hidden SDK retries.
An unset extraction output cap is set to 4,096 at the transport boundary and recorded there.
Text-only request bytes plus framing headroom conservatively bound input tokens at the
recorded published list rates. This is a reservation ceiling, not an account billing invoice.
Calls to other routes/models, excessive output/input, streaming and multimodal input are refused.

The default persistent ledger is `artifacts/evaluation/identity-validation/campaign.jsonl`.
`--campaign-budget` selects that campaign file explicitly. Do not reset it between cases or
reruns to bypass the campaign budget. Concurrent writers are refused. Reservations are flushed
before dispatch and survive unknown outcomes; they are not refunded after timeouts or restart.
`inference-http.jsonl` records each actual admitted HTTP attempt by role, request, response,
usage/finish reason and reserved cost; `identity-budget.json` describes the plan and final totals.
Keys/headers are not logged. Scoped decision and original-relationship JSON are included in
the store report. Only a newly owned disposable Neo4j fixture is accepted in this mode.

Keep the shared-machine RAM guard active during build and execution. The bounded validation
fixture has a 1 GiB container limit and 128/256 MiB heap plus 64 MiB page cache. A dry run
makes no network calls and starts no fixture. The general runner's older chat-only `--max-calls`
does not replace this campaign-wide transport guard.


The transport now durably records sanitized admission before dispatch and preserves every cost
reservation on unknown outcomes. The current26isolated budget/trace/acceptance controls pass, including
regressions against treating event-name words as location/participation evidence.

Use `--identity-validation --identity-writer` for the explicit store-aware composition with
DatesFromText, chat update fallback, evidence-based identity and event details. It has no external
retrieval judge and is not full Recommended/JEV. Ordinary defaults include neither identity arm.
The fixed validation client explicitly requests low reasoning effort while preserving its output,
HTTP-attempt and persistent USD1 caps. Production client defaults are unchanged.

`identity-validation.json` separates operational completion from store/prompt/native-hop checks.
An answer requires manual review. Event probes require a structured event-subject claim; `remote`
in an event's name is not permission. A valid uncertain verdict retains Pending identity, and the
native hop may then omit the permission even when normal retrieval and the answer are correct.
The runner reports that missing hop; it must not be relabelled an automatic expansion success.

The four bounded real scenarios have been executed. Dates and homonyms use their recorded earlier
source cuts; the physical-event case proves current native date-to-place/presence retrieval.
The remote answer respects permission, but its native permission hop remains unqualified because
identity is Pending. See [current outcomes, source boundaries and limits](../../docs/evidence-based-identity-implementation.md).
No exhaustive model matrix, uniform final-source four-case pass or perfect precision is claimed.

`LlmExtractionOptions.MemoryWriterEventIdentities` and `RecallOptions.EventDetails` remain explicit
opt-ins. The former uses source-grounded EVENT observations and coherent event-subject facts;
the latter follows only current permitted bindings, within existing context/latency limits.
The dedicated optional chat adjudicator supplies a constrained JSON schema; unsupported or invalid
output cannot create identity authority. Rebuild changed sources before any paid run: the local
supervisor checks timestamps and loaded binary hashes before dispatch.

## Full original Bariloche evaluation

The explicit full profile runs the unchanged original49 statements,8 sessions and3 question texts
with the current store-aware identity writer, DatesFromText, upcoming facts, time windows, overlap
expansion and event details. It adds separate evaluation assertions for location and physical
presence in the preparation question; assertion policy `bariloche-identity-v1` is recorded alongside
the original file hash. These gold assertions are not model input. The busy130-statement variant
and a multi-arm/JEV comparison are separate workloads.

```powershell
dotnet run --project tools/AgentMemory.Scenarios -- --identity-validation --identity-writer --identity-full-scenario --scenario tools/AgentMemory.Scenarios/scenarios/bruna-go-live.json --campaign-budget artifacts/evaluation/identity-validation/campaign.jsonl --dry-run
```

Omit `--dry-run` for a real run after reviewing the execution plan. The explicit profile permits200chat/800embedding
HTTP attempts **including prior usage** of the same campaign, while retaining the cumulative USD1
ceiling, request/output bounds, low reasoning, zero hidden retries and owned1GiB fixture. The normal
four-case profile keeps its60/180caps. Never reset the campaign file to obtain another allowance.
The profile accepts the original 49-statement scenario, its busy 130-statement variant and the 134-statement people
variant below, and requires the writer arm.

| Coverage | Executable entry point | What it establishes |
|---|---|---|
| Date identity and normalization |`scenarios/identity-validation/dates.json`; calendar/identity regressions |3/4/12remain distinct; text and temporal fields checked separately |
| Homonyms and evidence policy |`scenarios/identity-validation/ambiguous.json`; identity policy/review tests |Different people retain different identities; Pending is not equality |
| Storage and support |`IdentityIngestionIntegrationTests`, `IdentityDecisionIntegrationTests` |Actual Neo4j original observations, role bindings, review/revoke/replay/isolation |
| Upcoming, windows and overlap |`LookAheadAssemblerTests`; full scenario's explained recalls |Which dated facts enter upcoming/window/overlap sections and the actual prompt |
| Event context |`go-live.json`, `remote.json`, full native event probe |Confirmed support can supply undated place/presence; Pending can omit it |
| Full write-to-answer chain |`bruna-go-live.json` plus explicit full profile |6store checks, stronger3-question coverage, original identity receipts and manual answers |

These are deterministic and representative model evaluations, not a throughput benchmark or a
statistically established identity-accuracy score. The first full run completed49/49statements and3questions, with6/6store checks, but Q3and
the native hop missed location/presence: a failed quality result, since corrected (writer references, context-rooted
candidates, conversational policies; later runs pass on two or more runs per change). See the
[evaluation, root cause and head-to-head](../../docs/evidence-based-identity-evaluation-results.md).

The full review also finds two rejected writer operations (UnknownAction and QuoteNotFound),
logged at Debug despite zero warnings, and three known-event-subject facts without Subject
bindings. Read the operation/source/binding evidence as well as warning totals; processed turns
do not guarantee complete structured writes.

### The people variant (13 November 2026)

`scenarios/bruna-go-live-people.json` is the busy history plus four statements about two different people named Alex
Kim (Bruna's cousin, who moves to Buenos Aires, and someone from Acme's billing team), and two more questions: one about
Valentina, named in the question, and one about the cousin only. It measures recall for people: the facts bound to a
confirmed person the question names (`RecallOptions.PersonDetails`, which the identity arms turn on).

Two runs per arm: old is the previous similarity resolver, new is evidence-based identity with recall for people. Each
cell counts the needed facts that reached the prompt.

| Question | Old 1 | Old 2 | New 1 | New 2 |
|---|:-:|:-:|:-:|:-:|
| When and where is the go-live? | ✓ | ✓ | ✓ | ✓ |
| What is on my calendar 7–13 December? | ✓ | ✓ | ✓ | ✓ |
| Plan my go-live week prep (location and in-person included) | 2/4 | 2/4 | 2/4 | **4/4** |
| Mentoring session with Valentina | 4/4 | 4/4 | 3/4 | 3/4 |
| My cousin Alex Kim's plans | 2/2 | 2/2 | 2/2 | 2/2 |

What it shows, and what it does not:

- **The Valentina question does not separate the arms.** Every fact about her names her, so similarity already ranks
  them first in both arms. Recall for people ran (spans `memory.recall.person-identities` and
  `memory.recall.person-details`) but had nothing to add.
- **The new arm's Valentina miss happens at write time.** In both runs the writer stored "Martín asked me to mentor
  Valentina on system design" as a relationship to an entity named "Valentina on system design." and kept no fact, so
  no recall can return it.
- **The namesake guard was not exercised.** The billing Alex Kim was named only inside facts, so he got no identity
  decision of his own; in both arms the cousin question's prompt also carried a billing fact, found by similarity.
- **New run 1's prep miss is writer variance:** that run confirmed fewer go-live event decisions; run 2 passed 4/4, as
  every earlier busy run of this arm did.

A fair test of recall for people needs facts that do not carry the asked name, such as a nickname or a role, in a store
busy enough that similarity misses them. The four runs cost about USD 2.42 of the campaign's USD 12 ceiling.

## Independent identity gold (optional; needs a model and the corpus)

Every answer key above was written by the people who built the identity system. The independent gold scores the same
stores against labels written by others: [ECB+](https://github.com/cltl/ecbPlus) (news events; two similar but different
real events per topic, annotations CC BY 3.0) and [ConEL-2](https://github.com/informagi/conversational-entity-linking-2022)
(entities in Wizard of Wikipedia dialogues, annotations MIT). Sources and licences: `scenarios/public-gold/ATTRIBUTION.md`.

- **Frozen slices, no corpus text here.** `scenarios/public-gold/*-slice-v1.json` hold identifiers and SHA-256 hashes of
  the selected sentences, chosen by a fixed rule written in each file. The run reads the text from your downloaded copy
  and refuses a copy that differs.
- **Conversion.** ECB+: one article is one session; each annotated sentence is said verbatim after one fixed frame
  ("I'm covering this story at work: "), because a personal memory rightly stores nothing of general news. ConEL-2: one
  dialogue is one session of the user's turns. The gold groups are the corpus's own coreference instances or linked
  entities; nothing is relabelled.
- **Scores** (`identity-gold.json` beside the report): wrong merges (must stay 0), splits, splits if pending proposals
  were accepted, and event identities spanning two similar events of one topic, listed for a person to read. The scorer
  (`PublicGold/IdentityGoldScorer.cs`) gives the same numbers as the Bariloche metric on recorded runs.

```powershell
# Freeze a slice (no model), then dry-run and run; --legacy-comparison scores the old system the same way.
dotnet run --project tools/AgentMemory.Scenarios -- --identity-gold-select ecbplus --dataset artifacts/datasets/ecbplus --out ecbplus-slice.json
dotnet run --project tools/AgentMemory.Scenarios -- --identity-validation --identity-writer --identity-gold ecbplus --dry-run
# Re-score a recorded run without a model:
dotnet run --project tools/AgentMemory.Scenarios -- --identity-gold-score --report <report.json> --gold <identity-gold-answer-key.json> --scenario <scenario.json> --view decisions
```

The corpus folder is `--dataset`, else `ECBPLUS_DATASET` / `CONEL2_DATASET`, else `artifacts/datasets/<name>`; without
it the run stops with download instructions. `--identity-gold-sessions <n>` runs the first sessions only (the one-item
stage; not comparable). The runs share the persistent campaign budget above. Off by default and never in CI; the
adapters and scorer are covered without a model or corpus by `IdentityGoldTests`.
