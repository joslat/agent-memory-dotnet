# NAMS Phase 9 — Observability — Planning and Implementation Plan

**Prepared:** 2026-07-17
**Branch:** `nams/phase9-observability`
**Purpose:** Executes engineering plan Phase 9 ("Observability and operations"). Per §12's recommended
delivery sequence, this comes right after "Live sandbox proof" (today's earlier live validation +
`NamsAgent` sample) and before the optional Phase 7 convergence.

## 1. Design decisions

- **BCL diagnostics directly, no `AgentMemory.Observability` dependency.** That package decorates the
  direct backend's Core interfaces (`IMemoryService`, extractors) via `TryAddScoped` wrapping -- none of
  which exist on the NAMS path. `AgentMemory.Nams` (B9) stays at zero sibling references by using
  `System.Diagnostics.ActivitySource`/`System.Diagnostics.Metrics.Meter` directly -- pure BCL, not an
  external package, so B9's boundary rule is untouched.
- **Spans/metrics live at two layers.** `Neo4jNamsClientAdapter` (the actual HTTP-call boundary) emits
  `memory.backend.operations`/`.duration`/`.failures`/`.retries`/`.rate_limited`, one per real REST call
  (`resolve_conversation`, `get_context`, `store_turn`, `search_entities`, `list_entities`). `.context_items`/
  `.context_truncated` are emitted from `NamsRecallService` (it alone knows about budget truncation) and
  `.unknown_write_outcomes` from `NamsPersistenceService` (it alone classifies that outcome). All metric
  tags are the plan's own low-cardinality set (`backend`, `operation`, `status`, `failure_kind`) -- never an
  owner/workspace/conversation ID, memory text, or a prompt.
- **Cancellation is not counted as a failure**, per the plan's own test list -- `Neo4jNamsClientAdapter`
  records a distinct `status=cancelled` on the operations counter and never touches the failures counter for
  caller-requested cancellation.
- **A new `INamsClient.ListEntitiesAsync`** (`GET /v1/entities`, confirmed live) was added specifically to
  give the health check a safe, side-effect-free, no-precondition probe -- `SearchEntitiesAsync` requires a
  non-empty query (confirmed live: an empty query returns 400) and `GetContextAsync` requires an existing
  conversation, so neither works as a default connectivity probe.
- **Health check is a small, framework-agnostic interface** (`INamsHealthCheck`/`NamsHealthCheckResult`/
  `NamsHealthStatus`), not an ASP.NET Core `IHealthCheck` -- avoids a new package dependency for every
  consumer; a host that wants the ASP.NET Core shape adapts this thin result itself. Distinguishes
  `Unhealthy` (auth/network/timeout/unexpected) from `Degraded` (rate-limited) per the plan's own
  requirement ("status distinction between unhealthy and degraded/rate-limited"). Never performs a
  destructive write, by construction (only ever calls the new read-only `ListEntitiesAsync`).
- **Retry counting via a callback, not a metrics dependency in `NamsRetryPolicy`.** `ExecuteAsync` gained an
  optional `Action? onRetry` parameter, invoked once per actual retry -- keeps the retry policy itself free
  of any metrics-type dependency; the adapter supplies the callback.

## 2. Explicitly out of scope

- **Data-governance verification** (conversation/user deletion behavior, export, region/classification
  restrictions) -- the plan's own list requires either live testing against real deletion behavior or an
  organizational decision neither of which this PR can make alone.
- **`docs/security/production-checklist.md`** -- reviewed, deliberately left untouched. It's entirely
  direct-Neo4j-backend-scoped (threat-model TT-01 through TT-11) and NAMS isn't released yet (no preview,
  Phase 12 not started) -- grafting a half-formed NAMS item onto it now would be premature.
- **Phase 8 (MCP tools)** and further **Phase 10 test-matrix expansion** -- separate phases, next in this
  session's queue.

## 3. Log review (plan's own checklist item)

Reviewed every `_logger.Log*` call site in `AgentMemory.Nams` and `AgentMemory.AgentFramework.Nams`
(`grep` across both packages). None log an API key, recalled memory content, or a raw message body --
only conversation/session/application IDs and static messages. `NamsClientExceptionMapper.Redact()` already
scrubs the configured API key from any exception message before it can reach a log call (a Phase 2
guarantee, unchanged here). No changes needed -- clean by design, not clean by omission.

## 4. New/changed files

- `src/AgentMemory.Nams/Observability/` (new folder): `NamsActivitySource.cs`, `NamsMetrics.cs`,
  `NamsMetricTags.cs`, `NamsHealthStatus.cs`, `NamsHealthCheckResult.cs`, `INamsHealthCheck.cs`,
  `NamsHealthCheck.cs`.
- `src/AgentMemory.Nams/Client/INamsClient.cs` / `Neo4jNamsClientAdapter.cs` -- new `ListEntitiesAsync`;
  `InvokeAsync` instrumented with spans/metrics per operation.
- `src/AgentMemory.Nams/Client/NamsRetryPolicy.cs` -- new optional `onRetry` callback parameter.
- `src/AgentMemory.Nams/Recall/NamsRecallService.cs` -- records `context_items`/`context_truncated`.
- `src/AgentMemory.Nams/Persistence/NamsPersistenceService.cs` -- records `unknown_write_outcomes`.
- `src/AgentMemory.Nams/NamsServiceCollectionExtensions.cs` -- registers `NamsMetrics`, `INamsHealthCheck`.
- Test updates: every existing `INamsClient` test fake gained `ListEntitiesAsync`; every direct
  `NamsRecallService`/`NamsPersistenceService`/`Neo4jNamsClientAdapter` construction site gained a
  `NamsMetrics` argument.
- New tests: `NamsMetricsTests.cs`, `NamsMetricTagsTests.cs`, `NamsHealthCheckTests.cs`,
  `NamsObservabilityTests.cs` (+ `NamsObservabilityCollection.cs`, mirroring the existing
  `AgentMemory.Tests.Unit.Observability.ObservabilityCollection` pattern for tests that attach a
  process-wide `MeterListener`) -- verifies actual metric recording, not just that construction doesn't
  throw: success/duration, failure+failure_kind, rate-limited, retries, cancellation-not-a-failure,
  context items/truncation, unknown write outcomes. Plus 2 new `NamsRetryPolicy` tests for the `onRetry`
  callback itself.

## 5. Verification

- `dotnet build AgentMemory.slnx -c Release` -- 0 warnings, 0 errors.
- `dotnet test tests/AgentMemory.Tests.Unit` -- full suite green (189 Nams-filtered, ~30 net new).

## 6. Definition of done

- [x] Spans + metrics instrumented at the client/recall/persistence layers.
- [x] Health check built and covered.
- [x] Log review complete, no changes needed.
- [x] Full unit suite green.
- [ ] Self-reviewed, PR opened, CI green, merged to `main`.
