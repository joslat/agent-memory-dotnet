# ▶ Continue Here

> **The 30-second resume point for this repo.** For the full status, the hardening story, and the road to
> `1.0`, read **[`docs/ROADMAP.md`](docs/ROADMAP.md)** (this file is the short landing; ROADMAP is the source
> of truth). _Last updated: **2026-07-13**._

---

## Where we are

- **`0.1.0-preview.4` is published** to NuGet (12 packages: `AgentMemory` + `AgentMemory.*`); GitHub release is live.
- **The 2026-06-21 release record is clean and green:** Release build **0 warnings**; **2654 unit + 236 live-Neo4j integration tests passing**. This 2026-07-09 work now records **2658 Release unit tests passing**, plus a **5-test live Neo4j shakedown passing** for the golden-path/history changes; the earlier docs cleanup also recorded **34 Semantic Kernel tests passing**.
- **Behavioral compatibility pack is merged to `main`:** local TCK-style mirrors, the compatibility catalog, read-audit/history expansion, and recency/frequency reranking are now on the default branch.
- **Upstream TCK HTTP bridge is merged to `main`** through the **Gold tier**: `tools/AgentMemory.TckBridge` (PR #70 Bronze, #73 Silver, + this Gold PR) passes **Bronze 93/93, Silver 67/67, and Gold 18/18 (178 total)** against `neo4j-labs/agent-memory-tck` @ `4603b91f` over live Neo4j 5.26. Only **Platinum** (11 hosted-service ops) remains unimplemented — out of scope for a self-hosted library. Gold exposed `IEntityRepository.MergeEntitiesAsync` on the interface (was concrete-only) and surfaced one follow-up — re-pointing arbitrary typed relationships from source→target — which is now **resolved by PR #83**: merge re-points all typed `RELATED_TO` relationships onto the survivor (non-destructive, owner-scoped).
- The library is **feature-complete and heavily hardened** — 6 structured review cycles, then **6 rounds of full-repo adversarial bug-hunting + a convergence-verification pass** (80+ confirmed defects fixed, PRs #25–#69).

## What's next — pick up here

None of this is bug-fixing; `main` is clean. TCK compatibility is now complete through Gold — the remaining arc is all about cutting `1.0`. (Full rationale in [ROADMAP → Next steps](docs/ROADMAP.md#next-steps).)

1. ✅ **API-surface audit → `1.0` candidate — DONE (PRs #75–#83).** The public surface is locked for SemVer: implementation types internalized behind the interfaces (~331→~203 public types, #75); the two Silver/Gold follow-ups landed (`ListAllTracesAsync` + `IToolCallRepository.GetStatsAsync`, #76, plus `MergeEntitiesAsync`→`Task<bool>`); a correctness/contract-honesty pass (#77); a naming/enum/deprecated-surface freeze (#78); type-safety shapes (#79); the `MemoryNodeKind` enum (#80); a library-wide `ct`→`cancellationToken` rename (#81); Diffbot keyed-DI (#82); and the merge-relationship-transfer gap closed (#83). **Next actionable step is #2 (preview soak).**
2. **Preview soak + real-world feedback** — drive the `AgentWithMemory` sample end-to-end; validate the install/usage path on `preview.4`; iterate on ergonomics. **This is the gate to `1.0`.**
3. **Cut `1.0`** — bump `Directory.Build.props`, finalize `CHANGELOG.md`, tag `v1.0.0` → `squad-release.yml`.
4. **Docs–code reconciliation** — keep `architecture.md` / `design.md` / `schema.md` synced; prefer dated facts over "durable" counts.
5. **Ecosystem-breadth gaps** (optional, demand-driven) — local NLP extractors (GLiNER/ONNX), a concrete local embedding adapter, more framework integrations (AutoGen.NET / LangChain.NET), Opik-style LLM observability.
6. **TCK Platinum** (optional) — hosted-service ops (`create_conversation`, `get_entity_history`, `merge_entities`, `get_entity_graph`, `explain_step`, provenance); only relevant if a NAMS-style hosted backend is pursued.

## What just happened (most recent first)

- **1.0 API-surface lockdown shipped** (PRs #75–#83, through 2026-07-13) — internalized implementation types behind the interfaces (public surface ~331→~203, #75), landed the interface follow-ups (`ListAllTracesAsync`, `IToolCallRepository.GetStatsAsync`, `MergeEntitiesAsync`→`Task<bool>`, #76), a correctness pass (#77), a naming/enum/deprecated-surface freeze (#78), type-safety shapes (#79), the `MemoryNodeKind` enum (#80), a library-wide `ct`→`cancellationToken` rename (#81), Diffbot keyed-DI (#82), and **merge now re-points all typed `RELATED_TO` relationships source→target** (#83). Public surface is locked for `1.0`.
- **TCK Gold tier shipped** (2026-07-11) — added `merge_duplicate_entities` + `get_similar_traces` to the bridge; **Gold 18/18** (178 total with Bronze/Silver regressions green). Exposed `IEntityRepository.MergeEntitiesAsync` on the interface (was implemented but unreachable). A Copilot review loop was driven to zero.
- **TCK Silver tier shipped** (PR #73, 2026-07-11) — 12 long-term-search + reasoning endpoints; **Silver 67/67**. Folded in post-Bronze hardening (docs sync, MQ005 de-flake closing #71, a live-Neo4j EXPLAIN query sweep). An 8-round Copilot review was driven to zero.
- **Upstream TCK Bronze bridge shipped** (PR #70, 2026-07-11, squash `c5b0b1f`) — `tools/AgentMemory.TckBridge` at **full Bronze conformance 93/93**. The conformance run found + fixed 5 wire-contract issues and **2 real Cypher bugs** (`ListSessions collect(m ORDER BY …)` was invalid Cypher; `SHOW INDEXES … RETURN` missing `YIELD`). A 7-round GitHub Copilot review was driven to zero. **Known pre-existing flake:** `MQ005` (`SupersedeFactAsync`) — root-caused to stub-embedding non-determinism triggering fact dedup; hardened separately.
- **Behavioral compatibility pack merged and branch cleanup started** — the pack is on `main`; merged/stale branches were pruned, and the only useful artifact from the stale Aspire branch was salvaged as `samples/samples.sln`.
- **Docs reconciled** to current reality (2026-07-09 docs pass) — active docs were corrected, `docs/core/` was added, stale package/schema/test/license claims were fixed, and historical task docs were labeled as historical.
- **Released `0.1.0-preview.4`** (CHANGELOG finalized, tagged `v0.1.0-preview.4` → `squad-release.yml` packed + pushed to nuget.org + created the GitHub release).
- **R6 hardening (PRs #63–#69):** MAF cancellation guards, entity-resolution `invalidated_at`, owner-scoped session clear/delete, truncation-ordering fixes, trace concurrent-delete race; then cleanup — CA2007 `ConfigureAwait(false)` enforced library-wide, culture-invariant formatting, status-aware telemetry, dead `EnableAutoPrune` removed.
- **The convergence test** that motivated R6: an exhaustive sweep that found 24 confirmed defects in never-hunted areas, proving the earlier "converged to zero" was a shallow-lens artifact.

## How to cut the next release (verified)

1. Bump `<Version>` in `Directory.Build.props`; finalize `CHANGELOG.md` (`[Unreleased]` → `[<ver>] - <date>` + a fresh empty `[Unreleased]`).
2. Dry-run: `dotnet build AgentMemory.slnx -c Release -p:Version=<ver>` then `dotnet pack src/*/*.csproj -c Release --no-build -p:Version=<ver> -o /tmp/verify` — catch packaging issues **before** the irreversible publish.
3. Commit to `main`, then `git tag -a v<ver> -m "…" && git push origin v<ver>` → `squad-release.yml` does the rest (needs the `NUGET_API_KEY` secret).
4. nuget.org's public index lags ~5–15 min after the push step goes green — normal, not a failure.

## Gotchas baked in from this session

- **Run the FULL unit suite locally before pushing** — a filtered subset is just a happy-path test of your own change; it let two CI failures slip through this session.
- **A tag push publishes to NuGet immediately** (irreversible) — do the Release build + dry-run pack first.
- **Bug-hunt discipline:** sweep each defect *shape* to exhaustion (don't sample), write tests against the *trigger* (must fail before the fix), and audit the consumers of any behavior-changing fix.

---

_Where things live:_ **[`docs/ROADMAP.md`](docs/ROADMAP.md)** (status & plan) · [`docs/core/`](docs/core/) (canonical philosophy / requirements / design / specification / ADRs) · [`CHANGELOG.md`](CHANGELOG.md) (release notes) · [`docs/`](docs/) (reference docs) · [`docs/nextsteps.md`](docs/nextsteps.md) (historical planning + the .NET-vs-Python assessment).
