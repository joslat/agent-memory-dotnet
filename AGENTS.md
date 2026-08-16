# AGENTS.md

A README for coding agents working in this repository. Humans should start at
[`README.md`](README.md) and [`CONTRIBUTING.md`](CONTRIBUTING.md); this file is the short version, plus
the conventions that are easy to violate without noticing.

## What this repo is

**Agent Memory for .NET** — a graph-native persistent memory engine for AI agents, backed by Neo4j.
Framework-agnostic core plus thin adapters (Microsoft Agent Framework, Semantic Kernel, MCP). It is a
from-scratch .NET reimplementation of the ideas in Python's `neo4j-labs/agent-memory`, not a port, and
it is verified against that project's compatibility kit.

- 15 shipped projects under `src/`, multi-targeting `net8.0;net9.0;net10.0`. Everything else
  (tests, tools, samples) is `net10.0`. SDK pinned in `global.json`.
- 5 test projects under `tests/`, 5 tools under `tools/`, samples under `samples/`.
- Architecture: [`docs/architecture.md`](docs/architecture.md). What the memory layer does and does not
  do, honestly labelled: [`docs/memory-map.md`](docs/memory-map.md). Schema extensions:
  [`docs/extensions/`](docs/extensions/README.md).

## Build and test

Every command below is run from the repository root and is the same one CI runs
(`.github/workflows/ci.yml`).

```bash
dotnet restore AgentMemory.slnx
dotnet build AgentMemory.slnx                 # must be 0 errors AND 0 warnings
```

`TreatWarningsAsErrors` is on for `src/` projects and off for `tests/`. A warning in `src/` is a build
failure, not a note.

```bash
# unit + adapter unit suites (no Docker, no Neo4j, no LLM)
dotnet test AgentMemory.slnx --filter "Category!=Integration&Category!=Performance"

# integration — Testcontainers starts neo4j:5.26 automatically; Docker must be running
dotnet test tests/AgentMemory.Tests.Integration/AgentMemory.Tests.Integration.csproj \
  --filter "Category=Integration"

# hermetic perf gates (query counts, not wall time)
dotnet test tests/AgentMemory.Tests.Performance/AgentMemory.Tests.Performance.csproj

# static upstream schema-parity check
dotnet run --project tools/AgentMemory.Cli/AgentMemory.Cli.csproj -- \
  schema-parity --upstream-version 0.5.0
```

Single project, when you know where you are:

```bash
dotnet test tests/AgentMemory.Tests.Unit/AgentMemory.Tests.Unit.csproj
```

**Run the full unit suite before pushing, not a `--filter`ed subset.** A filtered run tests the happy
path of the change you just made; it has repeatedly passed while CI caught a real regression elsewhere.

Operational CLI verbs (all take `--uri`/`--password`, or the matching configuration/env values):
`migrate`, `bootstrap`, `schema-check`, `schema-parity`, `consolidate`, `decay`, `conflicts`,
`invalidate`, `supersede`, `history`, `evaluate`, `perf`, `block`.

## Conventions that are actually enforced

**Dependencies flow strictly inward.** `Abstractions ← Core ← Neo4j / adapters`. Abstractions takes
exactly one NuGet dependency (`Microsoft.Extensions.AI.Abstractions`); Core must not reference
`Neo4j.Driver` or any framework SDK; adapters never reference each other. Rules B1–B11 are in
[`docs/architecture.md` §5](docs/architecture.md#5-boundary-enforcement-rules) and are checked by
`AbstractionsContractGuardTests` / `PackageBoundaryGuardTests` — a violation fails the build, not the
review.

**`ConfigureAwait(false)` on every `await` in `src/`.** CA2007 is a warning in `src/.editorconfig`, and
warnings are errors there. Gotcha the auto-fixer gets wrong: it rewrites
`await using var x = Open().ConfigureAwait(false)` into a `ConfiguredAsyncDisposable` binding. Fix
those by hand, as a two-line disposal.

**Cypher lives in `Queries/` constants**, one file per domain, inside `AgentMemory.Neo4j`. Never inline
a Cypher string in a repository.

**Domain types are `sealed record` with `required` members.** Timestamps are `DateTimeOffset` with a
`Utc` suffix. Collections are `IReadOnlyList<T>` / `IReadOnlyDictionary<K,V>` and default to empty,
never null. Every async method takes `CancellationToken cancellationToken = default` and passes it
through.

**New capabilities ship off by default, and "off" means byte-identical.** Not "an unchanged graph" —
the query is not issued, the service is not called, the prompt bytes do not move. The current roster of
flags and defaults is [`docs/architecture.md` §3.6](docs/architecture.md#36-every-capability-in-this-cycle-ships-dark).
If you add a flag, say what off costs in its doc comment, and prove it.

**Supersession and invalidation are non-destructive.** No `DETACH DELETE` on any contradiction,
supersession, decay or prune path: the losing record is stamped `invalidated_at` (plus `valid_until`)
and linked to the winner by `SUPERSEDED_BY`. Hard deletion exists only where a caller explicitly asked
for it — `ClearSessionAsync` and the delete APIs — and nowhere else.

**Tests are written red-first, and target the trigger.** A regression test must be shown to fail
*before* the fix and pass after. A test that exercises the happy path of the fixed code proves nothing
about the defect. When a fix changes behaviour, audit that behaviour's consumers rather than assuming
the change is local.

**Schema changes have an owner.** Adding a label, relationship type or property means either a base
migration reviewed as base, or a schema extension that declares it —
[`docs/extensions/README.md`](docs/extensions/README.md) has the rules and the how-to. `schema-check`
fails when a shape has no owner.

**No `TODO`, `FIXME`, or `HACK` comments.** Finish the work or open an issue.

**Adding a project under `src/` does not publish it.** It must be listed in `eng/release-packages.txt`,
and CI fails when `src/*/` and that manifest disagree.

## Docs

Update docs in the same change as the code, not afterwards:

- `docs/architecture.md` — architectural changes, new options, new capabilities
- `docs/memory-map.md` — anything that changes what the memory layer can or cannot do, with its honest
  BUILT / WIRED / MEASURED status
- `docs/extensions/<id>.md` — **enforced by a test** for every shipped schema extension: the page must
  exist, carry the sections `## Shape`, `## Cypher`, `## Semantics`, `## Conformance`,
  `## Parity delta`, name every declared shape and parity-delta entry, and be linked from the index
- `docs/schema.md` — graph schema changes
- `docs/getting-started.md` — configuration or DI registration changes
- `CHANGELOG.md` — user-visible changes, under `[Unreleased]`

## Commits and PRs

Branches: `feature/<desc>`, `fix/<desc>`, `docs/<desc>`, `refactor/<desc>`.

Commit messages are imperative and specific — say what changed and, in the body, why. Do not add
tool-generated footers.

Before opening a PR: zero build warnings in `src/`, full unit suite green, integration suite green if
you touched persistence, no boundary violations, docs updated. Reviewers check exactly that list.
