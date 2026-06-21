# ▶ Continue Here

> **The 30-second resume point for this repo.** For the full status, the hardening story, and the road to
> `1.0`, read **[`docs/ROADMAP.md`](docs/ROADMAP.md)** (this file is the short landing; ROADMAP is the source
> of truth). _Last updated: **2026-06-21**._

---

## Where we are

- **`0.1.0-preview.4` is published** to NuGet (12 packages: `AgentMemory` + `AgentMemory.*`); GitHub release is live.
- **`main` is clean and green:** Release build **0 warnings**; **2654 unit + 236 live-Neo4j integration tests passing**. **No open PRs. No known bugs or regressions.**
- The library is **feature-complete and heavily hardened** — 6 structured review cycles, then **6 rounds of full-repo adversarial bug-hunting + a convergence-verification pass** (80+ confirmed defects fixed, PRs #25–#69).

## What's next — pick up here

None of this is bug-fixing; `main` is clean. (Full rationale in [ROADMAP → Next steps](docs/ROADMAP.md#next-steps).)

1. **Preview soak + real-world feedback** — validate the install/usage path on `preview.4`; iterate on ergonomics. **This is the gate to `1.0`.**
2. **API stabilization → `1.0`** — lock the public surface under SemVer; fold in preview.4's small surface changes (nullable `UpdateAsync`, owner-scoped `ClearSession`/`DeleteBySession`).
3. **Docs–code reconciliation** — keep `architecture.md` / `design.md` / `schema.md` synced; prefer dated facts over "durable" counts.
4. **Ecosystem-breadth gaps** (optional, demand-driven) — local NLP extractors (GLiNER/ONNX), a concrete local embedding adapter, more framework integrations (AutoGen.NET / LangChain.NET), Opik-style LLM observability.
5. **Minor** — add `--prerelease` to `squad-release.yml` if you want GitHub to flag preview releases (the NuGet package is already correctly a prerelease).

## What just happened (most recent first)

- **Docs reconciled** to current reality (this commit) — ROADMAP/README/nextsteps corrected; an earlier "decay/bitemporal on maintainer hold" claim was verified stale (it's shipped).
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

_Where things live:_ **[`docs/ROADMAP.md`](docs/ROADMAP.md)** (status & plan) · [`CHANGELOG.md`](CHANGELOG.md) (release notes) · [`docs/`](docs/) (architecture / design / schema / getting-started) · [`docs/nextsteps.md`](docs/nextsteps.md) (historical planning + the .NET-vs-Python assessment).
