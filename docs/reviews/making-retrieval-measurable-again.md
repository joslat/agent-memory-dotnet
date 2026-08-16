# Making retrieval measurable again: what a discriminating corpus would cost

**Task 26.4.** The finding that closed the quality search was that retrieval improvements have nothing
left to bite on. This costs the options for changing that, and recommends the cheap one.

---

## 1. The problem, measured

Realised gold-session coverage on the latest accepted 50-question run:

| Coverage | Structured | Hybrid |
|---|---:|---:|
| **1.00** | **47 (94%)** | **49 (98%)** |
| 0.75 – 0.99 | 1 (2%) | 0 |
| 0.50 – 0.74 | 1 (2%) | 0 |
| < 0.50 | 1 (2%) | 1 (2%) |
| **mean** | **0.965** | **0.980** |

And the accuracy-versus-coverage curve is a **step**, not a slope:

| Gold coverage | Accuracy |
|---|---:|
| 1.00 | 100% |
| 0.75 – 0.99 | 100% |
| **0.50 – 0.74** | **22.7%** |

Put together: **the cliff is at 0.5–0.75, and 94–98% of questions sit at 1.00.** A retrieval
improvement can only move questions that are below the top of the curve, and there are one or two of
them per fifty. That is the whole reason six architectural candidates measured flat.

## 2. Why the budget is not the constraint either

The obvious first guess — "retrieval is capped and starving" — is wrong, and the telemetry says so:

| Arm | items retrieved (min / mean / max) | budget | truncated |
|---|---|---:|---:|
| Structured | 4 / 14.8 / 30 | 30 | **0 / 50** |
| Hybrid | 79 / 85.9 / 90 | 90 | **0 / 50** |

**Nothing was truncated on any question.** Structured averages 14.8 items against a cap of 30, so the
cap is not binding for most questions. The retriever is not being starved; it is finding everything and
the questions are easy to cover.

## 3. The options, costed

### Option A — a larger haystack (LongMemEval-M)

Coverage falls naturally when there is more to search. **The most faithful option and the most
expensive:** a corpus roughly an order of magnitude larger means an order of magnitude more extraction.
Our measured build is **616 extraction calls / 2,386 units** for the S corpus; M is not a
proportionally larger bill so much as a different project.

**Verdict: correct, and not affordable right now.**

### Option B — lower the retrieval budget on the corpus we already have ✅

Force competition instead of buying more haystack. Structured at a budget of, say, 5 rather than 30
would bind on nearly every question and push realised coverage down into the discriminating band.

**Cost: zero extra corpus.** The frozen corpus is reused (`--reuse-prepared-volumes`), so this is
~100 chat calls per arm per level — the same as any 50-question arm.

**Why it is legitimate rather than a rigged test:** the precision sweep already established that
*noise* does not hurt (9.2× context, accuracy flat) while *missing evidence* does (95% → 15% when gold
was halved). A budget that binds manufactures the second condition, which is the one with a lever
behind it. It measures "does this retrieval change find the right things **first**" — precisely the
question a saturated corpus cannot ask.

**The honest caveat, which must travel with any number from it:** results at a constrained budget do
**not** transfer to the shipping configuration. They rank retrieval strategies against each other; they
do not predict production accuracy. A run at budget 5 is a *ranking instrument*, not a *scoring* one,
and reporting it beside the 90% headline without that label would be the same metric substitution this
project keeps refusing.

### Option C — inject distractor sessions into the live corpus

We already have this on the oracle side (`--oracle-precision --distractor-sessions K`), and it measured
**flat**: 25 distractors, 9.2× the context, no accuracy change. Doing it against real retrieval would
mostly re-measure that null.

**Verdict: already answered. Do not re-buy it.**

## 4. Recommendation

**Option B, as a pre-registered budget sweep**, and only when there is a retrieval change worth ranking.

1. Reuse the frozen corpus. Sweep the structured budget over roughly `{30, 15, 8, 5, 3}`.
2. Record **realised coverage** per level — the void witness is that if coverage does not fall, the
   budget never bound and the level measured nothing.
3. Confirm the coverage distribution actually lands in 0.5–0.75 before drawing a single conclusion.
4. Report every number as *"ranking at a constrained budget"*, never as an accuracy.

**Do not run it speculatively.** It is an instrument for comparing two retrieval strategies, and there
is currently no candidate strategy worth comparing — that is what the nine experiments in
`quality-effort-and-what-did-not-move.md` established. Build the lever first, then use this to measure
it.

The one candidate that might qualify is **query formulation (27.4)**, which is the only untested
retrieval lever left and which now has an instrument that can see it (27.1 made structured turn
coverage observable). If that shows anything at full budget, this sweep is how to size it.
