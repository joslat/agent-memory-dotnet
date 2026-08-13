# Decomposed answering: measured at perfect context, and killed

**Run:** `artifacts/evaluation/oracle-decomp-n30.json`, 2026-08-13. 30 questions, seed 42, both arms,
same judge, same deployment. **192 provider calls.** No Neo4j, no Docker, no prepared corpus — the
oracle reads gold sessions from the dataset.

## Why it was run

Across 62 recorded reports, **65 of 67** wrong answers had the gold evidence already retrieved or
present. The loss is at the answering stage, where no retrieval change can reach it — which is why
memory-type *routing* came out with a ceiling of one question in fifty. This measured the other
stage: the same question answered twice from the **same** gold context, once monolithically and once
decomposed into sub-questions whose answers are then composed. Retrieval held perfectly constant;
decomposition the only variable.

Perfect context is deliberately the most favourable condition decomposition will ever see. The
experiment was built to kill, not to endorse: if it cannot win here, it cannot win on real retrieval.

## Result

```
comparable 29 · both correct 27 · both wrong 0
decomposed-only 0 · monolithic-only 2
decomposed 12/29 · inconclusive 1
calls monolithic=60 decomposed=132
```

| | Value |
|---|---|
| Discordant pairs favouring decomposition | **0** |
| Discordant pairs against | **2** |
| Questions actually decomposed (the witness) | 12 of 29 — the mechanism ran |
| Cost | **2.2×** (132 calls vs 60) |
| Call-accounting mismatches | **0 of 30** |

**Pre-registered kill criterion: "kill if discordant favouring decomposition ≤ against."** 0 ≤ 2.
Decomposed answering is killed on measurement, at the most favourable condition available to it, at
2.2× the price.

McNemar exact on (0, 2) is p = 0.5, so this is not statistically significant *against* decomposition
either. The rule does not require significance to kill — it requires evidence *for*, and there is
none. The honest headline is that the best possible conditions produced **zero wins**.

## Why it lost — both losses are the same defect, and it is a design choice

The composer is denied the source context by design: it sees only the sub-question/answer pairs.
That is what would have made a *win* attributable to decomposition rather than to the extra
completion. It is also exactly why the arm lost.

**`6d550036` (multi-session, aggregation).** Decomposed into one sub-question — itself. The
sub-answer came back truncated (*"1 project explicitly… The memory"*), and the composer had only that
fragment. Monolithic saw the full context and found 2. An aggregation question decomposed into itself
gains nothing and loses the composer's access to the evidence.

**`ba61f0b9` (knowledge-update).** The sub-answer correctly reported the store as inconsistent —
5 women in one session, 6 in a later one. The composer, instructed to surface contradictions rather
than choose, refused to answer. Monolithic applied recency: *"the most recent mention says 6."*

The second is the sharper finding. **For knowledge-update, a contradiction is not an error — it is
the answer.** Later supersedes earlier, and resolving it requires the ordering that lives in the
context. The composer, denied context, has no supersession signal and structurally cannot resolve
what the task is about. That is one of six task types where decomposition destroys the information
needed to answer.

A production decomposer would hand the composer the context to fix this — at which point it is no
longer testing decomposition, and a win would be unattributable. The isolation that makes the
experiment clean is the same thing that makes the arm lose, and there is no version of the design
that escapes both.

## The finding that outlives the kill

**The monolithic oracle scored 27 of 29 — 93% — at perfect context.** There is roughly 7% headroom
for *any* answering-stage improvement, and decomposition lost both available questions.

That reconciles two facts that looked contradictory. "65 of 67 failures had gold present" was
measured at **real** retrieval, where the context is noisy and the gold sits among competitors.
At **clean** context the model is right 93% of the time. So gold being *present* is not the same as
the context being *usable*, and the gap between 93% (clean) and ~88% (real, hybrid) is not an
answering-stage problem at all.

**The implied lever is context precision — fewer wrong items, not more right ones, and not a
different answering strategy.** That is a retrieval-side property, but a different one from recall:
it is about what gets *excluded*. Nothing in the current plan measures it.

## What this closes

- Decomposed answering (option (b)): **killed**. Do not build `AgentMemory.Composition`.
- Query decomposition for compound queries: already out of scope at **2 of 500 (0.4%)**.
- Memory-type routing on accuracy: already capped at 1 question of 50.

Retained: the **hybrid cost** hypothesis, which was never an accuracy claim — hybrid buys +0.24
accuracy points over structured at 6.21× the context tokens, and one binary classifier
(the act-of-telling cue, 96.4% / 0%) may recover most of that gap.

## What was built, and is worth keeping

`LongMemEvalOracleComparison` and `LongMemEvalDecomposedOracle` stay. The comparison is a general
paired-arm instrument with a void witness, and it earned itself twice on first contact: it caught a
two-question smoke run where nothing decomposed and refused to report "no difference", and its call
accounting matched behaviour on 30 of 30 questions including retries. The `--oracle-decomposition`
verb needs no infrastructure, so any future answering-stage hypothesis can be tested against perfect
context for ~200 calls before anything is built.

---

# Addendum: context precision is not the lever either

**Run:** `artifacts/evaluation/context-precision-n30.json`, 2026-08-13. Same 30 questions, seed 42,
242 calls. Recall pinned at **100%** — every gold message present at every level — with distractor
sessions drawn from the question's own haystack.

| K (distractor sessions) | Correct | Accuracy | Mean context chars | Questions with distractors |
|---:|---|---:|---:|---|
| 0 | 28/29 | **96.6%** | 30,668 | 0/30 |
| 3 | 29/30 | **96.7%** | 59,080 | 30/30 |
| 10 | 28/29 | **96.6%** | 128,900 | 30/30 |
| 25 | 29/30 | **96.7%** | 281,399 | 30/30 |

**The context grew 9.2× and accuracy did not move.** The lead proposed one turn earlier — that the
gap between clean-context (~96%) and real-run (~88%) accuracy is caused by wrong material sitting
beside the right answer — is wrong.

**What this can and cannot say.** With one error at K=0 the ceiling effect is severe: this rules out
a *large* degradation, not a 1–2 point one. A drop to 90% (3 errors) would have been visible; a drop
to 95% would not. So: noise is not the 8-point explanation, and might still be a small term.

## Where that leaves the gap

Clean-context oracle ~96%; real hybrid runs ~88%. Three candidate explanations are now eliminated or
bounded:

| Candidate | Status |
|---|---|
| Answering strategy (decomposition) | **Eliminated** — 0 wins of 29 at perfect context |
| Context noise / precision | **Eliminated as a large term** — 9.2× context, no movement |
| Retrieval recall in the "gold present" sense | Already bounded — 65 of 67 failures had gold present |

What remains, and is untested:

1. **"Gold present" is over-counted.** `RetrievedGoldCoverage` is a *fraction*, and several failures
   sit at 0.43–0.58 — half the gold. The answer-presence gate is token overlap, which is weak, and on
   abstention questions it matches the refusal sentence's own words. "Present" may mean "some of it".
2. **Representation loss.** The oracle reads raw messages with timestamps and speakers. Structured
   memory reads triples. The three named episodic gaps — speaker-acts, ordinal position, event
   participants — are all things raw text carries and a triple has no slot for. This is the candidate
   the evidence most supports and nothing has measured.

**The decisive next experiment** is to give the oracle the *structured* representation of the same
gold sessions instead of the raw text, with recall still pinned at 100%. If accuracy falls from ~96%
toward ~88%, the loss is in **extraction**, not retrieval and not answering — and the schema-gap work
becomes the highest-value item in the plan rather than a per-type detail. It needs extraction calls
over gold sessions only, not a corpus build.
