# NAMS Live Validation & Integration Test Scaffold — Planning and Implementation Plan

**Prepared:** 2026-07-17
**Branch:** `nams/live-validation-integration-tests`
**Purpose:** Not one of the engineering plan's numbered phases -- this is a small, opportunistic follow-up
that became possible mid-session when a live NAMS SaaS account and API key were provisioned. It closes two
concrete gaps Phases 1-6 could only leave as documented unknowns (no live sandbox existed yet), and adds a
skip-cleanly live-integration test scaffold so the whole Phase 4-6 pipeline (identity resolution -> recall ->
persistence) is now verified against the real service, not just deterministic fakes.

## 1. Trigger and scope

Mid-session, a live NAMS SaaS account became available (`memory.neo4jlabs.com`), including a dedicated,
isolated `agent-memory-dotnet-dev` workspace (with its own free, auto-expiring managed database) created
specifically so live testing never touches a production/real workspace. Comparing the actual authenticated
API docs against the existing `AgentMemory.Nams` client surfaced two small, concrete findings:

1. The real public SaaS base URL/auth contract matches Phase 2's guessed-from-public-docs shapes almost
   exactly (conversations, messages, messages/bulk, entities/search, context) -- no client-shape bugs found.
2. `NamsOptions.WorkspaceId` was captured in configuration but never actually attached as a request header --
   harmless for a workspace-scoped API key (which carries its workspace implicitly), but a real gap for an
   account-wide (admin) key, which needs `X-Workspace-Id` to target a specific workspace.

**In scope:**
- `NamsWellKnown.Endpoint` -- a non-secret constant for the public SaaS base URL, so consuming code/tests
  don't need an env var for something that's the same for everyone.
- The `X-Workspace-Id` header fix in `NamsClientFactory.ConfigureHttpClient`.
- A live-integration test scaffold (env-var-gated, skips cleanly and never runs in CI) exercising the full
  public surface (`INamsConversationResolver` -> `INamsPersistenceService` -> `INamsRecallService`) against
  the real, isolated dev workspace.

**Explicitly out of scope** (not triggered by this work):
- Engineering plan Phase 7 (backend-neutral convergence) -- still gated on its own entry criteria, unrelated
  to this.
- Phase 8 (MCP/capability-aware tools), Phase 9 (observability), the rest of Phase 10 (contract tests, HTTP
  simulation, TCK Platinum), Phase 11 (sample) -- untouched by this branch.
- `SearchMessagesAsync`/`POST /v1/conversations/{id}/search` -- now confirmed to exist in the live API (it
  was dropped from Phase 2's `INamsClient` for lack of confirmation), but adding it is a separate, later
  decision, not bundled into this opportunistic fix.

## 2. Changes

- `src/AgentMemory.Nams/NamsWellKnown.cs` (new) -- `public static class NamsWellKnown { public static readonly
  Uri Endpoint = new("https://memory.neo4jlabs.com/v1"); }`.
- `src/AgentMemory.Nams/Client/NamsClientFactory.cs` (modified) -- `ConfigureHttpClient` now adds an
  `X-Workspace-Id` default request header when `NamsOptions.WorkspaceId` is set (a static header, unlike the
  per-request-refreshed `Authorization` header `NamsAuthenticationHandler` attaches).
- `tests/AgentMemory.Tests.Unit/Nams/NamsWellKnownTests.cs` (new) -- 1 test.
- `tests/AgentMemory.Tests.Unit/Nams/NamsClientFactoryTests.cs` (modified) -- 2 new tests (header present when
  `WorkspaceId` set; absent when not).
- `tests/AgentMemory.Tests.Integration/Nams/` (new) -- live-integration test scaffold:
  - `NamsLiveCredentials.cs` -- reads `NAMS_API_KEY`/`NAMS_DEV_WORKSPACE_ID` from the environment, with a
    Windows `EnvironmentVariableTarget.User` registry fallback so a credential set mid-session doesn't
    require restarting the IDE/terminal to become visible.
  - `LiveNamsFactAttribute.cs` -- a `[Fact]` variant that sets `Skip` at discovery time when credentials
    aren't available, so these tests never fail (or run) in CI or on a machine without the isolated dev
    workspace configured.
  - `NamsLiveFixture.cs` + `NamsLiveTestCollection.cs` -- a real DI container wired via
    `AddNamsAgentMemory`, exactly as a consuming application would.
  - `NamsLiveConnectivityTests.cs` -- 3 tests: conversation creation, persist-then-recall round trip, and a
    message-with-a-known-entity round trip (bounded polling for NAMS's asynchronous extraction, never
    unbounded per the plan's own Phase 10 release gate). Every identity uses a fresh GUID ("unique test user
    prefix" per Phase 10).
- `tests/AgentMemory.Tests.Integration/AgentMemory.Tests.Integration.csproj` (modified) -- added a
  `ProjectReference` to `AgentMemory.Nams`.

## 3. Verification so far

- `dotnet test tests/AgentMemory.Tests.Unit --filter "FullyQualifiedName~Nams"` -- 162/162 green.
- `dotnet build tests/AgentMemory.Tests.Integration` -- 0 warnings, 0 errors.
- `dotnet test tests/AgentMemory.Tests.Integration --filter "FullyQualifiedName~Nams.NamsLiveConnectivityTests"`
  -- **all 3 live tests passed against the real NAMS SaaS**, ~10s total. First-ever live validation of the
  Phase 4-6 pipeline end to end.

## 4. Definition of done

- [x] Live connectivity confirmed.
- [x] `X-Workspace-Id` gap fixed and covered.
- [x] `NamsWellKnown` added and covered.
- [x] Live-integration test scaffold built and passing against the real service.
- [ ] Self-reviewed, PR opened, CI green (live tests skip cleanly there -- no credentials in CI), merged to
      `main`.
