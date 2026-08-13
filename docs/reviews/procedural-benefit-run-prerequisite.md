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
