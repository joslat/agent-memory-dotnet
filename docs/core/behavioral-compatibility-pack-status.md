# Behavioral Compatibility Pack Status

Status: local pack implemented and verified as of 2026-07-09; PR-path TODOs clarified on 2026-07-10.

This document tracks the current behavioral compatibility pack. It is the live work log for the five requested features: TCK bridge/mirrored scenarios, compatibility scenario suite, real-provider golden path, read audit/history expansion, and recency/frequency reranking.

## Upstream Baseline

The current upstream `neo4j-labs/agent-memory-tck` repository describes a formal Technology Compatibility Kit with 189 executable scenarios, stable `SCN-*` scenario IDs, Bronze/Silver/Gold/Platinum tiers, and an HTTP bridge protocol for cross-language conformance. This pack keeps local .NET evidence in two forms:

- mirrored in-process integration scenarios for behavior we can prove directly against the .NET services;
- bridge-readiness documentation and command/task entry points for running the upstream TCK when a bridge adapter is available.

## Status Table

| Order | Feature | Status | Done | Pending | Notes |
|---:|---|---|---|---|---|
| 1 | TCK bridge/mirrored TCK scenarios | Verified | Added `NET-TCK-B-001` short-term Bronze mirror plus live Neo4j mirrors for long-term, owner isolation, relationships, reasoning/tool calls, temporal history/read audit, and vector retrieval. | Upstream HTTP bridge adapter remains the next automation step; local mirrored scenarios are in-process today. | This converts static schema compatibility into executable behavior evidence while avoiding a half-implemented bridge. |
| 2 | Compatibility scenario suite | Verified | Added `CompatibilityScenarioCatalog` and catalog guards covering Bronze/Silver/Gold mirrors, strict owner isolation, real-provider golden path, read audit/history, and recency/frequency ranking. | Keep expanding catalog as upstream `SCN-*` scenarios are mirrored or bridged. | The suite proves upstream-like behavior plus stricter .NET isolation. |
| 3 | Real-provider golden path | Verified | Added a source guard for the `AgentWithMemory` provider seams, documented the VS Code run task, and added `AgentMemory: golden path sample (local Neo4j)`. | A real external provider smoke can be run by a host with provider packages/API keys; repo remains provider-neutral. | No hard dependency on a specific paid provider enters the library. |
| 4 | Read audit / history expansion | Verified | Added `MemoryReadAudit` label/constraint/index, access-audit writes from `UpdateAccessTimestampAsync`, history fields for access counts/audit rows, CLI output, schema parity policy/doc updates, and live integration assertions. | Consider owner-scoped access-update overloads later if by-id admin semantics should become stricter. | This removes the previous upstream-only `MemoryReadAudit` divergence and adds richer .NET audit detail. |
| 5 | Recency/frequency reranker | Verified | Added a live Neo4j behavioral test proving equal-age, frequently accessed memory can outrank the unused semantic-best result when recency/frequency reranking is enabled. | Keep tuning weights with the future quality/performance evaluation harness. | Kept opt-in via `MemoryRankingOptions`; parity mode remains semantic-only. |

## Pending / Next Candidates

Recommended immediate sequence: finish #1 and #2 on `codex/behavioral-compatibility-pack`, then do #3 and open the PR into `main`.

| Priority | Task-Feature | Description | Notes |
|---:|---|---|---|
| 1 | Upstream TCK HTTP bridge | Implement the thin HTTP adapter expected by `neo4j-labs/agent-memory-tck` so the upstream runner can drive this .NET implementation out of process. | This is the cleanest way to turn "mirrored compatibility" into canonical TCK evidence before merge. |
| 2 | Map local scenarios to upstream `SCN-*` IDs | Link each mirrored .NET scenario and catalog entry to the stable upstream scenario IDs and conformance tiers. | Gives reviewers traceability from local tests to the TCK contract. |
| 3 | Open PR from branch to `main` | Open the PR for `codex/behavioral-compatibility-pack` after #1 and #2 are done. | A draft PR could be opened earlier, but the strongest conformance PR waits for bridge and mapping. |
| 4 | Real-provider golden-path smoke | Run `AgentWithMemory` with an actual external MEAI chat/embedding provider from a host environment. | Keep provider packages/API keys out of the repo; record the smoke as evidence only. |
| 5 | Owner-scoped access tracking | Consider owner-scoped access timestamp/read-audit overloads for by-id update paths. | Tightens admin-style by-id semantics; lower priority because recall/read paths are already scoped. |
| 6 | Expand scenario catalog | Add more mirrored Bronze/Silver/Gold/Platinum scenarios as upstream TCK behavior evolves. | Keep catalog updates tied to upstream TCK tags or meaningful `main` changes. |
| 7 | Memory quality/performance harness | Implement deterministic evaluation for Recall@K, MRR/NDCG, latency, owner leaks, temporal pass rate, and provenance completeness. | Enables Python-vs-.NET comparison without grading chat history or final model context first. |
| 8 | Upstream snapshot refresh automation | Keep the scheduled upstream watch and embedded schema snapshots aligned with relevant upstream releases/main changes. | Treat warnings as review triggers, not automatic adoption. |
| 9 | Preference supersession / graph expansion | Add cohesive semantic-history features if evaluation data shows they improve recall quality. | Fits the history/read-audit/reranking family, but it is not a pre-PR gate. |
| 10 | PR documentation polish | Make the PR description cite verification results, intentional divergences, and remaining non-gates. | Use this file as the source of truth for the PR narrative. |

## Verification Log

| Date | Evidence | Result | Notes |
|---|---|---|---|
| 2026-07-09 | `dotnet build tools\\AgentMemory.Cli\\AgentMemory.Cli.csproj --no-restore` | Passed, 0 warnings | Prior CLI/evaluation work before this pack. |
| 2026-07-09 | `dotnet run --no-restore --project tools\\AgentMemory.Cli\\AgentMemory.Cli.csproj -- schema-parity --upstream-version 0.5.0` | Passed | Current embedded Python v0.5.0 static schema snapshot remains compatible before this pack's schema changes. |
| 2026-07-09 | `dotnet test tests\\AgentMemory.Tests.Unit\\AgentMemory.Tests.Unit.csproj --no-restore --filter FullyQualifiedName~CliCommandsTests` | Passed, 27/27 | Prior CLI command wiring check. |
| 2026-07-09 | `dotnet test tests\\AgentMemory.Tests.Unit\\AgentMemory.Tests.Unit.csproj --no-restore --filter FullyQualifiedName~CypherQuerySnapshotTests` | Passed, 574/574 | Regenerated Cypher snapshot for `MemoryReadAudit` constraint/index and query inventory count 143. |
| 2026-07-09 | `dotnet test tests\\AgentMemory.Tests.Unit\\AgentMemory.Tests.Unit.csproj --no-restore --filter "FullyQualifiedName~UpstreamSchemaParityTests|FullyQualifiedName~TemporalQueryTests|FullyQualifiedName~DecayQueryTests|FullyQualifiedName~AgentWithMemoryGoldenPathSourceTests"` | Passed, 46/46 | Schema parity/unit query/golden-path seam guards. |
| 2026-07-09 | `dotnet test tests\\AgentMemory.Tests.Integration\\AgentMemory.Tests.Integration.csproj --no-restore --filter "FullyQualifiedName~TckMirroredBehaviorTests|FullyQualifiedName~CompatibilityScenarioCatalogTests|FullyQualifiedName~Neo4jMemoryDecayServiceIntegrationTests|FullyQualifiedName~RecencyRerankIntegrationTests"` | Passed, 22/22 | Live Neo4j/Testcontainers behavior for TCK mirrors, catalog, read audit, and recency/frequency ranking. |
| 2026-07-09 | `dotnet build AgentMemory.slnx --no-restore` | Passed, 0 warnings | Full solution build including samples and tests. |
| 2026-07-09 | `dotnet run --no-restore --project tools\\AgentMemory.Cli\\AgentMemory.Cli.csproj -- schema-parity --upstream-version 0.5.0` | Passed | Compatible; 11 documented divergences remain intentional (`User`, relationship extensions, owner/time/read-audit detail fields). |

## Commit Record

This pack was committed as `8626fed` (`Add behavioral compatibility pack`) and pushed to `origin/codex/behavioral-compatibility-pack` on 2026-07-10. A follow-up status-doc commit, `94521e7` (`Update compatibility pack commit record`), recorded the branch evidence. Generated artifacts under `artifacts/` stay uncommitted.
