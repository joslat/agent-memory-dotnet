# Pre-registration: does query formulation move retrieval? (27.4)

**Written before the run, on purpose.** Every decision rule below is fixed in advance, because this is
the last untested retrieval lever and the temptation to read a null result generously will be highest
exactly here. Pre-registration is what let 22.4 be decided from a control arm without buying the
treatment, and what let 8.3b be decided without ~96M tokens.

---

## 1. The hypothesis, and why it is worth testing at all

The retrieval query is **the question text, used verbatim**. No rewriting, no expansion, no
hypothetical-answer generation. That is a real, standard, untested lever.

The data supports testing it, weakly and specifically. Of six hybrid failures:

| Failure | Session cov | Turn cov | Retrieval-shaped? |
|---|---:|---:|---|
| `gpt4_8279ba03` | **0.0** | 0.0 | **Yes** — found nothing |
| `352ab8bd` | 1.0 | **0.0** | **Yes** — right session, wrong turns |
| `51c32626`, `195a1a1b`, `852ce960`, `a2f3aa27` | 1.0 | 1.0 | No — exact evidence retrieved, still wrong |

**Two of six.** And one of those two (`352ab8bd`) is now known to be **oracle-impossible** — 0/8 with
perfect context — so it cannot be fixed by any retrieval change.

**That leaves one question of fifty with a plausible retrieval fix.** The ceiling is stated here, in
advance, so a result of "no change" is read as confirmation rather than disappointment.

## 2. What is measured, and it is not accuracy

**Primary endpoint: gold-TURN coverage**, not accuracy.

This is the whole reason 27.1 came first. Turn coverage separates outcomes (correct 0.937 vs wrong
0.667) far better than session coverage does (1.000 vs 0.833), and until 27.1 it read 0.000 on every
structured question — the arm that ships was blind to the only signal with a lever behind it.

Accuracy is a **secondary** endpoint and is expected to be flat. With one movable question in fifty,
accuracy cannot resolve a real effect: one question is 2 points, and the measured answer-model noise is
larger than that (19 distinct texts in 24 calls at temperature 1.0, which this deployment will not let
us lower).

**Reporting accuracy as the primary endpoint here would guarantee a null result regardless of the
truth.** That is the trap this section exists to close.

## 3. Arms

| Arm | Query sent to retrieval |
|---|---|
| **Control** | The question text, verbatim — exactly what ships |
| **Rewrite** | One model call: restate the question as a standalone search query |
| **Expansion** | The question plus generated near-synonyms and entity aliases |

The frozen corpus is reused, so **retrieval is the only variable**. Same corpus, same budget, same
answer model, same judge.

## 4. Decision rules, fixed now

1. **Ship a formulation arm only if it raises mean gold-turn coverage on the structured arm by more
   than the control's own spread across its two accepted runs.** If the control's spread is unmeasured
   (see 25.7 — different denominators are not repeats), the arm must be run twice to establish one.
   No spread, no claim.
2. **A coverage gain with no accuracy change is still a positive result**, and is reported as a
   retrieval result, not an accuracy one. A coverage gain that *lowers* accuracy is a negative result
   and kills the arm — retrieving more of the right turns while answering worse means the arm changed
   something other than retrieval.
3. **If coverage does not move on any arm, query formulation is retired**, and this document becomes
   the record of why — like routing, decomposition, precision and representation before it.
4. **The two oracle-impossible questions in the sample are excluded from the improvable denominator**
   and named in the report (27.3).

## 5. Void witness

A run where the rewriter **returned the input unchanged**, or failed and fell back to the verbatim
query, measured nothing and must print VOID rather than "no difference".

Concretely: the run records, per question, whether the emitted query differs from the original. If
**fewer than 80%** differ, the arm is void and exits non-zero. This is the same guard that fired on the
decomposition smoke run, where the decomposer declined to split and the harness said so instead of
reporting a flat line.

## 6. Cost, and the stop condition

~250–300 calls: 50 questions × (1 rewrite + 1 answer + 1 judge) × 2 arms, on the existing frozen
corpus. No rebuild.

**Stop after the first arm if its void witness fires or its coverage delta is under half the control
spread.** There is no case where a third arm is worth buying once the second has shown nothing.

## 7. What this cannot establish

- **Not a production accuracy claim.** One movable question of fifty, and answer-model noise larger
  than one question.
- **Not transferable to a bigger corpus.** Coverage is saturated at 0.965–0.980 here (see
  `making-retrieval-measurable-again.md`); a lever that does nothing at saturation may still matter
  where coverage is genuinely distributed. A null here is a null **for this corpus**, and the write-up
  must say so rather than retiring the idea universally.
