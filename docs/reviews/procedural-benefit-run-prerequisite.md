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


---

## Third run: arbitrary dependency added, still no discrimination — and now the reason is structural

Added the dependency the second run's write-up called for: booking requires a `clearanceCode`, and
the only source is a **service bulletin** lookup — a tool whose name gives no hint. The refusal names
the missing code and never says where to find it. Unit tests pin both.

Result: **4 tool calls, both arms, zero refusals.** The model simply called *all four tools* before
booking.

**That is the structural finding, and it survives any amount of dependency-hiding:** with a small
tool set, exhaustive calling is a cheap and correct strategy. The agent never has to *discover* the
ordering because it never has to *choose* — it can afford to call everything. A stored procedure
saves nothing when the unguided policy is already near-optimal.

So the requirement is not "hide the dependency" but **make exploration expensive**:

- **A large action space** — dozens of tools, so calling them all costs more than the task is worth;
  or
- **irreversible or penalised wrong steps** — a wrong call that consumes a booking slot, charges a
  fee, or must be undone, so that exploring has a price the harness can see; or
- **a long chain**, where the number of orderings grows fast enough that guessing stops working.

All three make the benchmark meaningfully bigger. That is the honest scope of what is left.

### Three designs, one conclusion

| Attempt | Change | Result |
|---|---|---|
| 1 | Enforced chain, prerequisites in descriptions | 3 calls, 0 refusals — chain read off the prose |
| 2 | Prerequisites removed from all descriptions | 3 calls, 0 refusals — chain read off parameter names |
| 3 | Arbitrary dependency behind an unhinted tool | 4 calls, 0 refusals — agent called every tool |

The harness, runner, promotion path and arm switch are built, tested, and demonstrated working end to
end across all three. **None of these figures is evidence about procedural memory.** What is missing
is a task whose action space is large enough that exploration costs something.


---

## Fourth run: expensive action space — and the conclusion is about the feature

Implemented what the third run's finding called for: twelve plausible decoy tools alongside the four
real ones, so that calling everything costs sixteen invocations instead of four.

```
procedures  completion=100%  meanSteps=4.7  meanToolCalls=4.0
control     completion=100%  meanSteps=4.0  meanToolCalls=4.0
stepReduction=-16.7%   SHOWS BENEFIT: False
```

**Both arms still called exactly the four right tools.** The model selected them out of sixteen
without exploring, so the decoys cost nothing and created no discovery to save. And the procedural
arm came out *slightly worse* — 4.7 steps against 4.0 — the recalled procedure adding context the
agent then had to read past.

### What four attempts actually establish

The obstacle was never the wording, the parameter names, the arbitrariness of the dependency, or the
size of the action space. It is that **a competent model does not explore on this class of task at
all.** It reads tool descriptions and selects correctly on the first attempt. Procedural memory has
no exploration cost to remove because there is none.

That is a real result, and it is narrower than "procedural memory does not work":

> **For tasks where correct tool selection is inferable from tool descriptions — which is what a
> well-designed tool API is — a stored procedure saves nothing, and carries a small context cost.**

The place to look for a benefit is therefore tasks where the right action is *not* inferable from the
interface: undocumented sequencing constraints, environment-specific quirks, conventions learned from
failure rather than from a schema. Those are exactly the cases a human operator writes a runbook for,
and a runbook is what a procedure is.

| Attempt | Change | Tool calls | Verdict |
|---|---|---|---|
| 1 | Prerequisites in descriptions | 3 / 3 | chain read off the prose |
| 2 | Prerequisites removed everywhere | 3 / 3 | chain read off parameter names |
| 3 | Arbitrary dependency, unhinted tool | 4 / 4 | agent called every tool |
| 4 | Twelve decoys, 16-tool action space | 4 / 4 | agent selected correctly without exploring |

**Status.** The harness, runner, promotion path, arm switch and counting are built, tested and
demonstrated working across four runs. The instrument is sound; what it keeps reporting is that this
task class has nothing for procedural memory to do. That is worth knowing before building a larger
benchmark, and it is the finding to carry forward rather than the numbers.


---

## Fifth run: INVALID — the environment leaked state between arms

Attempt five added the thing the fourth run's finding called for: a convention discoverable only by
failing. `PlaceHold` refuses on a stale session, and the refusal deliberately never names
`refresh_session`, so the step is not inferable from any interface.

```
procedures  completion=100%  meanSteps=5.3  meanToolCalls=5.0
control     completion=100%  meanSteps=4.3  meanToolCalls=4.0
```

**These numbers are not usable, and the arithmetic is what gives it away.** The required chain is now
five calls — refresh, look up, hold, bulletin, book — yet the control arm averaged **4.0 calls at 100%
completion**. That is impossible for a chain of five. The only way to complete in four is to skip the
refresh, and the only way to skip it is for the session to already be refreshed.

**Cause:** `ProceduralBenefitProgram` constructs a single `ProceduralBenchmarkTask` and shares it
across all six attempts. `_sessionRefreshed` set by the first procedural attempt persisted into every
later attempt *and into the control arm*. The control did not discover the convention; it inherited it.

**This is my own error, of the same shape as the rest of this sequence.** The task's own test
`TheEnvironmentAnswersIdenticallyEveryTime` constructs a fresh instance per call and therefore passed,
while the program that actually runs the benchmark shared one. I verified the property I was thinking
about and not the object lifetime beside it — which is exactly the failure this whole track exists to
catch.

### The fix

Construct the task **inside** the agent factory, once per attempt, so the environment starts stale
every time. Then the control arm must discover the refresh on every attempt while the procedural arm
pays for it once — which is the difference the harness is trying to measure, and the first design in
five that could actually produce one.

Until that lands, **attempt five's figures should be treated as void**, not as another null result.
The four before it are genuine nulls; this one is a bug.


---

## Sixth run — and it invalidates the earlier conclusion, not just attempt five

Fixed the shared-state bug: the task is now constructed inside the agent factory, once per attempt.
The arithmetic is consistent again — six calls for both arms, which is the five-step chain plus one
stale refusal. Both arms hit the refusal, discovered the refresh, and finished.

```
procedures  completion=100%  meanSteps=6.3  meanToolCalls=6.0
control     completion=100%  meanSteps=6.0  meanToolCalls=6.0
```

**Both arms paid the discovery cost on every attempt.** The procedural arm did not skip it — and the
reason is not the task.

### The agent never had a memory provider

`ProceduralBenefitProgram.BuildAgent` composes `chatClient.AsAIAgent(...)` with the benchmark tools
and **no `AIContextProviders`**. There is no `Neo4jMemoryContextProvider` on either arm.

So the procedural arm **stored** procedures — promotion works, it is unit-tested — and then had no
way whatsoever to **read** them back. It was a plain agent writing traces nobody consulted.

### This retracts the "task class" conclusion

The write-up after attempt four concluded:

> *"A competent model does not explore on this class of task at all… procedural memory has no
> exploration cost to remove."*

**That conclusion is not supported by any run performed here.** Attempts 1–4 could not have shown a
benefit under any task design, because the procedural arm had no procedural memory. The nulls were
real observations of a system in which recall was never wired — which is a statement about this
harness, not about the feature or the task class.

The doc comment on `ProceduralBenefitProgram` claims the arms "differ in exactly two things": trace
recall and promotion. Only promotion was implemented. I wrote the claim and did not check it, which
is the sixth instance of the same failure in this sequence and by far the most consequential, because
it silently converted five runs into measurements of nothing.

### What is actually needed

Attach `Neo4jMemoryContextProvider` to the procedural arm's agent, with
`AutomaticRecallCategories.ReasoningTraces` enabled and `MaxTraces > 0`, and leave the control arm
without it. Then — and only then — the arms differ in the feature. Every figure recorded in this
document up to that point should be treated as void.
