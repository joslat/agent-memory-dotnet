# Query formulation: the result, and the confound that nearly faked it

**Task 27.4.** Decision rules were fixed in advance in `query-formulation-preregistration.md`. This is
the outcome under those rules, written without renegotiating them.

**Verdict: RETIRED.** Query formulation does not move retrieval on this corpus.

---

## 1. The measurement

Same build, same frozen corpus, same 50 questions, same judge protocol. The only variable is how the
retrieval query is derived from the question.

| | Control (verbatim) | Rewrite | Δ |
|---|---:|---:|---:|
| **structured** accuracy | 43/50 = 86.0% | 42/50 = 84.0% | **−1 question** |
| **structured** session coverage | 0.965 | 0.970 | +0.0050 |
| **structured** TURN coverage | 0.936 | 0.943 | **+0.0071** |
| **hybrid** accuracy | 43/50 = 86.0% | 43/50 = 86.0% | **0** |
| **hybrid** session coverage | 0.980 | 0.980 | **0.0000** |
| **hybrid** TURN coverage | 0.943 | 0.943 | **0.0000** |

Both runs `accepted: true`, zero validation issues.

**The mechanism definitely ran.** The void witness reports `derived 50, changed 50, failed 0` on both
arms: the rewriter was invoked on every question and produced a different query every time. This is a
null result about a treatment that fired, not a treatment that quietly didn't.

## 2. Applying the pre-registered rules

> *"Ship a formulation arm only if it raises mean gold-turn coverage on the structured arm by more than
> the control's own spread… No spread, no claim."*

The control's spread is unmeasured (one run). The observed delta is **+0.007 turn coverage** — 0.7
percentage points. For scale, two *accepted* runs of the identical configuration have differed by
**14 accuracy points** on this benchmark. A 0.7-point coverage delta is far inside that.

> *"If coverage does not move on any arm, query formulation is retired."*

Hybrid moved **exactly 0.0000** on all three metrics. Retired.

**Accuracy went down by one question on structured.** That is also inside noise and is not evidence
that rewriting hurts — but it is emphatically not evidence that it helps, and the pre-registration
named accuracy a secondary endpoint precisely so this number could not be mined either way.

## 3. The confound that nearly produced a false positive

The first comparison used the existing accepted control from 2026-08-13. Against it, structured turn
coverage appeared to rise **from 0.000 to 0.943** — a spectacular, publishable-looking gain.

**It was entirely an instrument change.** That control predates task 27.1, which is what *made*
structured turn coverage observable at all; before it, the metric read 0.000 on every structured
question, correct and wrong alike. Comparing across that boundary measures the fix, not the treatment.

The only arm comparable across the two runs was hybrid, whose control could already see turn coverage
(0.890 → 0.943, +0.052) — and even that shrank to **exactly 0.000** once the control was re-run on the
same build.

**A treatment run must be compared against a control from the same build.** That is cheap to say and
easy to skip; the cost of skipping it here would have been a headline claim built on a metric that had
been switched on in between.

## 4. Why the null is unsurprising, and what it does not prove

Three things predicted this, and the pre-registration recorded all of them before the spend:

1. **The ceiling was one question.** Of six hybrid failures, two were retrieval-shaped, and one of
   those two (`352ab8bd`) is oracle-impossible at 0/8 with perfect context. One movable question in
   fifty.
2. **Coverage is saturated.** 0.965–0.980 session coverage, with 94–98% of questions already at 1.00
   (see `making-retrieval-measurable-again.md`). There is almost nothing left to retrieve.
3. **Noise is larger than the effect.** The answer model runs at temperature 1.0 — this deployment
   refuses to lower it — and returned 19 distinct texts in 24 calls on one question.

**What this does not prove:** that query formulation is worthless in general. It is a null **for this
corpus**, where coverage is already saturated. A lever that does nothing at 0.98 coverage may matter
where coverage is genuinely distributed — which is exactly the regime `making-retrieval-measurable-again.md`
costs out, and exactly why that document recommends not buying it until there is a candidate worth
ranking. There now isn't one.

## 5. What was kept

The mechanism ships as **opt-in and off by default** (`--query-formulation verbatim`), byte-for-byte
the historical retrieval path. It is kept rather than reverted because the *instrument* is the durable
asset: if a future corpus is built where retrieval can be discriminated, the arm already exists and its
witness already works.

The witness is the other thing kept, and it earned itself twice. Its first version compared
`changed / derived` — which read 100% on a run where the formulator saw two questions of fifty and
rewrote both, reporting `voidReason: null` on an arm that had barely run. It now checks coverage *and*
effect. **A witness satisfiable by a sample of two is not a witness.**
