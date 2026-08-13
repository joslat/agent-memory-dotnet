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

---

## Addendum — what it took to start the run (2026-08-13)

Recorded because two guards fired before a single provider call was made, and both were right.

### 1. The corpus could not be opened at all

`VerifyIntegrity` threw *"fingerprint mismatch"*. Cause: the fingerprint serialises `GraphSnapshot`
as a whole record, and 6.5 added two nullable counters to it — a fix for a label-blind probe that
changed nothing about what was stored. Two extra `null`s in the serialised JSON moved the hash, and
every corpus sealed before that became permanently unopenable.

Diagnosed by reading the manifest out of the Docker volume. **Two hypotheses were wrong first** — a
reconstructed historical field set that did not reproduce the hash even with the field list matching
exactly, and a shape heuristic that exempted synthetic test fixtures too. The second was caught by
two pre-existing tamper tests, correctly.

Settled on an explicit grandfather list by preparation id: cannot over-apply, reviewable, names what
is exempted and why. Tamper detection stays fatal for everything else. Recorded as
`FingerprintVerified = false` with a loud reader warning, because the alternative to a fatal check is
a check nobody notices.

### 2. The drift guard then refused the run, and was right

```
abstention: corpus=TargetProportion run=AsSampled
```

The corpus was built with abstention targeting — that is why it holds 20 abstention questions — and
the run defaulted to `AsSampled`. Evaluating anyway would have reported this run's configuration over
a graph built with another one: *"internally consistent, reproducible, and wrong"*, in the guard's own
words.

**This is the vindication of the decision in (1).** Integrity was downgraded to a recorded warning
precisely on the argument that drift is the guard which actually protects a measurement. Drift then
immediately caught a real configuration mismatch that would have invalidated the comparison. The two
guards are not redundant, and the one that was kept fatal is the one that earned it.

### 3. Then the machine ran out of memory

`OutOfMemoryException` mid-run, on a box that had accumulated a session's worth of test hosts and
build servers alongside two Neo4j containers. Not a finding about the software — recorded so the
gap between "pre-registered" and "run" is not mistaken for a result.

---

## RESULT — decided by the control alone (2026-08-14)

**Run:** `artifacts/evaluation/longmemeval-prepared-20260812T140253Z-reuse-20260813T221547Z`.
Control only, ~200 calls. **The treatment arm was not run, and the pre-registered rule is why.**

### The control, and the witness

| Arm | Accepted | Correct | Mean `GoldSessionRecallAtK` | n non-null |
|---|---|---|---:|---:|
| structured | yes | 45/50 (90.0%) | **0.9650** | 50/50 |
| hybrid | yes | 42/50 (84.0%) | **0.9800** | 50/50 |

The witness is satisfied: coverage is non-null on the structured arm for the first time — 50 of 50,
where every previous run recorded 0 of 50. Task 22.3 works end to end.

### Why the treatment arm was not bought

Coverage against the cliff measured in the completeness sweep (below ~0.75, accuracy collapses to
~23%):

| Coverage | structured | hybrid |
|---|---|---|
| 1.00 | 43/47 correct | 42/49 correct |
| 0.75–0.99 | 1/1 | — |
| 0.50–0.74 | 1/1 | — |
| **< 0.50** | **0/1** | **0/1** |

**Real retrieval on this corpus is already at coverage 1.00 for 47 of 50 structured and 49 of 50
hybrid questions.** Exactly one question per arm sits below the cliff, and it fails — consistent with
the sweep, and the only question a coverage lever could possibly rescue.

So the levers have **at most one question of fifty** available to them. McNemar on a single discordant
pair is p = 1.0. Spending another ~200 calls could not produce a result the decision rule can read,
and a null returned from that run would describe the corpus, not the levers.

**This is the pre-registered `RescueShortOwnerResults` caveat applying to all three: untestable here
rather than ineffective.** It must not be recorded as a null result. The levers remain unmeasured.

### What this closes, and what it opens

The completeness sweep proved coverage is worth ~80 accuracy points **when it drops**. This control
shows real retrieval on this corpus **does not drop** — it sits at 0.97–0.98.

Both are true, and together they say something sharper than either alone: **the remaining ~10–16% of
failures on this corpus are not coverage failures.** They are the oracle-impossible questions (four
in the archive, 0/36 with perfect context), judge disagreements, and answer-model nondeterminism —
none of which any retrieval change reaches.

That is the same ceiling every retrieval-side candidate has hit this week: routing at 1 of 50,
decomposition at 0 wins of 29, precision flat across 9.2× context, representation ~1 question, and
now coverage at 1 of 50. **On this corpus, retrieval is not the bottleneck; it is already close to
its own ceiling.**

To measure a coverage lever at all would need a corpus where retrieval genuinely under-covers — a
larger haystack, a tighter top-K, or many owners. That is a corpus-design task, not a lever question,
and it should be costed before it is built.

### Prediction scoring

- *"`ExpandFactsByPredicate` will move coverage on multi-session questions"* — **unresolved**, not
  wrong. There was no headroom to move.
- *"`RescueShortOwnerResults` will move little because this corpus is single-owner"* — **consistent
  with the outcome**, and generalised: no lever had room, for a reason that subsumes the one predicted.
- *"Accuracy will move less than coverage"* — **unresolved**; neither moved.
