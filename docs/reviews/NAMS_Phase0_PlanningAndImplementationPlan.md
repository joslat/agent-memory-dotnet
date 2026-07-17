# NAMS Phase 0 — Planning and Implementation Plan

**Prepared:** 2026-07-17
**Branch:** `nams/phase0-baseline-freeze`
**Purpose:** Phase 1 of a 3-phase plan (stabilization [done, #127] → **this phase: NAMS Phase 0 baseline freeze** → NAMS package skeleton). Executes the "Phase 0: Contract confirmation and baseline freeze" section of `strategy/AgentMemory_NAMS_Backend_Engineering_Plan_V03.md` (§7), whose own "no-code gate" says production NAMS code must not start until this phase's unknowns are answered or explicitly deferred.

---

## 1. Task and scope

The NAMS plan's own Phase 0 task list (§7) has 7 items. Four are things I can do directly against this repository; two require a live NAMS sandbox or a direct answer from Neo4j (out of my reach — I can only *record* them as open questions, not resolve them); one is a documentation/issue-tracking task.

| # | Plan's task | In scope for me? | How |
|---|---|---|---|
| 1 | Record the exact target commit SHA | Yes | `git log -1` on `main` post-stabilization-merge |
| 2 | Create a release tag or immutable baseline branch | Yes | Annotated, non-destructive git tag |
| 3 | Run and archive: Release build, unit suite, live-Neo4j integration suite, TCK suites, public API compatibility checks, package build, sample build | Yes | All runnable locally; TCK is already part of the integration suite (`docs/neo4j-memory-ecosystem.md` confirms 178/178 Bronze/Silver/Gold) |
| 4 | Record test counts/duration/package versions/target frameworks/benchmark baselines/warning count/public API snapshot | Yes | Derived from #3's runs; public API snapshot needs a lightweight one-off tool (no `Microsoft.CodeAnalysis.PublicApiAnalyzers`/`PublicAPI.txt` exists in this repo today — confirmed by grep, not assumed) |
| 5 | Confirm with Neo4j (client canonicity, versioning, auth, idempotency, rate limits, etc.) | **No — requires a human conversation with Neo4j** | Record as open questions in a tracking issue instead of resolving them |
| 6 | Obtain/generate an immutable API contract snapshot (OpenAPI or fixture) | **Partial — best-effort only** | Attempt `WebFetch` against the public NAMS REST reference/limits pages; if no machine-readable OpenAPI doc is published, hand-transcribe the confirmed endpoints from the plan's own §4.2/Phase 2 table into a checked-in fixture, explicitly marked as *not authoritative* until Neo4j confirms it |
| 7 | Open an implementation issue recording every assumption | Yes | GitHub issue, using the plan's own Section 11 (35 questions) as the body, tiered per the earlier `backlog-triage-and-nams-assessment-2026-07.md` §1.5 grouping |

**Explicitly out of scope for this phase** (per the plan's own ADR-4/Phase 1+ boundary): no `AgentMemory.Nams` code, no client adapter, no package skeleton — that's Phase 2 of this 3-phase plan. This phase produces only: a tagged baseline, an archived test/build record, a best-effort contract fixture, and a tracking issue. Nothing in this phase touches `src/` production code.

## 2. Detailed implementation plan

1. **Baseline tag.** `git tag -a baseline/pre-nams-<date> -m "..."` on the current `main` HEAD (post-#127 stabilization merge), pushed to `origin`. Non-destructive — a tag is purely additive and never affects existing history/branches.
2. **Archive the build/test record.** Run, in this order (matching the repo's own established discipline of full-suite-before-anything-else):
   - `dotnet build AgentMemory.slnx -c Release` → record warning/error count.
   - `dotnet test tests/AgentMemory.Tests.Unit` → record pass count/duration.
   - `dotnet test tests/AgentMemory.Tests.Unit.SemanticKernel` → record pass count/duration.
   - `dotnet test tests/AgentMemory.Tests.Integration` (live Neo4j, includes TCK-mirrored behavior tests per `docs/neo4j-memory-ecosystem.md`) → record pass count/duration.
   - Package build: dry-run `dotnet pack` per `strategy/STATUS.md`'s documented release-dry-run recipe (reuses the existing, proven procedure — no new tooling).
   - Sample build: already covered by the Release build above (samples are in the same solution).
   - Save all of this as a dated snapshot section in this same planning doc (not a separate file — keeps the "plan" and "what actually happened" in one place, matching the stabilization doc's own pattern of an "Implementation notes" section added after the fact).
3. **Public API snapshot.** No public-API-diff tool exists in this repo yet (confirmed: no `PublicAPI.Shipped.txt`/`Unshipped.txt`, no `Microsoft.CodeAnalysis.PublicApiAnalyzers` package reference, no `api-compat` CI step). Rather than bolt on a new, permanent analyzer as a side effect of this one-off baseline task, generate a one-time reflection-based snapshot (a small throwaway console script run from the scratchpad, not committed as new tooling) listing every public type and member across the 12 shipped `AgentMemory.*` assemblies, and commit the *output* (a plain text listing) as this phase's dated artifact. This gives Phase 7 of the NAMS plan (public API diff review before any convergence work) something concrete to diff against later, without committing this repository to a new analyzer/CI dependency it didn't ask for.
4. **NAMS API contract snapshot (best-effort).** `WebFetch` the public NAMS REST API reference (`https://neo4j.com/labs/agent-memory/reference/rest-api/`) and limits page (`https://neo4j.com/labs/agent-memory/reference/nams-limits/`). If a machine-readable OpenAPI/JSON spec is linked or fetchable, save it verbatim. If not (most likely, per the NAMS plan's own repeated observation that several operations are "not documented" at the level Phase 2 needs), transcribe the CONFIRMED endpoint table already built in the plan's own §4.2/Phase 2 section into a checked-in fixture file, explicitly labeled as **transcribed from public docs on 2026-07-17, not Neo4j-confirmed, not authoritative** — matching the plan's own repeated discipline of never overstating certainty.
5. **Tracking issue.** Open a GitHub issue titled around "NAMS backend integration — tracking" with:
   - A short summary linking `strategy/AgentMemory_NAMS_Backend_Engineering_Plan_V03.md` (note: this file lives under the gitignored `strategy/` directory, so the issue body will need to inline the essential content — a plan file not tracked in git can't be linked by path in an issue for anyone without local repo access).
   - The plan's own Section 11 (35 questions), organized into the same three tiers already worked out in `strategy/backlog-triage-and-nams-assessment-2026-07.md` §1.5 (must-ask-live / technical-unblockers / follow-up-email) so this issue doubles as the literal Neo4j-meeting agenda artifact.
   - A checklist mirroring the plan's own Phase 0 "no-code gate" list, so the issue itself tracks when Phase 4+ (real NAMS recall/persistence code) becomes unblocked.

## 3. Explicit non-goals for this phase (so scope doesn't creep)

- No code in `src/AgentMemory.Nams` — that's Phase 2.
- No live NAMS sandbox calls — no credentials exist yet, and the plan's own Phase 0 doesn't require them (only Phase 10's live integration tests do).
- No resolution of the Section 11 questions — only recording them clearly enough that a 30-60 minute meeting can work through the tiered list efficiently.
- No changes to `strategy/AgentMemory_NAMS_Backend_Engineering_Plan_V03.md` itself — it's already been assessed (see `strategy/backlog-triage-and-nams-assessment-2026-07.md`) and doesn't need edits for this phase.

## 4. Definition of done for this phase

- [x] Baseline tag pushed.
- [x] Full build/test record archived in this doc with exact numbers (not carried over from memory — freshly run).
- [x] Public API snapshot generated and committed.
- [x] NAMS contract fixture attempted and committed (clearly labeled with its actual confidence level).
- [x] Tracking GitHub issue opened with the tiered Section 11 questions.
- [ ] Self-reviewed, PR opened, CI green, merged to `main`.

---

## 5. What actually happened (executed 2026-07-17)

### Baseline

- **Commit:** `60eb7c1fa1e79e607923259f67c207258fd16c40` (main, post-#127 stabilization merge).
- **Tag:** `baseline/pre-nams-2026-07-17`, pushed to `origin`.

### Build/test archive

- Release build: **0 warnings, 0 errors** across all 12 packages + samples/tools.
- Unit: **3041/3041** passing.
- Semantic Kernel unit: **54/54** passing.
- Live-Neo4j integration (includes TCK-mirrored behavior tests): **308/308** passing.
- Package dry-run pack (all 12 `eng/release-packages.txt` entries, `-c Release --no-build`): all 12 packed successfully with no errors. (Gotcha hit and fixed: `eng/release-packages.txt` has CRLF line endings, which corrupts the `$proj` variable with a trailing `\r` in a naive bash `while read` loop — stripped explicitly.)
- Package versions/target frameworks: net8.0/net9.0/net10.0 multi-targeted, current shipped version `1.2.0` (unchanged by this phase — no release cut here).

### Public API snapshot

No `PublicAPI.Shipped.txt`/`Microsoft.CodeAnalysis.PublicApiAnalyzers`/api-compat tooling exists in this repo (confirmed by grep, not assumed). Rather than add a new permanent analyzer dependency as a side effect of a one-off baseline task, generated a reflection-based snapshot (`System.Reflection.MetadataLoadContext`, run from a scratchpad throwaway tool, not committed) covering all 12 shipped packages plus the `Cli`/`TckBridge` tools — saved as `docs/reviews/nams-phase0-public-api-snapshot.txt`. Gotcha: `Assembly.LoadFrom` fails on partial-dependency library output folders (a library's own `bin/` doesn't get transitive NuGet deps copied) and on runtime-version-mismatched executable folders; `MetadataLoadContext` with an explicit core-assembly name and a resolver spanning both the target folder and the matching shared-framework directory (`dotnet --list-runtimes`) is the robust way to do this without executing any code.

### NAMS API contract snapshot — better than expected

The plan (§4.8, §11 Q1-2) treated the existence of a machine-readable OpenAPI contract as an open question requiring a Neo4j answer. **It isn't open — `https://memory.neo4jlabs.com/openapi.json` is live, public, and fetched clean**: valid Swagger 2.0, title "NAMS Memory API" v1.0, **80 paths / 92 operations**, `BearerAuth` security scheme. Saved verbatim as `docs/reviews/nams-openapi-snapshot-2026-07-17.json`. This directly resolves one of the plan's own flagged uncertainties:

- **Phase 2's `SearchMessagesAsync` gap (plan §7 Phase 2, flagged as "may not map to anything the API actually exposes") is resolved**: `POST /v1/conversations/{id}/search` exists in the spec. `POST /v1/entities/search` also confirmed (already assumed in the plan).
- **Auth confirmed**: `Authorization: Bearer <token>` with both `/v1/auth/exchange` (Auth0-style) and `/v1/auth/api-keys` (`nams_…` key lifecycle: create/list/revoke/reveal/rotate) present in the spec.
- **Idempotency (plan §11 Q12-16) is still genuinely unanswered**: checked `POST /v1/conversations` and `POST /v1/conversations/{id}/messages/bulk`'s documented parameters directly in the spec — neither exposes an idempotency-key or caller-supplied-ID parameter. This is a confirmed gap, not an unresearched one, going into the tracking issue.
- **Rate/payload limits (plan §11 Q24-25)**: confirmed narrow-endpoint limits only (ontology 30/hr/workspace, feedback 10/min+100/hr/user+200/hr/workspace, general 413 on oversized bodies) — nothing published for the core conversation/message/context path Phases 4-5 exercise most. Still an open question for Neo4j.
- **2-day managed-workspace reclamation (plan §11 Q26, risk register)**: reconfirmed verbatim from the public limits page — still doesn't state whether it applies to BYOD/production tiers. Still an open question.
- This snapshot is a real fetch of a live, versioned service, not a fixture Neo4j has agreed is stable — it can drift or require auth differently at any time. Treated as **informative, not contractually pinned**, exactly as the plan's own Phase 0 "no-code gate" anticipates ("a spike may proceed with explicit temporary assumptions, but it may not be released").

### Tracking issue

Opened as [#128](https://github.com/joslat/agent-memory-dotnet/issues/128) with the plan's Section 11 questions, tiered per `strategy/backlog-triage-and-nams-assessment-2026-07.md` §1.5, updated to mark the two items this research resolved (OpenAPI existence, `SearchMessagesAsync` mapping) as answered rather than open. Kept open (not closed by this PR) — it tracks Neo4j's answers, which haven't happened yet.
