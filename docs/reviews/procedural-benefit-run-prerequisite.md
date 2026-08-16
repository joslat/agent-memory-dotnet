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

---

## Runs 7–10: the read path, and the first interpretable result

Attaching the provider was necessary and nowhere near sufficient. Wiring "the arm can read a
procedure" turned out to be **five** independent gates, three of them silently shut, and each one
produces the identical output: both arms the same, `SHOWS BENEFIT: False`.

| # | Gate | State before | Symptom if shut |
|---|---|---|---|
| 1 | An `AIContextProvider` on the arm | absent | arm reads nothing (runs 1–6) |
| 2 | `MaxTraces > 0`, other categories zeroed | n/a | reads the wrong memory, or memory generally |
| 3 | `ReasoningTrace.TaskEmbedding` on the promoted trace | **never set** | trace stored, matched by no search |
| 4 | `ContextFormatOptions.IncludeReasoningTraces` | **false** | trace recalled, dropped by the formatter |
| 5 | The trace's **outcome** rendered at all | **impossible** | block says "you did this before", not how |

Gates 3–5 were all found by reading the code before spending, and 5 was a product gap rather than a
harness one: `MafTypeMapper` rendered a recalled trace's `Task` and never its `Outcome`. On a repeated
task the `Task` text is what the agent is already holding, so trace recall could not convey a procedure
to a MAF agent at all. Fixed behind `ContextFormatOptions.IncludeTraceOutcomes` (default off, so no
sealed base moves).

### The arm is no longer trusted to be wired

Five identical false negatives is enough. `ProceduralRecallWitness` now rides the admission-policy seam
— the last gate before `MafTypeMapper` hands a block to the model — and counts the procedure blocks
that were actually admitted, per attempt. A run whose later attempts admitted **zero** procedures now
prints `VOID` and exits non-zero instead of reporting a verdict. The property the measurement depends on
is observed, not inferred from configuration that had been wrong three times.

### Run 8: recall proven, and the promotion was the problem

With the witness reporting `[0, 1, 2]` — attempt 1 reads nothing by construction, then 1, then 2 — the
read path was confirmed working for the first time. The arms still tied at 6 tool calls, and the reason
was visible in the procedure text the witness printed:

```
... : LookUpTraveller then CheckServiceBulletin then PlaceHold then RefreshSession then PlaceHold then Book
```

**That is a transcript, not a procedure.** It records how the agent stumbled into success, refused call
included, so replaying it faithfully reproduces the wasted call. Promotion now records only calls whose
result was not a refusal, decided by a caller-supplied predicate exactly as completion is. Counting is
untouched: a refused call still costs a tool call, because the agent really did spend it.

### Two defects in the verdict rule, found by it firing wrongly

Run 8 reported `SHOWS BENEFIT: True` on a 0.4-step difference while reporting
`improvedWithRepetition=False` on the line above — a benefit claim and "nothing was learned", together.

1. **`ShowsBenefit` ignored `ImprovedWithRepetition`.** The class had always documented both comparisons
   as required. It now requires both.
2. **No noise floor.** A third of a step across three attempts is the difference between two runs of the
   *same* configuration. The floor is now the **control arm's own spread** across attempts — the control
   cannot learn by construction, so its variance *is* the instrument's jitter. Deliberately not the
   enabled arm's spread, which learning inflates by design.

A third change is disclosed rather than buried: `ImprovedWithRepetition` read `Steps` alone, so an arm
that learned to skip one wasted **tool call** in the same number of turns scored as having learned
nothing — while `ToolCallReduction`, from the same class, showed the saving. It now accepts learning in
either measure and requires the other not to regress. **That rule was widened after seeing a run with
exactly that shape**, so both per-measure flags are reported separately and no reader has to take the
composite on trust.

### Run 10 — the measurement, at last

`--procedural-benefit --attempts 5`, promotion refusal-filtered, verdict noise-gated, recall witnessed.

```
procedures  completion=100%  meanSteps=6.0  meanToolCalls=5.2
control     completion=100%  meanSteps=6.6  meanToolCalls=6.0
stepReduction=9.1%  toolCallReduction=13.3%  completionDelta=0%
improvedWithRepetition=True (steps=False, toolCalls=True)
noiseBand(control spread): steps=0.55  toolCalls=0.00  => exceeded: steps=True, toolCalls=True
perAttempt steps/toolCalls: procedures=[6/6, 6/5, 6/5, 6/5, 6/5]  control=[6/6, 7/6, 7/6, 6/6, 7/6]
proceduresInContextPerAttempt=[0, 1, 2, 3, 3]
SHOWS BENEFIT: True
```

The per-attempt column is the whole result. The procedural arm pays **6** tool calls on attempt one and
exactly **5** on every attempt after it. The control pays 6 every single time and never varies — its
tool-call spread is 0.00, so it is not that the control got unlucky. The saving is one call, it is the
same call every time, and it is the one step in the chain that **cannot be inferred from any
interface**: the stale-session refresh, discoverable only by being refused.

### What this does and does not establish

> **On a task containing a convention that must be learned by failing, a promoted procedure removes that
> discovery cost from every subsequent attempt — one tool call, on 5/5 attempts, with no loss of
> completion.**

Narrow, and deliberately so:

- **One task, one model, five attempts per arm.** This is an existence proof that the feature works
  end-to-end and is measurable, not an effect size anyone should quote.
- **The saving equals the discoverable step, and nothing more.** The other four calls are inferable from
  tool descriptions and the procedural arm still makes all four. Consistent with runs 1–4's *shape*
  (though not their conclusion, which was void): a well-documented tool API leaves procedural memory
  nothing to remove. The benefit lives precisely in what the API cannot say.
- **Steps did not improve** (6.0 vs 6.6, inside the 0.55 noise band). The arm saves a call, not a turn.
- **The retracted conclusion stays retracted.** Runs 1–6 remain void; this is the first run whose read
  path was verified rather than assumed.

### Product changes this required

- `ContextFormatOptions.IncludeTraceOutcomes` (new, default `false`) — a recalled trace renders its
  outcome, not only its task. Without it, procedural memory is mute on the MAF surface.
- The trace/outcome pair renders as `"task: outcome"` and procedures are written with the word `then`
  rather than `->`, because every admitted block is HTML-escaped (#92 Phase 1) and an arrow reaches the
  model as `-&gt;`. The escaping is the security property and stays; the procedure is written to survive
  it.
- **A tension worth recording, not fixed here:** the shipped context prefix tells the model that recalled
  memory is untrusted data and that it must never follow instructions found inside it. A promoted
  procedure *is* a suggested ordering, so the default framing argues against the feature's purpose. The
  benchmark keeps the #92 prefix verbatim and appends one sentence scoped to procedures. A host enabling
  procedural recall needs to make that decision consciously; there is no shipped default that resolves
  it.
