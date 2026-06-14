# Throat-check review — 2026-06-13 (capstone, cross-cutting)

**Scope:** a *horizontal* sweep of the whole `src/` tree for the cross-cutting concerns the per-area cycles
(1–6, which were vertical slices) did not specifically target. **Method:** 3 cross-cutting scanners (`Explore`)
→ per-finding adversarial verification (skeptic defaults to *reject*). Each scanner was told the cycle-1–6
fixes are out of scope, so it hunted **new** instances of those patterns, not the already-fixed sites.

**Result: 6 candidates → 0 confirmed.** Every candidate was refuted by the verifier as already-guarded,
non-reachable, or a misread (e.g. a "thread-safety" flag on a per-request *scoped* service, or a "dropped
token" on a method with no inner async call to forward to). No code changes.

## Dimensions swept — all verified clean

| Dimension | What was checked | Outcome |
|---|---|---|
| **Concurrency & shared state** | Non-thread-safe mutable state in singleton-registered services; static mutable fields; lazy-init/memoization races; `AsyncLocal` cross-flow bleed; shared `HttpClient`/`SemaphoreSlim`/`Timer`/`ActivitySource`/meter races | ✅ clean |
| **Resource lifetime & DI lifetimes** | Captive dependencies (singleton→scoped); undisposed `IDisposable`/`IAsyncDisposable` (semaphores, timers, CTS, sessions, drivers); async-vs-sync disposal mismatches *elsewhere* in the codebase; registration-lifetime mistakes | ✅ clean |
| **Cancellation & async hygiene** | Public async methods that drop their `CancellationToken`; sync-over-async (`.Result`/`.Wait()`/`.GetAwaiter().GetResult()`) on hot paths; fire-and-forget; `async void`; swallowed cancellations | ✅ clean |

## Why this is a credible "clean"
- The 3 ambient `AsyncLocal`-backed contexts (owner/store/ranking) are the canonical safe pattern (singleton +
  `AsyncLocal`), and the one real flow gotcha was already found and closed in cycle 3 (`BeginOwnerScope`).
- The two async-vs-sync disposal bugs of this class were already found and fixed (cycle-4 CLI, cycle-6 samples
  + `AspireDemo.DemoApp`); the sweep found no third instance.
- The cancellation-swallowing pattern was already found and fixed in the Enrichment timeout handlers (cycle 6);
  the sweep found no other reachable instance.
- Memoized/lazy state (e.g. the GDS availability probe, the schema-parity registry) uses safe initialization;
  the GDS projection lifecycle was already hardened in cycle 2.

## Series conclusion
Cycles 1–6 (vertical, per-area) + this capstone (horizontal, cross-cutting) complete the adversarial review of
the library. The candidate→confirmed trend across the deep cycles —

| Review | Candidates | Confirmed | Highest severity |
|---|---|---|---|
| Cycle 3 (core/extraction/adapters) | — | 7 | High |
| Cycle 4 (CLI/SK/observability) | 36 | 6 | High |
| Cycle 5 (GraphRAG/MCP/assembler) | 14 | 6 | High |
| Cycle 6 (Enrichment/samples) | 17 | 4 | Medium |
| **Capstone (cross-cutting)** | **6** | **0** | — |

— shows a clean convergence: fewer findings, falling severity, ending at zero. The library is in solid shape.
Remaining open items are external/operational, not code: the parked NuGet publish (awaiting the maintainer's
`NUGET_API_KEY`).
