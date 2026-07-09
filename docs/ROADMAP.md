# Agent Memory for .NET — Roadmap & Status

> **The single authoritative "where we are / what's done / what's next" document.**
> For the deep historical implementation plan see
> [`Memory_Review_and_Implementation_Plan.md`](Memory_Review_and_Implementation_Plan.md); for completed
> review records see [`reviews/`](reviews/); for the deferred-ideas backlog see
> [`Improvement-Ideas-Backlog.md`](Improvement-Ideas-Backlog.md).
> **Last updated: 2026-07-09.**

---

## TL;DR — resume here

> For an even shorter landing, see [`../CONTINUE-HERE.md`](../CONTINUE-HERE.md) at the repo root.

- **The library is feature-complete and published.** Latest release: **`0.1.0-preview.4`** (NuGet, 2026-06-21).
- **It is heavily hardened.** Beyond the original 6 review cycles, it went through **six rounds of full-repo
  adversarial bug-hunting plus a final exhaustive convergence-verification pass** — **80+ confirmed defects
  found and fixed** (PRs #25–#69), each with a regression test targeting the trigger. See
  [Quality & hardening](#quality--hardening).
- **`main` is green and clean** in the 2026-06-21 release record: Release build **0 warnings**; **2654 unit + 236 integration tests passing**. This 2026-07-09 work now records **2658 Release unit tests passing**, plus a **5-test live Neo4j shakedown passing** for the golden-path/history changes; the earlier docs cleanup also recorded **34 Semantic Kernel tests passing**.
- **What's genuinely left is not bug-fixing** — it's preview soak + ecosystem breadth + API stabilization
  toward `1.0`. See [Next steps](#next-steps).

---

## Status at a glance

| | |
|---|---|
| **Version** | `0.1.0-preview.4` — published to NuGet 2026-06-21 (12 packages: `AgentMemory` + `AgentMemory.*`) |
| **Maturity** | Feature-complete; in public preview, stabilizing toward `1.0` |
| **Tests** | 2658 Release unit tests and a 5-test live Neo4j shakedown passed locally on 2026-07-09; 34 Semantic Kernel tests were also recorded in the earlier 2026-07-09 docs cleanup; 236 live-Neo4j integration tests are the latest full ROADMAP record (2026-06-21); CI (build-test) on every PR |
| **Build** | Release builds with **0 warnings** (`TreatWarningsAsErrors` on for `src`; library code is CA2007-enforced) |
| **Hardening** | 6 review cycles + capstone, **then 6 rounds of adversarial bug-hunting + a convergence-verification pass** — 80+ confirmed defects fixed (see below) |
| **Open work** | No known bugs/regressions in the documented release state. Forward work is preview feedback, ecosystem breadth, and API stabilization — see [Next steps](#next-steps) |

**What it is:** a native .NET 9 implementation of graph-native persistent memory for AI agents, backed by
Neo4j, with GraphRAG interop and first-class adapters for the Microsoft Agent Framework, Semantic Kernel,
and the Model Context Protocol. It is the .NET counterpart to the Python `neo4j-labs/agent-memory`, with a
documented superset schema.

---

## Shipped capabilities ✅

| Area | What's delivered |
|------|------------------|
| **Three-tier memory** | Short-term (conversations/messages), long-term (entities/facts/preferences/relationships), reasoning (traces/steps/tool-calls) |
| **Extraction pipeline** | `ExtractionStage` → `PersistenceStage`; LLM extractors (`Extraction.Llm`) + Azure AI Language (`Extraction.AzureLanguage`); entity resolution chain (Exact → Fuzzy → Semantic → CreateNew); **streaming/chunked extraction** (`IStreamingExtractor`, DI-registered) |
| **Bitemporal + decay (D1–D7)** | Recency re-ranker, structural hop-decay (`γ^hops`), query-intent presets, non-destructive decay-by-default (soft-invalidate, never `DETACH DELETE` unless opted in), `invalidated_at` transaction clock, two-clock `RecallAsOfAsync(validAsOf, systemAsOf)`, contradiction→supersession. **On `main`, live-tested.** |
| **Multi-tenant isolation** | **R1** `owner_id` + `MemoryScope` (optional shared) across all recall/CRUD/GraphRAG/trace paths; **R1b** per-application store tier (`SharedDatabase` default, opt-in `DatabasePerApplication` with auto-provisioning); **R2** owner-scoped list reads; owner-scoped session-clear/prune; `BeginOwnerScope` host helper |
| **GraphRAG retrieval** | Vector, Fulltext (BM25, Lucene-escaped), Hybrid (scale-free Reciprocal Rank Fusion), and multi-hop Graph traversal |
| **Analytics (optional)** | `AgentMemory.Analytics` — GDS PageRank + Louvain community detection over an owner-scoped projection; graceful no-op without the GDS plugin |
| **Adapters** | **MAF** (context + chat-history providers, `MemoryToolFactory`, facade, trace recorder); **SK** (`Neo4jMemoryPlugin` + text search); **MCP** (tools, resources, prompts; stdio + HTTP) |
| **CLI** | `agentmemory`: `migrate`, `bootstrap`, `schema-check`, `consolidate`, `decay`, `conflicts`, `schema-parity`, `invalidate`, `supersede` |
| **Cross-cutting** | Observability (OpenTelemetry decorators, status-aware); Enrichment (Nominatim geocoding + Wikimedia/Diffbot, rate-limited + retried + cached); schema-parity compatibility kit; consolidation/hygiene; conflict detection |
| **Release & CI** | Tag-gated `squad-release.yml` (push `v<semver>` → pack all `src/*`, push to nuget.org, create GitHub release); `build-test` CI on every PR (unit + SK + live-Neo4j integration via Testcontainers) |

---

## Quality & hardening

The library has been hardened in **two distinct phases**.

### Phase 1 — structured review cycles (converged to zero)
Six vertical/horizontal adversarial review cycles plus a cross-cutting capstone. The candidate→confirmed
trend converged cleanly to zero — a solid first pass.

| Review | Scope | Confirmed | Record |
|---|---|---|---|
| Cycles 1–2 | initial sweep + GDS / invalidate-supersede | — | [`reviews/`](reviews/) |
| Cycle 3 | core / extraction / adapters | 6 | [`reviews/review-2026-06-13-cycle3.md`](reviews/review-2026-06-13-cycle3.md) |
| Cycle 4 | CLI / SK / Observability | 6 | [`reviews/review-2026-06-13-cycle4.md`](reviews/review-2026-06-13-cycle4.md) |
| Cycle 5 | GraphRAG / MCP / assembler | 6 | [`reviews/review-2026-06-13-cycle5.md`](reviews/review-2026-06-13-cycle5.md) |
| Cycle 6 | Enrichment / samples | 4 | [`reviews/review-2026-06-13-cycle6.md`](reviews/review-2026-06-13-cycle6.md) |
| Capstone | concurrency / lifetime / async (whole tree) | 0 | [`reviews/review-2026-06-13-capstone.md`](reviews/review-2026-06-13-capstone.md) |

### Phase 2 — adversarial bug-hunting + a convergence test (2026-06-18 → 06-21)
The "converged to zero" of Phase 1 turned out to be an artifact of **shallow, single-file lenses**. A
deeper, multi-agent, full-repo adversarial effort — **six rounds plus a final exhaustive
convergence-verification pass** — found **80+ additional confirmed defects** and fixed them all (PRs
#25–#69). This was the cross-cutting class the per-file cycles structurally miss: DI/config wiring,
cancellation, multi-tenant isolation, bitemporal/dedup correctness, resilience, and context assembly.

Highlights of what was fixed:
- **~10 dead config options** wired or removed (e.g. `MinConfidenceThreshold`, `EntityTypes`,
  `MaxTracesPerSession`, retry `MaxRetries`).
- **Cancellation honored everywhere** — `OperationCanceledException` no longer swallowed as fabricated
  success (the entire Agent Framework adapter layer had *zero* OCE handling).
- **Multi-tenant isolation hardened** — session-keyed destructive writes (prune + clear) confine to one
  owner bucket; entity resolution/dedup excludes tombstoned nodes.
- **Bitemporal/dedup correctness** — Fact triple-MERGE aligned across single + batch paths; re-asserted
  facts restore to live recall; empty-embedding search-boundary invariant.
- **Resilience** — transient enrichment failures retried not cached; backfill termination guard;
  concurrent-delete races return typed errors.
- **Context assembly** — truncation keeps the *newest* messages; token-budget overflow clamp.
- **Engineering hygiene** — `ConfigureAwait(false)` enforced library-wide via CA2007; `graph_query`
  read-only enforcement test; culture-invariant formatting.

**Root-cause lesson (recorded for future hunts):** rotating review lenses *sample* defect shapes rather
than *exhausting* them, prior fixes can spawn regressions, and happy-path tests let those regressions ship
green. The adopted process change: sweep each defect shape to exhaustion, write tests against the *trigger*
(must fail before the fix), and audit the consumers of any behavior-changing fix. The convergence test that
closed this out came back finding genuinely-new defects only in **never-hunted areas** — and, once those
were fixed, with **no shape left under-swept**, which is the real convergence signal.

---

## Next steps

Nothing below is a bug or regression — `main` is clean. These are forward-looking.

| # | Item | Notes |
|---|------|-------|
| 1 | **Preview soak + real-world feedback** | Validate the install/usage path on `0.1.0-preview.4`; iterate on ergonomics. This is the gate to `1.0`. |
| 2 | **API stabilization → `1.0`** | Lock the public surface under SemVer once the preview has soaked. Note the small surface changes shipped in preview.4 (nullable `UpdateAsync`, owner-scoped `ClearSession`/`DeleteBySession`) — fold into the `1.0` contract. |
| 3 | **Docs–code reconciliation** | Periodic drift check (this very pass corrected several stale claims). Keep `architecture.md` / `design.md` / `schema.md` synced; prefer dated facts over "durable" counts. |
| 4 | **Ecosystem-breadth gaps** (the real .NET-vs-Python deltas) | Optional, demand-driven: local NLP extractors (GLiNER/ONNX), a concrete local embedding adapter (sentence-transformers via ONNX/MEAI), more framework integrations (AutoGen.NET, LangChain.NET), Opik-style LLM observability. See `nextsteps.md` §2 and the backlog. |
| 5 | **Release ergonomics (minor)** | The `gh release create` in `squad-release.yml` does not pass `--prerelease`, so preview releases show `isPrerelease=false` on GitHub (the NuGet package is still correctly a prerelease via the `-preview` suffix). One-line workflow tweak if a true GitHub-prerelease flag is wanted. |

> Granular/older task tracking and the full .NET-vs-Python assessment live in [`nextsteps.md`](nextsteps.md);
> deferred future ideas live in [`Improvement-Ideas-Backlog.md`](Improvement-Ideas-Backlog.md).

---

## How to cut a release (verified procedure)

1. Bump `<Version>` in `Directory.Build.props`.
2. Finalize `CHANGELOG.md`: rename `[Unreleased]` → `[<version>] - <date>`, add a fresh empty `[Unreleased]`.
3. Dry-run locally: `dotnet build AgentMemory.slnx -c Release -p:Version=<version>` then
   `dotnet pack src/*/*.csproj -c Release --no-build -p:Version=<version> -o /tmp/verify` — catches
   packaging issues **before** the irreversible publish.
4. Commit to `main`, then `git tag -a v<version> -m "…" && git push origin v<version>`.
5. The tag triggers `squad-release.yml` → build/test/pack/push-to-nuget.org/create-GitHub-release.
   Requires the `NUGET_API_KEY` secret (glob `AgentMemory*` scope + "push new packages").
6. nuget.org's public index lags ~5–15 min after the push step goes green — that's normal, not a failure.

---

## Where the docs live

| Folder / file | Purpose |
|---|---|
| **[`ROADMAP.md`](ROADMAP.md)** | **This file — the authoritative current status & next steps.** |
| [`getting-started.md`](getting-started.md) | Install, configure, first memory store, multi-tenant setup |
| [`core/`](core/) | Canonical current documentation set: philosophy, requirements, design, specification, ADRs, summaries |
| [`architecture.md`](architecture.md) · [`design.md`](design.md) · [`schema.md`](schema.md) | Current reference — layers/boundaries, type/interface catalogs, Neo4j graph model |
| [`specification.md`](specification.md) | Short current specification entry point; detailed specification lives in [`core/specification.md`](core/specification.md) |
| [`nextsteps.md`](nextsteps.md) · [`Improvement-Ideas-Backlog.md`](Improvement-Ideas-Backlog.md) | Historical task tracking + .NET-vs-Python assessment · current deferred-ideas backlog |
| [`Memory_Review_and_Implementation_Plan.md`](Memory_Review_and_Implementation_Plan.md) | The detailed historical implementation plan (isolation deep-dive); kept as deep reference |
| [`reviews/`](reviews/) | Completed adversarial-review records (point-in-time) |
| [`reference/`](reference/) | Upstream / parity reference (Python schema snapshots, PR how-to, MAF migration guides) |
| [`archive/`](archive/) | Superseded plans and now-implemented design discussions (read-only history) |
