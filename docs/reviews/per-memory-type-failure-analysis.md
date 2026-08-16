# Per-memory-type scores, and root cause on every failure

**Run:** `longmemeval-prepared-20260812T140253Z` — 50 questions, seed 42,
`abstentionPolicy: TargetProportion`, cold build, 616 extraction calls, both arms.
**Date:** analysed 2026-08-13. **Cost of this analysis: zero provider calls** — every number below is
read off artifacts already on disk.

Structured **43/50 (86.0%)**, hybrid **44/50 (88.0%)**.

## Scores by memory type

Taxonomy revision `2026-08-12` (`tools/AgentMemory.LongMemEval/Taxonomy/memory-type-map.json`).
Abstention overrides the task label, so an `_abs` question scores as metamemory whatever it asks about.

| Memory type | Structured | Hybrid | n |
|---|---:|---:|---:|
| Metamemory (abstention) | 18/20 · 90.0% | 18/20 · 90.0% | 20 |
| Semantic | 12/13 · 92.3% | 11/13 · 84.6% | 13 |
| Temporal | 11/13 · 84.6% | 12/13 · 92.3% | 13 |
| Episodic | 2/4 · 50.0% | 3/4 · 75.0% | **4** |
| Procedural | — | — | 0 |

**Episodic n=4.** One question is 25 points. The structured/hybrid gap is a single question and means
nothing yet. **Procedural is 0 by construction** — LongMemEval-S contains no procedural workload at
any sample size, which the taxonomy records as unreachable rather than unmeasured. Procedural is
measured by its own harness (PLAN 7.6), not here.

## By task type

| Task type | Structured | Hybrid | answerable + abstention |
|---|---:|---:|---|
| single-session-user | 8/8 · 100% | 8/8 · 100% | 4 + 4 |
| multi-session | 15/15 · 100% | 13/15 · 86.7% | 7 + 8 |
| knowledge-update | 8/9 · 88.9% | 8/9 · 88.9% | 5 + 4 |
| temporal-reasoning | 9/12 · 75.0% | 11/12 · 91.7% | 8 + 4 |
| single-session-assistant | 2/4 · 50.0% | 3/4 · 75.0% | 4 + 0 |
| single-session-preference | 1/2 · 50.0% | 1/2 · 50.0% | 2 + 0 |

## Every failure, with its discriminator

`EvidenceLearned` was **true for all ten**, with gold source-message coverage 0.50–0.89. Extraction is
not the failure mode anywhere in this set — which reproduces the Phase 0 finding on a different run.
`RetrievedGoldCoverage` is what separates the categories.

| Question | Type | Fails in | retrGoldCov (str / hyb) | Root cause |
|---|---|---|---|---|
| `gpt4_8279ba03` | temporal-reasoning | **both** | **0 / 0** | **Retrieval miss.** 20 gold learned items exist; none retrieved in either arm. Both arms declined |
| `195a1a1b` | preference | hybrid | 0.889 / **0** | **Retrieval miss, hybrid only.** The negative preference ("not phone or TV") never reached context; the answer suggested screen-based activities |
| `gpt4_93159ced_abs` | temporal-reasoning | structured | **0.429** / 0.429 | Partial retrieval + asserted a job the user *has not started*. Hybrid passed on the same coverage — raw messages carry the tense that a triple does not |
| `0bc8ad92` | temporal-reasoning | structured | **0.579** / 0.579 | The event's **participant** ("with a friend") was not stored. Who was present is an episodic attribute a semantic triple drops |
| `031748ae_abs` | knowledge-update | **both** | 0.857 / 0.857 | **False presupposition.** Q names a role ("Software Engineer Manager") the user never held. Both arms answered from the nearest role fact |
| `a96c20ee_abs` | multi-session | hybrid | 0.846 / 0.846 | False presupposition (a poster presentation that never happened). Answered "Harvard University." Structured passed — it retrieved 9 items where hybrid retrieved 83 |
| `352ab8bd` | single-session-assistant | **both** | 0.875 / 0.875 | The number was stated by the **assistant**. `AssistantContentMode` is `Ignore`, so structured never stored it; hybrid had it in raw messages and still missed it — a ranking failure on the same question |
| `1903aded` | single-session-assistant | structured | 0.500 / 0.500 | Ordinal position in an assistant-generated list ("the 7th job"). Extraction destroys list order. Answered confidently and wrongly |
| `51c32626` | multi-session | hybrid | 0.833 / 0.833 | Date attribute lost — surrounding evidence retrieved, the date itself was not |
| `d24813b1` | preference | structured | 0.875 / 0.875 | **Judge error, not memory.** The answer matches the gold preference. This is the run's one validation issue and the class PLAN 3.7 fixed |

Categories, over the eight non-judge, non-presupposition failures: **two are clean retrieval misses**
(gold learned, zero retrieved), **one is a capture gap**, **five were retrieved and still answered
wrongly**. Retrieval and assembly are the ceiling. Extraction is not.

---

## Three structural findings

### 1. This corpus cannot exercise temporal memory at all

Chased while preparing an ablation that would have turned on `MemoryOptions.ResolveTemporalQueries`
and re-run the frozen corpus. **The ablation is dead, and it died for free.**

| Layer | What the corpus actually holds |
|---|---|
| `Message.TimestampUtc` | `DateTimeOffset.UnixEpoch.AddSeconds(counter)` — a synthetic ordering key, ~1970 (`AgentMemoryLongMemEvalAdapter.cs:1317`) |
| `Fact.created_at` | The **ingestion** clock — 2026-08-12 for this corpus (`FactQueries.cs:111`) |
| `Fact.valid_from` / `valid_until` | Never written; `TemporalValidityMode` ships `Ignore` |
| The conversation's real dates (2023) | Message *metadata* and the prompt text `Current Date: …` only |

The as-of path filters `node.created_at <= datetime($systemAsOf)`
(`CypherQueryRegistry.cs:61`). Resolving "10 days ago" against a 2023 question date and recalling
as-of that instant would exclude **every fact in the store**, because all of them were created in
2026. Enabling the option would not fail to help — it would empty the context, and the result would
read as the feature being harmful.

**So the temporal memory-type score (84.6% / 92.3%) is not measuring our bitemporal machinery.** It
measures whether date strings survive into the prompt. The taxonomy warns about metric substitution
for procedural; the same substitution is happening for temporal, one level deeper than it anticipated.

**A corrected attribution.** `gpt4_8279ba03` ("what kitchen appliance did I buy 10 days ago") was
first read as a temporal-resolution failure. It is not. `RetrievedGoldCoverage` is **0 in both arms**
with 20 gold learned items present — the fact was learned and never retrieved. No temporal feature
would have changed it.

### 2. A both-clocks default is wrong for the common question, and its failure is silent

`MemoryService.RecallAsync` routes a resolved temporal query to `RecallAsOfCoreAsync(request, asOf,
asOf)` — **both** the valid clock and the transaction clock. That is the right reading of *"what did I
think back in March"*: reconstruct past belief.

It is the wrong reading of *"what did I buy 10 days ago"*, which asks about the world at a past
instant using everything known **now**.

The parser cannot tell those apart, so the choice is which default is safer, and the failure modes are
not symmetric:

- **Valid-time only**, worst case: a later correction is applied to a past question. Usually desirable.
- **Both clocks**, worst case: on any host whose `created_at` is *import* time rather than
  conversation time — every backfill, every migration, every history import — a "what happened last
  month" query returns **nothing**, silently.

The second failure is total, silent, and hits a whole class of deployment. **Valid-time-only should be
the default; belief reconstruction should be opt-in.**

### 3. The answer-presence gate is meaningless on abstention questions

For an `_abs` question the gold answer is a refusal sentence, so the gate matches its *own* tokens:

```
031748ae_abs  MatchedTokens: [information, provided, enough, mentioned, role,
                              senior, software, engineer, but, manager]   Coverage 0.909
```

It called **19 of 20** abstention questions "answer present." PLAN 4.2 already found this at n=4 and
routed the sufficiency label to the dataset's own `IsAbstention` flag; the gate itself was never
fenced off. Any per-type capture or headroom figure that includes `_abs` rows is reading noise —
which matters, because 8.3c's episodic ceiling is computed from this gate.

### 4. Over-answering on a false presupposition is the one shared failure

Two of the three distinct abstention failures share a shape: the question presupposes something that
never happened (a role never held, a poster never presented). Retrieval returns the semantically
nearest rows, and **a near-match is rendered into the prompt identically to an exact match**. Nothing
in the assembled context says "you asked about X; this is about Y."

`031748ae_abs` retrieved gold at 0.857 coverage and the sufficiency signal read **0.92** — confidently
answerable — for a question that is unanswerable by construction. This is the one failure mode where
the memory layer, not the answer model, is what could carry the fix.

---

## What this decides, for free

| Question | Verdict |
|---|---|
| Run a temporal-resolution ablation on the frozen corpus? | **No.** The corpus is not time-grounded; the as-of transaction filter would empty the context |
| Is `gpt4_8279ba03` evidence for temporal query parsing? | **No.** It is a retrieval miss with gold present and zero retrieved |
| Can per-type capture/headroom figures include `_abs` rows? | **No.** The presence gate matches the refusal sentence's own tokens |
| Is extraction the ceiling on this run? | **No.** `EvidenceLearned` true on all ten failures |

### 5. The corpus this analysis depends on was one cold build from deletion

`am-lme-longmemeval-prepared-20260812t14-base-e5c49cf7cbd74c78b2a123eeae968b0d` — 616 extraction
calls, ~52 minutes, and **the only corpus that has ever run abstention questions** — was not in
`artifacts/evaluation/pinned-volumes.txt`. It was protected solely as "newest cold build", so the
next cold build would have demoted it into the removable set.

Worse, the pin file was resolved **against the working directory**, and a miss returned an empty pin
list with no message — so a launch from anywhere but the repository root treated every pinned corpus
as removable. The pin file's own header records a base already lost to this sweep, during a
`--preflight-only` run that omitted `--no-orphan-sweep`.

Both are fixed: the corpus is pinned, and `LongMemEvalOrphanSweep.PinFilePath` now anchors to the
repository root and warns loudly when no pin file is found. The pin list is gitignored local state,
so the volume name is recorded here as well — a note in a file nobody reads is how the first one was
lost.

## What still needs a decision

- **Valid-time-only as the routing default** (finding 2) — free, unit-testable, and it fixes a real
  silent-empty-recall bug for any host that imports history.
- **Time-grounding the corpus** — stamping messages with their session date and facts with their
  source time. It requires a rebuild, and without it no LongMemEval run can say anything about
  temporal memory.
- **Fencing `_abs` rows out of the presence gate** — free, and it re-opens the question of whether
  8.3c's episodic ceiling was computed over a clean denominator.
- **Episodic at a usable n.** Four questions cannot carry the assistant-content decision.
