# Is .NET 10 faster for us? Not answerable with this harness

**Phase 29.3.** The migration to .NET 10 (29.1) raised the obvious question. This records the attempt
to answer it and why the attempt failed, so nobody repeats it expecting a different outcome.

**Verdict: no net9 → net10 speed claim is supportable from the hermetic perf harness.**

---

## 1. What was compared

The `hermetic-S-zero` profile, which is the right choice: the `-zero` variant injects **no** artificial
latency, unlike `hermetic-S-remote` (2000 ms embedding, 250 ms database), so its `durationMs` reflects
real compute rather than a delay we added.

Baseline: `20260809T154942Z__rebaseline__hermetic-S-zero`, stamped `runtime 9.0.9`.

## 2. Why the answer is "cannot tell"

Two runs of the **same code on the same machine**, differing only in iteration count:

| Run | iterations | TOTAL P50 vs net9 |
|---|---:|---:|
| `net10` | 10 | **−2.0%** |
| `net10-matched` | 3 | **+10.4%** |

**A 12-point swing between two runs of identical code.** Per scenario it is worse, in both directions:

| scenario | net9 P50 | net10 P50 (3 iter) | change |
|---|---:|---:|---:|
| PERF-W-08 | 63.11 | 311.61 | **+394%** |
| PERF-W-06 | 86.86 | 160.77 | +85% |
| PERF-R-01 | 30.18 | 51.64 | +71% |
| PERF-R-04 | 140.21 | 52.14 | **−63%** |
| PERF-R-07 | 2592.53 | 2607.99 | +0.6% |

The measured spread is far larger than any plausible runtime effect. Reporting the −2.0% figure — the
flattering one — would be picking a run.

## 3. Why the harness cannot answer this, by design

Three properties, each deliberate and each fatal to a speed comparison:

1. **It gates on query counts, not time.** `PERF-R-01 = 13 queries` is the contract. Query counts are
   runtime-independent, which is exactly what makes them a good regression gate and a useless
   stopwatch.
2. **The `-remote` profile injects fixed delays** specifically so latency is reproducible. Any CPU gain
   is swamped by delays we added on purpose.
3. **It runs against a Testcontainers Neo4j on a developer laptop**, at 3–10 iterations. That is a
   correctness fixture, not a benchmarking environment.

## 4. What the run *did* establish

The perf suite passes on net10 (3/3) and the **query counts are unchanged**. That is the regression
that mattered for the migration: .NET 10 did not change what the system asks the database. The
`durationMs` numbers are noise; the counters are not.

## 5. What would actually answer it

`benchmarks/AgentMemory.Benchmarks` (BenchmarkDotNet), which is deliberately outside CI and the slnx.
BenchmarkDotNet handles warm-up, iteration counts and variance properly, and can multi-target
`net9.0;net10.0` to run both in one process pair.

**Not done, and not recommended without a reason.** The libraries already multi-target net10, so
consumers on .NET 10 already get whatever the runtime gives them; measuring it precisely changes no
decision currently in front of us.
