# LongMemEval-S: what this system scores, and what those numbers are worth

**Phase 23.** Every figure here comes from an artifact in `artifacts/evaluation/`. Where two runs of
the same configuration disagree, both are shown — a single number would be more quotable and less true.

---

## 1. The headline

Structured memory reaches the full-history band while sending **304× fewer tokens per question**.

| Arm | Accuracy | Mean context / question | Artifact |
|---|---:|---:|---|
| **No memory** (floor) | **0%** (0/19 over two runs) | ~42 tokens | `longmemeval-reference-nomemory-*` |
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
and one question in a 4-question subset is 25 points.

| Memory type | Structured | Hybrid | n | 1 question = |
|---|---:|---:|---:|---:|
| **Semantic** | **25/25 = 100%** | 22/25 = 88% | 25 | 4.0 pts |
| **Temporal** | 18/21 = 85.7% | 17/21 = 81.0% | 21 | 4.8 pts |
| **Episodic** | 2/4 = 50% | 3/4 = 75% | **4** | **25 pts** |

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

- **The raw arm has never run at 50 questions.** Every 50q report contains `structured` and `hybrid`
  only. Raw needs no extraction, so this is cheap — it is simply not done.
- **Prospective memory**: no instrument exists.
- **Procedural at scale**: one task, one model. An existence proof, not an effect size.

## 7. How to cite this

- ✅ *"Structured memory reaches the full-history accuracy band on LongMemEval-S using 403 tokens of
  context per question against 122,605 — roughly 1/300th — over a 0% no-memory floor."*
- ✅ *"Semantic 100% (n=25); temporal 85.7% (n=21); episodic not measurable at n=4."*
- ❌ *"90% on LongMemEval-S."* — true of one run; the same configuration also produced 76%.
- ❌ *"Structured outperforms hybrid."* — the two accepted runs disagree on the sign.
- ❌ Any use of the overall accuracy figure as evidence about **procedural** or **prospective** memory.
  The dataset contains no such questions.
