# StructuredJson judge protocol: reachable, and currently rejected

**Status:** blocker specified, not fixed. **Date:** 2026-08-13.

## Why the protocol matters

AgentEval's free-text verdict parser *"vetoes a leading yes when the word no appears later in the
response"*. A judge answering "yes — there is no discrepancy" is therefore scored as a failure. That
is a **systematic** mis-scoring, not noise, and `JudgeVerdictProtocol.StructuredJson` is the fix.

It was also unreachable: the parameter existed on `CreateOptions` and every call site took the
default. It is now selectable via `--judge-protocol free-text|structured-json` (default unchanged),
and the choice is emitted into the report's protocol block so a StructuredJson score can never sit
beside a FreeText one without the difference being visible.

## What the first run found

`longmemeval-prepared-20260813T001444Z` — 2 questions, `--judge-protocol structured-json`. **Rejected.**

The expected risk was *incomparability with a free-text base*. The actual blocker is different and
more concrete: **our own run validator is FreeText-shaped and refuses a StructuredJson run outright**,
on two independent counts.

**1. Call accounting.** The validator observed 4 LLM calls, classified 2 of them as diagnostic judge
retries, and was left with **0 base judge calls against an expected 2–6**. The StructuredJson judge's
call pattern is not the arithmetic that guard encodes.

```
structured: Observed 2 judge calls (0 base after excluding 2 diagnostic retries)
            for 2 questions; expected between 2 and 6 base judge calls.
```

**2. Correctness reconciliation.** Fired for *every* question in *both* arms:

```
structured: AgentEval judge verdict and recorded correctness disagree for question bc149d6b.
hybrid:     AgentEval judge verdict and recorded correctness disagree for question 5831f84d.
```

That says our recorded correctness is still derived on the free-text path regardless of which
protocol the judge actually ran under.

## Why the guard was not loosened

The obvious way to get a green run is to widen the call-count bounds and relax the reconciliation.
**That guard exists to catch exactly this class of anomaly.** Adjusting it so that the run which
provoked it passes produces a number that looks measured because the thing that would have objected
was tuned away — which is the failure this evaluation track exists to prevent.

## Root cause (traced 2026-08-13)

Both symptoms are one bug. `LongMemEvalRunValidator` reconciles with:

```csharp
TryParseJudgeVerdict(question.JudgeExplanation, out var judgedCorrect)
...
if (question.Correct != judgedCorrect) // disagreement
```

`question.Correct` is **AgentEval's own verdict**. `judgedCorrect` is **our re-parse of the
free-text explanation**. Under StructuredJson the explanation is no longer yes/no prose, so our
re-parse yields the wrong boolean — and the same failed parse is what triggers the post-run
diagnostic judge retry, one per question, which is what drove base judge calls to zero.

Confirmed against the package: `AgentEval.Memory.External.Models.QuestionResult` exposes
`Correct`, `JudgeExplanation`, `JudgeRawResponse`, `JudgeReasoning`, `JudgeStatus`, `RawScore` — and
**no structured verdict property**. So there is nothing to read a verdict *from* except `Correct`
itself.

**Which makes the fix principled rather than a loosened bound.** The reconciliation exists to catch
AgentEval's *free-text parser* mis-scoring — precisely the bug StructuredJson eliminates. Under that
protocol the cross-check is not applicable: re-parsing prose to second-guess a structured verdict is
checking the thing the protocol removed. Skipping it there does not weaken the guard, because the
guard's subject no longer exists; keeping it there is what produces a false rejection.

The same reasoning disposes of the call accounting: the diagnostic retry repairs unparseable
free-text verdicts, so under StructuredJson it should never fire, and once it does not,
`baseJudgeCalls == questionCount` and the bound is satisfied without being touched.

## What is actually needed

1. Plumb the active `JudgeVerdictProtocol` into `LongMemEvalRunValidator` and
   `LongMemEvalPostRunDiagnostics` — neither currently knows which protocol ran.
2. Under StructuredJson: take `question.Correct` as the verdict and skip both the free-text re-parse
   and the diagnostic retry. **Do not widen the call bounds** — with the retry suppressed they are
   already satisfied, and bounds loose enough to admit both shapes admit real anomalies too.
3. Only then re-run, and report it as a protocol change on a fresh base — never by flipping the
   default, because every sealed base here is free-text.

Estimated S–M against AgentEval's contract. The run was worth its cost for turning a guessed-at
comparability caveat into this list.
