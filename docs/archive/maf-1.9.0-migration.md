# Microsoft Agent Framework 1.9.0 Migration

**Status:** Complete — 2026-06-05.
**Release:** https://github.com/microsoft/agent-framework/releases/tag/dotnet-1.9.0

## Summary

Migrated the codebase from Microsoft Agent Framework (MAF) **1.1.0 → 1.9.0**. The migration was
near-mechanical: the MAF abstractions the adapter depends on (`AIContextProvider`,
`InvokingContext`/`InvokedContext`, `AIContext`, `AgentSession`/`StateBag`, `ChatHistoryProvider`)
are **source-compatible** between 1.1.0 and 1.9.0, so no adapter code changes were required — only
package-version bumps (and the transitive `Microsoft.Extensions.AI.Abstractions` floor that MAF 1.9.0
requires). This also resolves the version-drift the MAF audit flagged (audit baseline 1.1.0 vs. the
on-disk 1.3.0 guide); the codebase now sits well past both.

## Version matrix

| Package | Before | After | Where |
|---|---|---|---|
| `Microsoft.Agents.AI.Abstractions` | 1.1.0 | **1.9.0** | `src/AgentMemory.AgentFramework`, `tests/AgentMemory.Tests.Unit` |
| `Microsoft.Agents.AI` (concrete) | 1.1.0 | **1.9.0** | `samples/AgentMemory.Sample.RealAgent` |
| `Microsoft.Extensions.AI.Abstractions` | 10.4.1 | **10.5.1** | all src (`Abstractions`, `Core`, `Neo4j`, `Extraction.Llm`, `AgentFramework`) + `Tests.Unit` |

MAF 1.9.0 requires `Microsoft.Extensions.AI.Abstractions >= 10.5.1`; bumping it repo-wide keeps a
single consistent MEAI version and avoids NU1605 downgrade errors (the build treats those as errors).

## What was NOT required

- No changes to `Neo4jMemoryContextProvider`, `Neo4jChatMessageStore`, `Neo4jChatHistoryProvider`,
  `Neo4jMicrosoftMemoryFacade`, or `MafTypeMapper` — the overridden methods and consumed types are
  unchanged across 1.1.0 → 1.9.0.
- No changes to the concrete-agent usage in the RealAgent sample (`AsAIAgent`,
  `ChatClientAgentOptions`, `CreateSessionAsync`, `RunAsync`, `AsBuilder().UseOpenTelemetry()`).

## Verification

- Full solution builds clean (0 warnings, 0 errors) on MAF 1.9.0 + MEAI 10.5.1.
- `2,139` unit + `31` SemanticKernel tests green (incl. the 93 MAF adapter unit tests).
- `AgentMemory.Sample.RealAgent` runs end-to-end against live Neo4j (multi-turn `AgentSession`,
  native MAF OpenTelemetry `chat` spans, memory persisted/recalled).

## Notes / follow-ups

- `docs/reference/maf-1.3.0-migration-guide.md` and `docs/reference/maf-audit-review-and-improvement-plan.md`
  predate this bump; their 1.1.0/1.3.0 API references are still broadly accurate (the API surface the
  adapter uses is stable) but should be read as historical baselines.
- Consider central package management (`Directory.Packages.props`) to single-source these versions and
  prevent future drift across the (now consistent) MEAI/MAF pins.
