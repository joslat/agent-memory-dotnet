# 8.3b decided without buying it: the episodic failures are mostly not capture failures

**Status:** decided on evidence already on disk. **No provider calls, no rebuild.**
**Date:** 2026-08-13. **Instrument:** `longmemeval --capture-headroom` (PLAN 8.3c).

## The question, and why it was worth asking cheaply first

8.3b asks whether `AssistantContentMode` should stop defaulting to `Ignore`. The plan costs the answer
at roughly **96M input tokens** — ~30 episodic questions × 3 cold builds × 2 arms, because extraction is
nondeterministic and one build proves nothing. Before spending that, there is a question that costs
nothing:

> `AssistantContentMode` is a **capture** setting. It stores more. So it can only convert a failure
> where something needed was **never stored**. How many episodic failures are actually of that kind?

The answer-presence gate already records, per question, whether the gold answer's distinctive tokens
were in the assembled context. So the split is computable from artifacts on disk.

## Method

`--capture-headroom` sweeps recorded `prepared-pair-report.json` files, joins each judged question to
its presence gate, maps the dataset's task label to a memory type via the taxonomy, and splits each
type's failures into:

- **answer already present** — it was in the context and the run was still wrong. Storing more cannot
  fix this; it only makes the context bigger, and the measured cost of doing so is 32.3% of the
  retrieval budget and +23.1% prompt tokens.
- **answer absent** — the only failures a capture-side change could possibly convert.

**Only arms whose gate actually evaluated are counted.** 42 of 62 recorded reports have no live gate,
and pooling them would repeat 4.5's mistake exactly — the pass that reported 3.4% accuracy against a
known 90% because it averaged in runs that could not answer the question at all.

## Result

```
capture-headroom: 62 report(s) scanned, 20 arm(s) with a live presence gate
  [hybrid]
    episodic   n=39  acc=76.9%  wrong= 9  checkable= 7  answerAlreadyPresent= 7  captureReachable=0  ceiling= 0.0%
    semantic   n=88  acc=90.9%  wrong= 8  checkable= 6  answerAlreadyPresent= 6  captureReachable=0  ceiling= 0.0%
    temporal   n=77  acc=97.4%  wrong= 2  checkable= 2  answerAlreadyPresent= 2  captureReachable=0  ceiling= 0.0%
  [structured]
    episodic   n=41  acc=48.8%  wrong=21  checkable=19  answerAlreadyPresent=14  captureReachable=5  ceiling=12.2%
    semantic   n=86  acc=73.3%  wrong=23  checkable=15  answerAlreadyPresent=13  captureReachable=2  ceiling= 2.3%
    temporal   n=77  acc=92.2%  wrong= 6  checkable= 6  answerAlreadyPresent= 6  captureReachable=0  ceiling= 0.0%
```

### Hybrid mode: the question is answered, and the answer is no

**Every one of the 7 checkable episodic failures had the gold answer already in context.** Zero were
capture-reachable. That is not a close call needing a bigger sample — it is a structural observation
about the mode: hybrid ships raw recalled messages alongside extracted memory, so the assistant's turns
are *already* in the context. Extracting them a second time as utterance-acts adds a copy, not a fact.

For hybrid, **no run at any sample size can show a capture-side gain on episodic**, because there is no
capture-side loss to recover.

### Structured mode: headroom exists, and it is about one question

Memory-only retrieval is the mode where the argument for the feature is real — with no raw messages,
extraction is the *only* route by which an assistant act reaches the context. And the instrument agrees:
**5 of 41 episodic questions (12.2%)** failed with the answer absent.

That is the honest ceiling, and it is small in the way that matters. 12.2% is roughly **one question per
ten-question episodic sample**, while:

- repeated evaluation of a *fixed* corpus at n=10 already moved 80% / 90% / 90% — one whole question of
  jitter with nothing changed;
- and cold **build**-to-build variance is far larger: three builds of an identical configuration scored
  25 points apart at n=50.

The decision rule requires the episodic mean gain to **exceed that type's own noise band across ≥3
builds per arm**. A ceiling of ~1 question sits at or below the band before the run starts.

## Decision

**Do not spend the 96M tokens. Publish the bounded null.**

- **Hybrid:** no headroom exists. Settled.
- **Structured:** a ceiling of 12.2% exists but is at or under the measured noise band, so the rule
  cannot return "ship it" even if every absent answer were recovered. Running it would buy a number the
  rule already disposes of.
- `AssistantContentMode` therefore **stays opt-in**, and the reason is now quantitative rather than an
  absence of evidence: *on this instrument, the largest gain available to it is one question, against a
  band wider than that.*

**What would reopen it.** A sample large enough that one question stops being the resolution — ~30+
episodic questions in **structured** mode only, which also removes the pooling that diluted the original
decision ~8× (episodic was 6 of 50 questions). At that size the ceiling would be ~4 questions and could,
in principle, clear a tight band. That is a budget decision, and it is now a decision with a number
attached.

## Limits of this evidence, stated plainly

The presence gate is a **token-overlap** test, not a proof of sufficiency. Three consequences, none of
them hidden:

1. **The ceiling is an upper bound on a weak signal.** It is sound for arguing a run *cannot* help
   (nothing was missing) and unsound for arguing one *would*.
2. **"Absent" is not "absent because the assistant's act was not captured."** Some of the 5 structured
   failures may be absent for unrelated reasons, which makes the real ceiling lower, not higher.
3. **Uncheckable failures are excluded from the numerator** (2 hybrid, 2 structured episodic). Counting
   them as headroom would inflate exactly the number used to justify spending.

## A reporting gap found on the way, and fixed

Deciding this from disk required knowing which arm a recorded corpus belonged to — and the report could
not say. Schema 6 has recorded ingestion identity (`AssistantContent`, vocabulary hashes, abstention
policy, refused sessions, memory types, seed) on the *manifest* since it was introduced, precisely so an
Utterance corpus cannot be silently adopted by a run configured for Ignore. **The report projected none
of it.** So two corpora built under materially different ingestion settings looked identical in every
field a human reads. The fingerprint would have differed, but a fingerprint says "not the same" — never
"differs in the episodic mode". Now projected, alongside the observed provider build ids from S-4.

## Related

- `LongMemEvalCaptureHeadroom` — the instrument, 7 provider-free tests
- `LongMemEvalCaptureHeadroomProgram` — the `--capture-headroom` verb (read-only, credential-free)
- PLAN 4.5 — the "judged without a gate" flag, which is why only 20 of 62 arms are counted here
- PLAN 5.5 — the per-type noise floor this ceiling is compared against
