# Pre-registration — do the coverage levers move coverage? (22.4)

**Written 2026-08-13, before the run.** Committed first precisely so the decision rule cannot be
chosen after seeing the number. Nothing below may be edited once a run has started; corrections go in
a dated addendum.

## The claim under test

The completeness sweep measured gold-session coverage as worth ~80 accuracy points, with a step
between 0.50 and 0.75 (`docs/reviews/decomposed-answering-oracle-result.md`). AgentMemory already
ships three retrieval mechanisms aimed at coverage, all defaulting to `false`:

| Lever | What it does |
|---|---|
| `RecallOptions.ExpandFactsByPredicate` | Retrieves a relation **whole** via `predicate_key` instead of top-K. Its own note: *"top-K is a relevance cutoff and cannot answer 'how many'"* |
| `RecallOptions.ResolveQueryRelations` | Resolves the query's relation before retrieving it |
| `MemoryOptions.RescueShortOwnerResults` | A short scoped result falls back to an owner-bounded scan |

**Question: do they raise measured gold-session coverage on a real corpus, and at what cost to
accuracy?**

## Design

- **Corpus:** the pinned frozen base
  `am-lme-longmemeval-prepared-20260812t14-base-e5c49cf7cbd74c78b2a123eeae968b0d` (50 questions,
  seed 42, 616 extraction calls), reused via `--reuse-prepared-volumes`. **No extraction** — both
  configurations read the identical graph, so extraction nondeterminism is held exactly constant and
  the comparison is paired.
- **Two configurations, not four.** Control (all three levers off) versus **all three on**. If the
  union does not move coverage, no individual lever can, and attributing a null result to three
  separate causes would be three times the spend for no extra information. Attribution is a
  *conditional* follow-up, not part of this run.
- **Both arms** (structured and hybrid) run in each configuration, as the prepared-pair path always
  does.
- **Cost:** answer + judge only, ~200 calls per configuration, ~400 total.

## Primary metric, fixed in advance

**`GoldSessionRecallAtK`**, per question, averaged per arm — the fraction of a question's gold
sessions represented in the assembled context. This became observable on the structured arm only
today (22.3); before that it was null on 1,476 of 1,476 structured records, which is why this
question has never been asked.

**Secondary:** accuracy, and mean context tokens.

## Decision rule

Ship a lever ON by default only if **both** hold:

1. **Mean `GoldSessionRecallAtK` rises** in the treatment arm versus control, on the same corpus.
2. **Accuracy does not fall**, judged against the between-cold-build band already measured at n=50:
   **6.1 points structured / 3.9 points hybrid**. A drop larger than that band kills it outright; a
   drop inside it is not exculpatory, it is inconclusive.

**Kill the whole line of work** if coverage does not move at all. That would mean the shipped
mechanisms do not address the measured failure mode, and 22.5 (iterative retrieval) and 22.6
(session-granular retrieval) become the only remaining candidates rather than refinements.

**What would NOT count as success:** accuracy rising while coverage does not. On a 50-question run
that is within noise of everything, and crediting it to a coverage lever would be exactly the
post-hoc reasoning this document exists to prevent.

## Predictions, recorded before the run

Written down so being wrong is visible rather than reinterpretable.

- `ExpandFactsByPredicate` **will** move coverage on multi-session questions, because retrieving a
  relation whole is the only shipped mechanism that can return more than top-K of one predicate.
  Confidence: moderate.
- `RescueShortOwnerResults` will move coverage **little on this corpus**, because it fires on a short
  *owner-scoped* result and this corpus is single-owner — the condition it exists for may never
  arise. Confidence: moderate. **If this is right, the lever is untestable here rather than
  ineffective, and must not be reported as a null result.**
- Accuracy will move **less than coverage**, because the step function means only questions crossing
  the 0.75 threshold change verdict. Confidence: high.

## Witness

The two configurations must differ in their recorded run fingerprint in exactly the swept fields
(`expandFactsByPredicate`, `resolveQueryRelations`, `rescueShortOwnerResults`). **Identical
fingerprints void the run** — a sweep whose arms are configured the same measured one condition
twice, which would report "the levers do nothing" while never having enabled them.

`GoldSessionRecallAtK` must be non-null on the structured arm. If it is null, 22.3 did not take
effect on this path and the run is void rather than negative.
