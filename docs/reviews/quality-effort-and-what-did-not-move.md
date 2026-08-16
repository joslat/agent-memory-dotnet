# The quality effort: what was tried, what did not move, and why

**Period:** 2026-08-13 → 14. **Provider spend:** ~1,800 calls across nine experiments.
**Net change in benchmark accuracy: none attributable to any of it.**

That last sentence is the point of this document. Nine experiments, six architectural candidates
eliminated, and the accuracy number did not move — because the search found a *ceiling*, not a lever.
Recording why matters more than recording a delta would have, because the next person will otherwise
retry these in the same order.

---

## 1. What was tried, and what each one cost

| # | Candidate | Method | Cost | Result |
|---|---|---|---:|---|
| 1 | **Memory-type routing** | Archive analysis + movability decomposition | free | Ceiling **1 question of 50** |
| 2 | **Decomposed answering** | Same question answered twice from the same perfect context | 192 calls | **0 wins of 29**, 2 losses, 2.2× cost |
| 3 | **Context precision** | Distractor sweep, recall pinned at 100% | 242 calls | 9.2× context, accuracy **flat** |
| 4 | **Structured representation** | Extract from gold sessions, answer from triples | 93 calls | **~1 question** vs raw |
| 5 | **Gold completeness** | Drop gold sessions, recall the only variable | 242 calls | **95% → 15%** when halved |
| 6 | **Fine coverage curve** | Pooled by realised per-question coverage | 242 calls | A **step**, not a slope |
| 7 | **Coverage levers** | Pre-registered control on the frozen corpus | ~200 calls | Coverage already **0.965** — untestable |

Two of these produced findings that *look* like wins and are not: completeness is worth eighty points
but is already saturated in practice, and the step function is a strong result about a regime the
system never enters.

---

## 2. Why nothing moved: the ceiling, stated precisely

### 2.1 Four questions are unanswerable with perfect information

> **Corrected 2026-08-14.** An earlier version of this section named `352ab8bd` (0/36), `58470ed2`
> (0/36), `09ba9854` and `031748ae_abs` as oracle-impossible. **The archive did not support that.**
> The oracle had never been pointed at any of them — all 36 attempts were *retrieval* runs, where a
> wrong answer is ambiguous between "unanswerable" and "not retrieved". Task 27.3 made the oracle
> targetable by question id and settled it by measurement. Two of the four named questions do not
> belong on the list, and two that were never suspected do.

Measured directly, gold-only context, zero distractors, no retrieval, **8 independent attempts each**
(`--oracle-precision --distractor-sessions 0 --gold-fraction 1.0`,
artifacts `oracle-impossible-probe-r1..r8.json`):

| Question | Type | Perfect-context oracle | Verdict |
|---|---|---:|---|
| `352ab8bd` | single-session-assistant | **0/8** | Oracle-impossible |
| `58470ed2` | single-session-assistant | **0/8** | Oracle-impossible |
| `7a8d0b71` | single-session-assistant | **0/8** | Oracle-impossible — *newly identified* |
| `bf659f65` | multi-session | **0/8** | Oracle-impossible — *newly identified* |
| `031748ae_abs` | knowledge-update | 3/4 | **Solvable** — wrongly listed before |
| `gpt4_8279ba03` | temporal-reasoning | 4/4 | **Solvable** — a pure retrieval miss |

**What "0/8 with perfect context" means:** the model was handed exactly the evidence the dataset says
answers the question and got it wrong every time. No memory system can reach these. Under a coin-flip
null, 0-of-8 is p≈0.004 per question; four of them together are not a sampling accident.

**The pattern worth noticing:** three of the four are `single-session-assistant` — questions whose
answer was stated by the *assistant*. That is the smallest question type in the set and it holds three
quarters of the oracle-impossible questions. That is a property of the benchmark, not of any system
measured against it.

These are now excluded from the *improvable* denominator and reported beside the raw one, with the
exclusion named and its evidence carried in every report — plus a contradiction flag that fires if one
is ever answered correctly, because a curated exclusion list is exactly the kind of thing that decays
into a way of not counting inconvenient questions.

### 2.2 The intermittent failures are not retrieval failures

Across constant-configuration repeats, **13 of 14** cells that flipped verdict did so with
**identical `ItemsRetrieved`**. Same question, same corpus, same configuration, same items in the
context — different verdict.

**The cause is configuration, and it is ours.** The answer call passes **no `ChatOptions` at all** —
no temperature, no seed:

```
// AgentMemoryLongMemEvalAdapter: the answer call
chatClient.GetResponseAsync([system, user], cancellationToken: ct)
```

So the answer model runs at the provider default, which on this deployment is **temperature 1.0** —
the same deployment whose rejection of `temperature: 0` forced
`ProviderCompatibleExtractionChatClient` to exist on the *extraction* path. The answer path never got
the equivalent treatment, and nothing pins it.

**This means a meaningful share of the "noise band" that blunts the instrument is self-inflicted, and
has never been attacked.** It is not an inherent property of the benchmark.

### 2.3 The gold is present, and that is not the same as answerable

"65 of 67 wrong answers had gold present" was the finding that redirected the whole search. It is
true and it was over-read. Two refinements:

**Session coverage is not turn coverage.** On the hybrid arm:

| | mean session coverage | mean turn coverage |
|---|---:|---:|
| Correct answers (n=29) | 1.000 | **0.937** |
| Wrong answers (n=6) | 0.833 | **0.667** |

There *is* a turn-level signal — failures retrieve fewer of the specific annotated gold turns. But
**4 of the 6 hybrid failures have turn coverage 1.0**: the exact evidence turns were retrieved and
the answer was still wrong.

**Structured turn coverage is not measurable at all.** It reads 0.000 on every structured question,
correct and wrong alike, because turn attribution runs through recalled raw messages and the
structured arm has none. Task 22.3 made *session* coverage observable on that arm; *turn* coverage is
still blind. **This is an open instrument gap, not a finding** — see task 27.1.

---

## 3. The candidate that was never tested: query formulation

Worth stating plainly because it is the obvious next hypothesis and the data only *partly* answers it.

The retrieval query is the question text, used verbatim. No rewriting, no expansion, no hypothetical
answer generation. That is a real, standard, untested lever.

**What the data says about it:**

| Failure | Session cov | Turn cov | Could query formulation help? |
|---|---:|---:|---|
| `gpt4_8279ba03` | **0.0** | 0.0 | **Yes** — a genuine retrieval miss, nothing found |
| `352ab8bd` | 1.0 | **0.0** | **Yes** — right session, wrong turns: a ranking/query problem |
| `51c32626` | 1.0 | 1.0 | No — exact evidence retrieved |
| `195a1a1b` | 1.0 | 1.0 | No |
| `852ce960` | 1.0 | 1.0 | No |
| `a2f3aa27` | 1.0 | 1.0 | No |

**So 2 of 6 hybrid failures are retrieval-shaped and 4 are not.** Query formulation is worth testing
and is capped at roughly the same 1–2 questions per 50 as every other retrieval lever. It should be
tested *after* structured turn coverage becomes observable, because on the arm that ships we
currently cannot see the signal at all.

---

## 4. What the eliminations are actually worth

Each of these is a decision the project no longer has to make, and a body of work it no longer has to
build:

- **`AgentMemory.Composition` was not built.** A decomposed-answering package, its orchestrator,
  contradiction surfacing and reconciliation — retired for 192 calls.
- **Memory-type routing was not built.** A classifier, a routing policy, per-mode action spaces —
  retired by archive analysis, free.
- **A precision/re-ranking push was not started.** Retired for 242 calls.
- **~96M input tokens were not spent** on the episodic-default decision (8.3b), decided from
  artifacts instead.

The reusable instruments below cost less than any one of those would have.

---

## 5. The three oracle instruments, and what each isolates

All three read gold sessions **straight from the dataset**: no Neo4j, no Docker, no prepared corpus,
no retrieval. That is why nine experiments were affordable. Each pins a different variable.

| Verb | Holds constant | Varies | Answers |
|---|---|---|---|
| `--oracle-decomposition` | Context (gold only) | Monolithic vs decomposed answering | *Does the answering strategy matter?* |
| `--oracle-precision` | Recall at 100% (every gold message present) | Distractor sessions **K**, and gold fraction | *Does noise hurt? Does missing evidence hurt?* |
| `--oracle-representation` | Recall at 100%, gold sessions only | Raw messages vs extracted triples | *Is the representation lossy?* |

Each carries a **void witness**, because the common outcome of all three is "nothing changed" and
that is indistinguishable from "the mechanism never ran":

- decomposition: a run where nothing was split prints VOID and exits non-zero — **it fired on the
  first smoke run**, catching two questions the decomposer declined to split;
- precision: a level whose context did not grow, or a gold fraction that dropped nothing, voids —
  **it fired on `gold=0.85`**, which the ceiling made identical to the control;
- representation: an extractor returning nothing voids, because an empty context scores like a
  no-memory arm and would read as "the representation loses everything".

---

## 6. Was the wiring actually verified?

Yes, and the verification repeatedly caught real breakage — which is the reason to trust the negative
results rather than a reason to doubt them.

| Check | What it caught |
|---|---|
| Red-before-fix, every behavioural change | 6 separate confirmations this session |
| Decomposition smoke run (2 questions) | Decomposer declined to split → **VOID**, not "no difference" |
| Conjunction probe (2 named questions) | Split correctly into 2 sub-questions; **calls 5 = expected 5** |
| Call accounting per question | **0 mismatches across 30 questions**, retries included |
| Corpus reuse | Three separate gates failed and were fixed: fingerprint break, drift refusal (correct), `prepared-graph-mismatch` on all 50 |
| Coverage witness | Pre-registered "null on structured ⇒ void" — **one run was voided by it** |

The `prepared-graph-mismatch` case is the sharpest: a run started, reused the corpus, retrieved
**zero items on every question**, and was rejected on downstream judge noise. Without the per-question
diagnostics it would have looked like a catastrophic quality regression. It was a comparison asking
about a field the manifest was never able to record.

---

## 7. What this says about the instrument

**LongMemEval-S can no longer discriminate improvements to this system**, for three compounding
reasons:

1. **8% is structurally unwinnable** (four oracle-impossible questions).
2. **The answer model is unpinned**, so repeat runs disagree with themselves on ~13 of 14 movable
   questions.
3. **Retrieval is already near its own ceiling** — coverage 0.965/0.980, and 13 of 14 structured
   failures have full session coverage.

Between them, the band inside which a real improvement would have to appear is wider than any
improvement the remaining candidates could produce.

**The instrument is not broken. It has been out-run**, and it did its job first: it found the
owner-starvation bug, the extraction-nondeterminism finding, and the completeness step function.

---

## 8. What is worth doing about it

In order of value per unit of effort:

1. **Pin the answer model** (27.2). If the deployment honours a seed, a chunk of the noise band is
   self-inflicted and removable. Cheapest possible test, largest possible effect on *measurability*.
2. **Make structured turn coverage observable** (27.1). The arm that ships is blind to the one
   retrieval signal that still has a plausible lever behind it.
3. **Retire the oracle-impossible questions from published denominators** (27.3), with the exclusion
   named and justified rather than silent.
4. **Then, and only then, test query formulation** (27.4) — with an instrument that can see it.
