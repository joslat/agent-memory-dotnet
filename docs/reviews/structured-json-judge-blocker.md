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

## What is actually needed

1. Teach the run validator the StructuredJson judge's call shape, rather than widening the bounds
   until both shapes fit — bounds loose enough to admit both admit real anomalies too.
2. Read recorded correctness from the **structured verdict** when that protocol is in force, instead
   of from the free-text parse.
3. Only then re-run, and report it as a protocol change on a fresh base — never by flipping the
   default, because every sealed base here is free-text.

Estimated S–M against AgentEval's contract. The run was worth its cost for turning a guessed-at
comparability caveat into this list.
