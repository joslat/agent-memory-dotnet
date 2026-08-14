# LongMemEval-S: what this system scores, and what those numbers are worth

**Phase 23.** Every figure here comes from an artifact in `artifacts/evaluation/`. Where two runs of
the same configuration disagree, both are shown — a single number would be more quotable and less true.

---

## 1. The headline

Structured memory reaches the full-history band while sending **304× fewer tokens per question**.

| Arm | Accuracy | Mean context / question | Artifact |
|---|---:|---:|---|
| **No memory** (floor) | **0%** (0/19 over two runs) | ~42 tokens | `longmemeval-reference-nomemory-*` |
| **Raw** | **90.0%** (45/50, one run — see §6) | — | `raw-arm-50q.json` |
| **Structured** | **76.0% – 90.0%** | **403 tokens** | `longmemeval-prepared-*` |
| **Hybrid** | **84.0% – 90.0%** | 2,505 tokens | `longmemeval-prepared-*` |
| **Full history** (ceiling) | **80% – 100%** (18/20) | **122,605 tokens** | `longmemeval-reference-fullhistory-*` |

The floor is the important control and it is genuinely 0: with no memory the model answers **none** of
these questions. Everything above 0 is memory doing work.

**The comparison worth making is the last column against the first.** Structured is not meaningfully
below full-history on accuracy, and it is two and a half orders of magnitude below it on context. On a
50-question run that is ~20 thousand tokens instead of ~6.1 million.

## 2. The honest part: these are bands, not points

Two runs that the harness **accepted**, same configuration, same 50 questions:

| Run | Structured | Hybrid |
|---|---:|---:|
| `20260810T092614Z-reuse-20260810T163701Z` | 76.0% | 90.0% |
| `20260812T140253Z-reuse-20260813T221547Z` | **90.0%** | 84.0% |

**Structured moved 14 points between two accepted runs.** Hybrid moved 6, and in the *opposite*
direction. The two arms swap places depending on which run you read.

So any claim of the form *"structured beats hybrid"* — or the reverse — is unsupported by this data.
Three things drive that spread, and only the first is about memory:

1. **Extraction nondeterminism** — the two runs were built from different corpora.
2. **Answer-model nondeterminism** — the answer call runs at temperature 1.0, which this deployment
   refuses to let us lower. Measured directly: the same question answered 6 times returned
   **19 distinct texts in 24 calls**. See `quality-effort-and-what-did-not-move.md` §2.2.
3. **Judge disagreement** on borderline answers.

**Report the band.** A point estimate from one run of this benchmark is not reproducible, and we have
the receipts to prove it about our own numbers.

## 3. Per memory type

From the most recent accepted 50-question run. **Read the `n` column first** — these subsets are small,
and one question in a 4-question subset is 25 points. The per-type noise band is now measured by the
instrument rather than estimated from question count.

| Memory type | Structured | Hybrid | n | 1 question = | measured band |
|---|---:|---:|---:|---:|---|
| **Semantic** | **25/25 = 100%** | 22/25 = 88% | 25 | 4.0 pts | *not measured* |
| **Temporal** | 18/21 = 85.7% | 17/21 = 81.0% | 21 | 4.8 pts | **±0.0 pts** (2 runs) |
| **Episodic** | 2/4 = 50% | 3/4 = 75% | **4** | **25 pts** | *not measured* |

Produced by `--typed-report`, not by hand. **"Not measured" is not "zero".** Only two 50-question runs
exist, and they sampled *different numbers* of each type — 23 vs 25 semantic, 6 vs 4 episodic. Runs with
different denominators score different question sets, so their difference is not this configuration's
noise. Temporal is the one type with the same denominator in both runs (21), and there the two runs
agreed exactly.

An earlier draft of this table reported **±17.4 points** for semantic. That figure was wrong: it came
from pooling the 23-question and 25-question runs, so it measured the sampling difference and labelled
it measurement error. The instrument now refuses that comparison.

Adjusting for the oracle-impossible question in the episodic subset (`352ab8bd`, see §5), episodic
improvable is **2/3 structured and 3/3 hybrid** — which is a different story from "50%", and is also
n=3 and therefore not a story at all. **Episodic is not measurable at this sample size.** The correct
statement is that we do not know its accuracy, not that it is 50%.

Two memory types are missing from this table because LongMemEval-S cannot test them:

- **Procedural** — no procedural questions exist in the dataset at any sample size. Measured separately
  and positively; see `procedural-benefit-result.md`.
- **Prospective** — not measurable at all without a time-grounded corpus.

**Metamemory** (abstention) is measured on a different sample: 18/20 correct.

## 4. Coverage is a step function, not a slope

Pooling questions by their realised gold coverage, with recall as the only variable:

| Gold coverage | Accuracy |
|---|---:|
| 1.00 | 100% |
| 0.75 – 0.99 | 100% |
| **0.50 – 0.74** | **22.7%** |

Completeness is worth ~80 accuracy points — and it falls off a cliff rather than degrading gracefully.
**But the system already sits at 0.965–0.980 coverage**, on the flat part of the curve, so this is a
strong result about a regime it does not enter. It is the reason retrieval work has stopped paying.

## 5. What is excluded, and why

Four questions are answered wrongly by a **perfect-context oracle 8 times out of 8** — handed exactly
the evidence the dataset says answers them, with no retrieval involved. No memory system can reach
them, so they cap the score for reasons unrelated to memory.

`352ab8bd`, `58470ed2`, `7a8d0b71` (all `single-session-assistant`) and `bf659f65` (`multi-session`).

They are **reported separately, never deleted**: every run now carries both the raw and the improvable
denominator, the excluded ids, the evidence for each, and a contradiction flag that fires if one is
ever answered correctly. On the latest run the difference is 90.0% raw against 91.8% improvable.

Three of the four are the same question type — the smallest type in the dataset. That is a property of
the benchmark, not of anything measured against it.

## 6. What has never been measured

Stated because an absent arm is easy to mistake for a bad one:

- **Prospective memory**: no instrument exists.
- **Procedural at scale**: one task, one model. An existence proof, not an effect size. Retrieval
  *precision* is separately measured — see `procedure-retrieval-precision-result.md`.

### The raw arm, and why its number carries an asterisk

Run 2026-08-14 (task 23.3): **45/50 = 90.0%**, on a cold build with no extraction at all. Three
caveats, all of which must travel with the figure:

1. **The harness marked the run `accepted: false`**, for one reason: *"5 question(s) scored incorrect
   and no answer-presence measurement was recorded, so this run cannot distinguish an extraction
   failure from a retrieval failure."* That rejection is **inherent to raw mode rather than a defect in
   the run** — the graph probe is only wired for modes that extract, and raw does not extract, so there
   is no extraction step to attribute a failure to. The scoring itself (50 questions judged, 45
   correct) is sound. *Instrument note: the validator should exempt the raw arm from an
   attribution requirement it cannot satisfy by construction.*
2. **Different corpus.** Raw built its own store; structured and hybrid ran on the frozen prepared
   corpus. So `raw 90.0%` and `structured 90.0%` are **not** the same measurement of the same thing,
   and the coincidence of the numbers is not evidence they are equivalent.
3. **One run.** Structured moved 14 points between two accepted runs; there is no reason to think raw
   is steadier. This is a point estimate with an unmeasured band, which is exactly what §2 warns
   against — recorded here because an absent arm is easy to mistake for a bad one, not because one run
   settles anything.

## 7. How to cite this

- ✅ *"Structured memory reaches the full-history accuracy band on LongMemEval-S using 403 tokens of
  context per question against 122,605 — roughly 1/300th — over a 0% no-memory floor."*
- ✅ *"Semantic 100% (n=25); temporal 85.7% (n=21); episodic not measurable at n=4."*
- ❌ *"90% on LongMemEval-S."* — true of one run; the same configuration also produced 76%.
- ❌ *"Structured outperforms hybrid."* — the two accepted runs disagree on the sign.
- ❌ Any use of the overall accuracy figure as evidence about **procedural** or **prospective** memory.
  The dataset contains no such questions.
