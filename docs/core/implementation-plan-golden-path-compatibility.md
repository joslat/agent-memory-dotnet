# Implementation Plan - Golden Path and Compatibility Automation

Status: accepted and executed in this documentation/code pass.
Date: 2026-07-09.

This plan turns the sample and compatibility review into concrete repository work. The goal is to make the first demo trustworthy, keep sample documentation aligned with the shipped API, and make upstream compatibility visible in automation.

## Goals

- Make `AgentMemory.Sample.AgentWithMemory` the official golden path.
- Demonstrate the production identity model: store -> owner -> session.
- Keep the sample runnable offline while leaving clear replacement seams for real MEAI chat and embedding providers.
- Remove stale documentation that still references obsolete or manually registered APIs.
- Treat upstream `neo4j-labs/agent-memory` compatibility as a tested guardrail, not a runtime compatibility layer.
- Automate the checks that can run today and document the TCK bridge path for behavioral conformance.

## Step 1 - Promote AgentWithMemory

`AgentMemory.Sample.AgentWithMemory` is the first sample users should run. It demonstrates the canonical Microsoft Agent Framework memory shape: `ChatClientAgent`, `Neo4jMemoryContextProvider`, memory tools, multi-turn `AgentSession`, serialize/restore, and durable recall beyond a single session.

Execution:

- Update `samples/README.md` to call `AgentWithMemory` the flagship golden path.
- Update `docs/getting-started.md` so the first runnable MAF sample is `AgentWithMemory`, while `MinimalAgent` is described as a lower-level facade sample.

Acceptance criteria:

- New users can identify the intended first sample without reading every sample folder.
- The samples index and getting-started guide agree.

## Step 2 - Add Identity Scoping to the Golden Path

The golden path must not imply that agent ID fallback is the production correlation model. It should explicitly stamp memory identity into the MAF session.

Execution:

- Use `WithMemoryIdentity(...)` in `AgentWithMemory` for `userId`, `sessionId`, `conversationId`, and `applicationId`.
- Use one owner/application across two sessions to demonstrate durable cross-session recall.
- Wrap each `agent.RunAsync(...)` call in `IWritableMemoryOwnerContext.BeginOwnerScope(userId)` so model-invoked memory tools inherit the same owner context.

Acceptance criteria:

- Provider recall/persistence reads session identity from the state bag.
- Memory tools run under trusted ambient owner context rather than relying on model-supplied owner parameters.

## Step 3 - Keep Offline Defaults, Add Real Provider Hooks

The sample should stay easy to run without API keys, but the code should show the replacement seam for production.

Execution:

- Register `StubEmbeddingGenerator` via `TryAddSingleton`.
- Register `EchoChatClient` via `TryAddSingleton<IChatClient, EchoChatClient>`.
- Resolve `IChatClient` from DI instead of constructing the mock inline.

Acceptance criteria:

- Offline execution remains deterministic.
- Production hosts can replace chat and embedding providers in DI without changing the memory wiring.

## Step 4 - Clean Sample and Getting-Started Docs

Docs should reflect current APIs and current DI registration behavior.

Execution:

- Replace stale `MemoryToolFactory.CreateTools` mentions with `CreateAIFunctions` in sample docs.
- Remove manual registration guidance for `AgentTraceRecorder` and `MemoryToolFactory`; `AddAgentMemoryFramework(...)` registers them.
- Keep `MinimalAgent` documented, but no longer call it the best starting point.

Acceptance criteria:

- No active sample README points users at obsolete tool creation APIs.
- DI examples do not ask users to duplicate current framework registrations.

## Step 5 - Document Compatibility Guardrail

The compatibility story should be explicit and dated.

Execution:

- Add ADR 0014: schema compatibility guardrail, not runtime compatibility layer.
- Clarify that upstream-compatible labels, relationship names, and snake_case properties are preserved where useful.
- Document intentional .NET divergences: `owner_id`, `owner_key`, `invalidated_at`, stronger isolation, and Neo4j/.NET operational tooling.

Acceptance criteria:

- Future schema changes have a decision record to evaluate against.
- Intentional divergences are visible rather than treated as accidental drift.

## Step 6 - Add Sample Smoke-Build CI

Samples are user-facing documentation. They should compile as part of CI even if the solution build already covers them indirectly.

Execution:

- Add a dedicated sample smoke-build step to `squad-ci.yml`.
- Build each sample project in Release with `--no-restore` after the solution restore/build.

Acceptance criteria:

- CI output clearly shows sample compile health.
- Future sample rot is visible in a named job step.

## Step 7 - Automate Compatibility Checks

Compatibility should have three layers: static schema parity, behavioral TCK strategy, and upstream snapshot refresh cadence.

Execution:

- Run `agentmemory schema-parity` in CI through the CLI project.
- Keep unit tests that drive the same `SchemaParityVerifier` against embedded upstream snapshots.
- Add an upstream compatibility watch workflow that runs on a schedule and manually, records upstream `agent-memory` and `agent-memory-tck` state, and runs static parity against the embedded snapshot.
- Document the TCK path: use `neo4j-labs/agent-memory-tck` directly if a .NET bridge can satisfy its adapter contract; otherwise mirror high-value TCK scenarios as .NET integration tests.
- Refresh embedded snapshots on tagged upstream releases or material schema changes on `main`; do not chase every docs-only upstream commit.

Acceptance criteria:

- Static schema compatibility is checked on normal CI.
- Upstream movement is visible without pretending every upstream commit should force a local schema change.
- The TCK is tracked as behavioral conformance work, not confused with the current static schema diff.

## Execution Summary

This pass implements the near-term pieces: golden-path sample scoping, sample docs cleanup, compatibility ADR/documentation, sample smoke-build CI, static schema parity CI, and scheduled upstream compatibility watch.

Follow-up status: the 2026-07-09 behavioral compatibility pack implemented the local mirrored scenarios, compatibility catalog, read-audit/history expansion, and recency/frequency reranking evidence, and the pack was merged to `main` on 2026-07-11. The remaining Step 7 work is now concrete follow-up work: implement the upstream TCK HTTP bridge and map local mirrored scenarios to stable `SCN-*` IDs on `codex/tck-bridge-scn-mapping`. See [`behavioral-compatibility-pack-status.md`](behavioral-compatibility-pack-status.md).

## Follow-up Execution - 2026-07-09

After the initial sample/compatibility cleanup, the next implementation slice added two confidence features:

- A read-only `IMemoryHistoryService` plus `agentmemory history` command for long-term memory lifecycle inspection: live versus invalidated status, `SUPERSEDED_BY` links, valid-time windows, source message ids, owner scope, and metadata.
- Live Neo4j shakedown coverage for the golden-path owner-scope pattern used by `AgentWithMemory`: `BeginOwnerScope("alice")` stamps tool-style writes and prevents `bob` from reading them.

This keeps compatibility work practical: schema parity catches static drift, history inspection exposes the .NET temporal/supersession extensions, and integration coverage protects the production identity path shown by the flagship demo.

## Step 8 - Add Memory Quality and Performance Evaluation

Compatibility should now grow into a deterministic evaluation track. The first target is memory behavior itself, not generated chat-answer quality and not final model context quality.

Execution:

- Add `performance-quality-evaluation.md` as the canonical plan for memory quality/performance evaluation.
- Record ADR 0016: evaluate memory-layer behavior first, then add context/answer evaluation later.
- Add TCK-mirrored integration scenarios for long-term memory, reasoning memory, cross-memory graph behavior, and strict owner/shared visibility.
- Use future scenario runners to compare Python/upstream and .NET with identical Neo4j setup, fixture data, embeddings, query sets, and normalized result records.

Acceptance criteria:

- The evaluation boundary is documented.
- Python-vs-.NET comparison metrics are explicit.
- The first executable service-level compatibility slice runs without relying on chat history or model-generated answers.
