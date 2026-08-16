# Procedure retrieval precision: the measured result, and the bug it found first

**Task 26.2.** The first real measurement of *whether procedural recall returns the **right**
procedure* — as opposed to whether following one saves a tool call, which is
`procedural-benefit-result.md`.

Artifact: `artifacts/evaluation/procedure-retrieval-*.json`. Cost: **embedding calls only** — no chat
model, no judge.

---

## 1. What ran first: two shipped bugs

The instrument found both on its first execution. Neither was reachable by any unit test.

### 1.1 Promotion had never worked

`PromoteAsync` wrote `kind.ToString()` — **`"Procedure"`**. Every Cypher filter and the C# read-back
compare case-sensitively against lowercase **`"procedure"`**. The trace-creation path, six hundred
lines away in the same file, always wrote the lowercase form. Only the promote path disagreed.

Consequences, all silent:

- a promoted trace **read back as `Episode`**;
- it was **invisible to `proceduresOnly` recall** — the retrieval this whole feature exists for;
- and it was **never exempt from retention pruning**, which is promotion's main purpose. That one
  loses data: a procedure promoted specifically to survive pruning was pruned like any episode.

Fixed by centralising the stored spelling in one helper — two write sites had disagreed for as long
as promotion existed — and by normalising the four Cypher comparisons with `toLower(...)`, so traces
already written as `"Procedure"` start working **without a migration**.

### 1.2 The owner-scoped fallback scan crashed

`SearchByTaskVectorOwnerScopedFallback` emits `AND t.success = $successFilter` whenever a success
filter is requested; the scan's parameter dictionary never bound it. Every owner-scoped trace search
carrying a success filter threw **`Expected parameter(s): successFilter`** on reaching that
last-resort scan.

Not a rare path here: a `proceduresOnly` search returns zero from the indexed pass **by construction**
when the corpus holds no promoted procedures — which, thanks to bug 1.1, was *always*.

The correct binding already existed twenty lines up the call site, in a dictionary that was built and
**never passed to anything**. The fix was written and left unwired.

## 2. The result

12 procedures, 20 queries — 14 answerable, **6 where abstaining is correct**.

| minScore | correct | wrong | abstained | **missed** | wrongRate | precisionWhenAnswering |
|---|---:|---:|---:|---:|---:|---:|
| 0.00 – 0.86 | 13 | 7 | 0 | 0 | 35.0% | 65.0% |
| 0.90 | 13 | 4 | 3 | 0 | **20.0%** | 76.5% |
| **0.92** | **12** | **1** | **5** | **2** | **5.0%** | **92.3%** |
| 0.94 | 5 | 0 | 6 | 9 | 0.0% | 100.0% |
| 0.96 | 4 | 0 | 6 | 10 | 0.0% | 100.0% |
| 0.98 | 3 | 0 | 6 | 11 | 0.0% | 100.0% |

### 2.1 The default threshold is inert

**Every threshold from 0.00 to 0.86 produces an identical result.** The cosine similarities here are
compressed high, so `RecallOptions.MinSimilarityScore` — which defaults to **0.7** — sits in the dead
zone. At the shipped default, **procedure retrieval never abstains**: it returns its best match for
every query, including the six where nothing applies.

That is the safety characteristic this instrument exists to measure, and it is the bad one. An agent
with no procedural memory investigates; an agent handed a confident wrong procedure executes.

### 2.2 The knee is 0.92

`0.90` is a **free** improvement: wrongRate 35% → 20% with no correct answers lost and no misses.
`0.92` is the knee — wrongRate **5%**, precision-when-answering **92.3%**, five of six abstain cases
caught, at a cost of two misses.

**Recommendation: procedure retrieval needs its own, much higher threshold than semantic recall.** A
value tuned for facts is not a value tuned for methods, because acting on the wrong method is more
expensive than retrieving the wrong fact.

### 2.3 Above 0.94 the perfect scores are fake

`0.94`, `0.96` and `0.98` all report **0% wrong and 100% precision when answering**. They are the worst
settings in the table: 9, 10 and 11 **misses**.

**This is what the `Missed` column is for, and it was added hours before this run.** The instrument
originally counted every empty retrieval as an *abstention* — the column its own documentation calls
"not a failure". Under that scoring, `0.98` would have read as flawless, and the recommendation coming
out of this document would have been to ship it. The bug was invisible while the only caller was a unit
test supplying its own expectations; it needed a real labelled set to surface.

## 3. What this does not establish

- **Twelve procedures is a small library.** Wrong-procedure rate rises with the number of plausible
  competitors, so these figures are optimistic for a large store.
- **One embedding model.** The whole shape of the table follows from the similarity distribution, which
  is a property of the model, not of the memory system.
- **Not an accuracy.** Four outcomes are reported separately and abstention is not a failure. Collapsing
  them into one percentage is the metric substitution this project keeps refusing — and §2.3 is the
  concrete demonstration of what that substitution costs.
