# Full Bariloche: real evaluation result

> **13 November 2026: full Bariloche acceptance reopened.** Candidate-source context and partial writer operations remain active corrections; see the [root-cause audit and bounded validation matrix](evidence-based-identity-root-cause-review.md). Earlier component/source-cut results are retained, not full-scenario acceptance.

Run: 12 November 2026, original 49 statements, 8 sessions, 3 questions. **Quality result: FAILED.**
All input turns and questions executed; operational completion is separate from accepted behavior.

The source is the unchanged [original Bruna scenario](../tools/AgentMemory.Scenarios/scenarios/bruna-go-live.json)
from Pablo Piovano's MIT-licensed scenario. Assertion policy: bariloche-identity-v1. The original
history, question texts, answers and scenario hash are preserved. Prep additionally requires
location and physical-presence evidence; evaluation gold is not model input.

## Configuration and provenance

Store-aware writer + DatesFromText + evidence-based identity + Upcoming/TimeWindows/
OverlapExpansion/EventDetails. GLM-5.3-Flash on Bitdeer writes, adjudicates and answers; bge-m3
embeds. Low reasoning, output/request/call/cost bounds and no hidden SDK retries remain active.
This is the explicitly composed identity arm, with no external JEV retrieval judge.
Neo4j 5.26 is disposable and owned by the runner, limited to 1 GiB; minimum available host RAM 10.448 GiB.
Container lifecycle was about 2 minutes 2 seconds and the fixture was disposed. The clock replays
the source timestamps and asks on 4 December 2026; it does not use the host date for that history.

The branch and evaluator changes are uncommitted; a Git HEAD alone does not identify this source.
Exact loaded DLL hashes, input hash, HTTP traces, prompts and raw snapshots are retained locally
under run label bariloche49-identity-writer-real-01.

Original scenario SHA-256: 5b3ebeb0e8b25f924f8aeec126105d3d7b5b487085ecb0d7976b5dee899621f9.

## What passed and failed

| Layer | Actual result |
|---|---|
| Input execution |49/49 statements and 3/3 questions |
| Storage |55 live facts, 1 closed fact, 19 Entity nodes, 3 preferences and 10 original relationship observations |
| Selected storage/date checks |6/6 pass |
| Identity decisions |20: 13 New, 6 Pending, 1 Confirmed; these are not accuracy scores |
| Current Fact-role bindings |20; snapshot not truncated |
| Q1when/where |Required date/place reach prompt; answer gives December 11 and Cordoba |
| Q2calendar |Required go-live/trip dates reach prompt; answer covers them, with imprecise conflict wording |
| Q3prep |Date/trip reach prompt; Cordoba and in-person requirement are missing |
| Independent native event probe |Missing both undated location and required-presence details |
| Overall |Not accepted as a complete business-scenario pass |

The six storage checks retain:

| Fact | Stored date/value |
|---|---|
| Go-live |2026-12-11 |
| Go-live place |Acme's office in Cordoba |
| Bariloche trip |2026-12-09 through 2026-12-13 |
| Wedding |2026-12-12 |
| Training |2026-12-03 |
| Code freeze start |2026-12-04 |

The checked wedding date is not replaced with 3 or 4. No DATE Entity was emitted, so this run
does not independently exercise date-entity equality; typed calendar identity has separate
deterministic/native regression coverage.

## Temporal retrieval

Upcoming supplies the three dated go-live/trip/wedding facts for Q1. Q2 resolves December 7-13
and retrieves those three facts. Q3 resolves the same week through the dated go-live anchor.
OverlapExpansion is enabled, but OverlappingFacts has no additional facts in this run; this
does not independently measure its contribution. The problem is the missing undated context,
not failure to retrieve the checked go-live/travel dates.

## Why event context is missing

The writer stores both location and presence correctly. Actual binding IDs show that the
dated go-live resolves to event observation A, while location/presence resolve to separate
Pending observation B. The common display name does not authorize identity equality.

The adjudicator's offered candidate has an empty sourceIds list. Its only offered source is
the current statement beginning “The cutover happens on-site...”; the earlier statement that
introduces the named go-live/date is absent. It returns valid uncertain JSON with finish_reason
stop, 29 completion tokens and no parsing/transport warning. This is not output-token exhaustion.
Native date expansion correctly refuses to cross the unresolved identity, leaving the important
facts inaccessible by that hop.

Direct Q1retrieval can still find the location. Q3retrieves preparation material and the dated
week, but not the two facts needed to decide whether remote participation is permitted.

## Additional writer contract finding

The log reports zero Warning entries, but it records **two rejected operations at Debug level**:

- Turn 36: the model returns action op=entity rather than add for the wedding EVENT observation.
  The parser rejects UnknownAction. The wedding date/place facts are retained, without a Subject
  identity binding for that turn.
- Turn 43: the proposed rollback-checklist fact has a quote absent from the source as a contiguous
  span. The parser rejects QuoteNotFound; that proposed Fact is not stored.

Three facts whose subjects match observed EVENT names have no Subject binding: the go-live
checklist and the wedding date/place. This is separate from the Pending go-live location/presence
identity. Existing source words remain in the transcript; accepted memory and graph coverage are
partial. Zero warnings cannot be used as a zero-rejection or complete-write score.

A writer follow-up should enforce the operation schema, validate required event-observation
references, expose partial-result/drop diagnostics and use only bounded correction. It must not
silently repair an invalid action into authority or substitute a broad quote for unsupported data.
The existing writer/reviewer roadmap item 41.15 receives this evidence; no writer fix is claimed.

## Independent answer review

Q1 gives the requested date/place. Q2 includes the required dates/trip, though its wording treats
overlap with the wedding in the trip destination too loosely. Q3 is not reliable: it proposes a
a kickoff labelled remote? and training on December 8 while the stored training is December 3. These answer
issues are separate from storage corruption and must not be hidden by keyword/date-only scores.

## Cost, regression and next correction

This run made 60 chat HTTP requests (50 writer/update, 7 identity adjudication, 3 answer) and 110 embedding
requests. Incremental conservative reservation: USD0.21709471. Cumulative campaign: USD0.37292261
of the unchanged USD 1 ceiling. These are reservation bounds, not an account invoice.

The evaluator's 26 focused controls pass, including preserved prior reservations, hard currency
cap, original input/question preservation and rejection of display-name words as detail evidence.
The current full root passes 8,040/8,040 cases with zero skips and a clean build. Green unit tests
do not convert this failed real scenario into accepted behavior.

The next correction should supply bounded, owner-authorized historical candidate sources and
relevant adjacent context to adjudication, preserving provenance/freshness checks, hard type/date
vetoes and conservative Pending behavior for genuinely ambiguous cases. Re-run this unchanged
scenario plus an ambiguous-event negative control after that implementation. No context fix is
claimed by completing this evaluation.

One run and six selected store assertions are not a statistical precision benchmark, a guarantee
that every extracted claim is correct, or a controlled version-to-version comparison.

## Head-to-head and ablation, 13 November 2026

Same harness for both arms: the same store-aware writer, chat and embedding models, metered campaign and no external
judge; the old arm uses the legacy similarity resolver, the new arm evidence-based identity with
`ApplyConversationalPolicies()` and four read-only context turns. Wrong merges and splits are scored against a frozen
statement-level gold for the original scenario.

| Scenario | Arm | Prompt needs (3 questions) | Event details (native) | Wrong merges | Notes |
|---|---|---|---|---|---|
| Original, 49 statements | new (4 consecutive runs) | 3/3 each | 8 details, 0 failures | 0 | 0 split identities, people included |
| Original, 49 statements | old | 3/3 | unsupported | 0 | 10 entities, no event entities; answers carried by vector retrieval |
| Busy variant, 130 statements | new (2 runs) | 3/3 in both | 8 details, 0 failures in both | n/a (gold covers the original) | |
| Busy variant, 130 statements | old (2 complete runs) | **2/3 in both: conflict question missing the go-live location and the in-person requirement** | unsupported | n/a | distractors defeat vector retrieval |
| Ambiguous control (two people named Alex Kim) | old | 1/1 | n/a | **1: both people stored as one entity** | |
| Ambiguous control | new | 1/1 | n/a | 0 | the second Alex Kim stays a separate, reviewable identity |

Ablation on the original scenario (one component off at a time): adjudicator off, two runs, all checks pass but one run
split an organisation into two identities, at no measurable call saving; context turns off, one run, all checks pass. The
adjudicator and the context turns are kept as safety nets; writer references produce nearly all confirmations.

Limits: one scenario family and its busy variant, one chat model, gold written by the implementing team; an independent
gold (adapted public corpora such as ECB+ for events and WePS for shared names) and a second model are the next measures.
