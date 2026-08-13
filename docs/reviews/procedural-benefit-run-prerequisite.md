# 7.6's run has one prerequisite left, and it is a wiring gap

**Status:** harness, MAF runner and benchmark task all built and merged. **Do not run yet.**
**Date:** 2026-08-13.

## What was found while preparing the run

The Agent Framework provider **recalls** reasoning traces and **never writes one**.

- `Neo4jMemoryContextProvider` sets `MaxTraces` on recall when
  `AutomaticRecallCategories.ReasoningTraces` is enabled — that is the read side, and it works.
- Nothing in the provider's run path writes a trace. `AgentTraceRecorder` exists and can persist one,
  but it is gated behind `AgentFrameworkOptions.PersistReasoningTraces` (**default `false`**) *and*
  must be invoked explicitly by the host. It is not called as part of a normal agent run.

## Why that makes running now worse than not running

The procedural arm recalls procedures. Nothing stores one. So the arm would find nothing, both arms
would behave identically, and `ProceduralBenefitResult.ShowsBenefit` would return **false**.

That result would be indistinguishable from an honest negative — "procedural memory does not help" —
while actually measuring a wiring gap. It would cost real money to produce a number about the
harness rather than about the feature, and the number would look exactly like a finding.

**This is the same defect shape 7.6 was written to prevent**, arriving one layer further out than
expected: not "a promoted procedure is invisible to the instruments" (6.5 fixed that), but "no
procedure is ever promoted in the path the benchmark drives".

## What the run needs first

1. Set `AgentFrameworkOptions.PersistReasoningTraces = true` for the procedural arm only — it is the
   arm switch, and the control arm must keep the shipped default.
2. Invoke `AgentTraceRecorder` after each **successful** attempt, so the stored trace describes a
   procedure that actually worked. Recording failed attempts would teach the wrong chain, which 7.7's
   wrong-procedure rate would then correctly punish — but the benchmark would be measuring a bug we
   introduced rather than the feature.
3. Ensure the trace is stored as `TraceKind.Procedure`, not `Episode`. Owner-scoped procedural recall
   filters on `proceduresOnly`, so an episode-kinded trace is written, recalled by nothing, and
   presents as the same false negative.

Then the two arms differ in something real, and the harness measures the feature.

## Related

- `ProceduralBenefitResult` — completion-gated benefit scoring
- `ProcedureRetrievalPrecision` — wrong-procedure rate (7.7)
- `ProceduralBenchmarkTask` — the enforced-chain task
- `MafAgentTaskRunner` — step and tool-call counting off the transcript


---

## First run executed — and the result is about the task, not the feature

**Run:** `--procedural-benefit --attempts 3`, 2026-08-13. Promotion wiring in place.

```
procedures  completion=100%  meanSteps=4.0  meanToolCalls=3.0
control     completion=100%  meanSteps=4.0  meanToolCalls=3.0
stepReduction=0.0%  toolCallReduction=0.0%  completionDelta=0%
SHOWS BENEFIT: False
```

**This is not evidence that procedural memory does not help.** Three tool calls is the *minimum
possible* chain, and the log contains **zero refusals** — the agent walked
`LookUpTraveller → PlaceHold → Book` correctly on its first cold attempt, in both arms. There was
nothing to discover, so there was nothing a stored procedure could save.

The cause is my own task design, and it is the exact property the benchmark's tests declare as the
hard requirement: *the shortest correct path must be discoverable but not guessable.* I enforced that
in the tool **bodies** — booking without a hold is refused — but gave it away in the tool
**descriptions**, which state the prerequisites outright:

- *"Requires the traveller's loyalty tier."*
- *"Requires a hold reference."*

A competent model reads the descriptions and orders the calls correctly without ever being refused.
The enforcement is real and never fires.

### What the run does establish

The assembly works end to end, which was the open question: the arms are genuinely distinct, the
promotion path stores a procedure, and the counting produces figures off the transcript. A harness
that could not run at all would have failed here instead of returning a clean, uninformative zero.

### The fix

Withhold the prerequisites from the descriptions — name each tool's purpose and let the refusal
message teach the ordering. Then the control arm must discover the chain by being refused on every
attempt, while the procedural arm pays that cost once. Re-run after that change; the current numbers
should not be cited.


---

## Second run: descriptions cleaned, still no discrimination

Removed every prerequisite from the tool and parameter descriptions, and added two guards asserting
they stay out. Re-ran. **Identical result — zero refusals, 3 tool calls, both arms the same.**

So the leak was never only in the prose. `PlaceHold(connection, tier)` and `Book(holdReference)`
telegraph the chain through their **parameter names**: a model that sees a `tier` argument it cannot
fill goes looking for the tool that yields one. The information is in the signature, and a signature
cannot be obfuscated without making the task artificial in a different way.

**The honest conclusion: a three-step chain over semantically-named tools is not a procedural-memory
benchmark for a competent model.** There is nothing to discover. This is a property of the task
class, not of wording, and no amount of description-editing fixes it.

### What a discriminating task actually needs

An ordering the model cannot infer from names or types — the dependency has to be **arbitrary**:

- An opaque token obtainable only from a tool whose name does not suggest it (e.g. `book` requires a
  `clearanceCode` that only `check_weather` returns), so the chain is learnable but not guessable; or
- a longer chain where the *branch* taken depends on a value discovered mid-run, so a single stored
  procedure encodes a decision rather than a sequence.

Both are real design work, and both risk the opposite failure — a task so artificial that succeeding
at it says nothing about agents doing real work. That tension is why this is a task-design problem
and not a wording problem.

### Status

The harness, runner, promotion path and arm switch are all built, tested, and demonstrated working
end to end across two runs. **What is missing is a task hard enough to measure them with.** The
figures from both runs should not be cited as evidence about procedural memory in either direction.
