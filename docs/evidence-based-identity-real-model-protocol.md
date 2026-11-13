# Evidence-based identity: bounded real-model validation

Status: first real unified control completed with partial success; the four-case acceptance series remains incomplete.
Owner revision, 11 November 2026: use a small representative series with real models,
an explicit inexpensive budget, root-cause analysis, fixes and targeted reruns.
An exhaustive benchmark, large progressive matrix and mandatory repeated full corpora
are no longer delivery requirements. Earlier proposals remain historical plans, not results.

## Small acceptance series

Use four short scenarios, each in an owned fresh store:

| Case | Input and independent checks |
|---|---|
| Dates | Training December 3, code freeze December 4 and wedding December 12, 2026. Check extracted surfaces, stored values and distinct date identities, then ask about the wedding. |
| Ambiguous identity | Two people with the same name but different supported context; an explicit alias/reference. Check that ambiguity stays separate/pending and that a supported confirmation preserves the source observation. |
| Go-live and travel | December 11 go-live; on-site at Acme in Córdoba in a separate undated statement; physical presence required; Bariloche December 9-13. Inspect both stored evidence and the actual prompt, then the three original questions. |
| Remote control | The same overlapping dates, with explicitly remote participation. The answer may report overlap but must not invent an unavoidable physical-presence conflict. |

One run per scenario is sufficient initially. Rerun a failing trigger after its root cause
and fix; add a repetition only when variability is material to that finding. Retain failures.
A small deterministic regression suite remains appropriate for atomicity, ownership,
concurrency, cancellation and idempotency; real inference cannot replace those controls.

## Models and cost boundary

Use the existing agreed Bitdeer model lineage where available: GLM-5.3-Flash for the
chat roles and BAAI/bge-m3 for embeddings. Record extraction/writer, identity adjudicator
and answer roles separately even when they use the same model. This is not a reproduction
of Pablito's gemma3:12b/gpt-oss:20b setup. That exact pairing is optional follow-up.

Before any inference dispatch, produce a no-network executable plan with the exact endpoint,
model IDs, source/config hashes, number of scenarios and all call/token/time/cost ceilings.
Use the owner's authorization for inexpensive bounded validation; do not expand the budget
or switch providers/models silently. The concrete transport must enforce the selected ceiling
across attempted requests, including retries, embeddings and any update/retrieval judge.
Record requested output caps and finish reasons, truncation, provider errors and actual usage.
No SDK-hidden retry or uncounted inference path may bypass that boundary.

The current general scenario runner's `--max-calls` limits chat only. It does not yet cap
judge or embedding dispatch and is insufficient as the total-cost guard for this series.
The `--identity-validation` path has qualified transport/acceptance controls and an explicit1USD persistent campaign cap; the owner has approved the stated synthetic payloads, Bitdeer destination and models. Keep API keys
in environment/configuration and out of prompts, headers in logs and reports.

Use an explicitly named extraction path. A unified-extractor smoke tests that path; it
must not be called Recommended. A Recommended run must use the actual configured writer
and required judge. Never relabel a judge-free fallback as Recommended. The `--identity-writer` variant explicitly uses the existing store-aware writer, DatesFromText and registered chat update fallback, without an external retrieval judge; this is a declared composition, not full Recommended/JEV.

## What is checked

Report three outcomes independently:

1. Storage: extracted and stored dates/words, decision status, target and source IDs;
   original observations remain inspectable and no forbidden identity is confirmed.
2. Retrieval: the actual prompt contains the event date, Córdoba/on-site requirement and
   Bariloche trip as supported facts. A correct answer cannot hide missing prompt evidence.
3. Answer: manually compare the real response to the independent expected facts, including
   the remote negative control. The deciding model is not its own sole judge.

The original Bruna questions remain unchanged:

1. When is the Acme go-live, and where does it happen?
2. What do I have on my calendar between December 7 and December 13?
3. Plan my prep for go-live week.

Q3 needs an explicit location/presence oracle in addition to the original harness's weaker
date-only checks. Wedding December 12 is a separate storage-integrity assertion.
Synthetic host keys are labelled fixture evidence, not identifiers verified by a model.

For every finding: preserve the initial prompt/response and stored state, identify whether
the cause is extraction, identity, retrieval or answering, correct reproducible code defects,
add the relevant regression and rerun that short scenario within the same campaign budget.
If the budget is exhausted, report the unverified remainder; do not silently spend more.

## Retained evidence and deferred work

Keep exact prompts/responses, source/config/model IDs, identity receipts, stored snapshots,
retrieved fact IDs, token/call/cost records, errors, elapsed time and host-memory witness.
Use disposable owned Neo4j and preserve the shared 5 GiB host RAM reserve throughout.
Passing this finite series supports these checked behaviors, not a numerical accuracy claim
or a guarantee of perfect identity resolution.

The original 49-statement and busy 130-statement corpora, pinned 2.1.0 comparisons,
multiple models, exhaustive P00-P07 combinations and three repeated full matrices remain
optional future benchmark work. They are explicitly deferred by the owner's scope revision
and do not block this implementation's bounded acceptance.


## First actual result and targeted follow-up

The unified control kept the correct literal dates3/4/12. The wedding date12 reached the actual
prompt and real answer. Normalized date fields passed1/3: the model omitted them for freeze and
wedding, so their correct ISO text values alone did not make them searchable by date. Provider
JSON establishes that omission; persistence did not erase supplied fields. This is a partial
result and is not accepted as complete temporal storage. No DATE entities were emitted in this
control, so it does not independently exercise the typed-date entity matcher.

The targeted follow-up selects the existing store-aware writer and deterministic DatesFromText
instead of broadening the frozen unified extractor's behavior. The registered chat update
fallback uses the same planned provider. All future case runs/reruns share the unchanged budget.
An initial writer attempt was stopped by the RAM guard before a chat result; wait for measured
headroom rather than pooling incomplete attempts as passes. The unresolved sister-of/user
endpoint also remains a context-retrieval observation for follow-up, despite its retained claim.

## Completed bounded evaluation and retained limits

See [current outcomes](evidence-based-identity-implementation.md#real-model-findings-and-outcomes).
All four scenarios were executed, with targeted fixes/reruns rather than an exhaustive benchmark.
The physical event proves actual native place/presence retrieval. The remote control's normal
prompt/answer respects permission; its identity remains Pending and its native permission hop is
unqualified. A weak checker originally missed that distinction and was corrected with red/green
controls and offline reassessment of the retained actual data. Do not overwrite the original report.

Uncertain identity is an allowed conservative outcome, not a reason to force a name-based match.
Keep positive native-hop qualification, negative answer control and missing-hop evidence separate.
The validation transport's low reasoning setting and the adjudicator's explicit JSON schema fix
observed protocol failures while retaining the same model, destination, frozen inputs and caps.
