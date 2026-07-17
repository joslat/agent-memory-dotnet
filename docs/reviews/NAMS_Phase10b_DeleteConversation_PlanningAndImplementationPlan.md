# NAMS Phase 10b — Data Lifecycle: Delete Conversation — Planning and Implementation Plan

**Prepared:** 2026-07-17
**Branch:** `nams/phase10b-delete-conversation`
**Purpose:** Second of five follow-up Phase 10 increments (10a-10d + TCK Platinum research).

## 1. Live behavior verified before writing any code or assertions

Per this whole Phase 10 push's established discipline, checked real behavior first:

- `DELETE /v1/conversations/{id}` returns `{"status": "deleted"}` -- **not** `{"status": "success"}` as the
  Phase 0 OpenAPI-snapshot guess assumed.
- **Idempotent**: deleting an already-deleted (or otherwise nonexistent) conversation still returns 200, not
  404. Safe to treat as retryable like any other read.
- **`GetContextAsync` keeps returning 200 with empty tiers after delete** -- `reflections`/`observations`/
  `recentMessages` all become empty arrays, never a 404. Recall against a deleted conversation degrades
  gracefully rather than failing.
- **Writing after delete 404s** -- confirmed for both the single-message endpoint and the bulk endpoint
  (the only one `PersistTurnAsync` ever calls). `NamsPersistenceService` classifies a 404 as `Failed` (not
  `UnknownWriteOutcome`, which is reserved for genuinely ambiguous Network/Timeout failures -- a 404 is an
  unambiguous "this doesn't exist").

## 2. Scope decision: `INamsClient` only, not exposed via any public service

`DeleteConversationAsync` is added to `INamsClient`/`Neo4jNamsClientAdapter` only -- **not** wired into
`INamsPersistenceService`, `INamsConversationResolver`, or any MCP tool. Data-lifecycle operations
(deletion, export) are explicitly called out in the plan's own Phase 9 text as needing live testing and/or
an organizational decision before being exposed as a routine capability. This gives a host that has already
made that decision the low-level operation without having to reach around this package to call the REST
endpoint directly -- it does not itself make the "should this be automatic/routine" decision.

Because `INamsClient` is `internal`, the live-integration test needs direct access to it. Added
`AgentMemory.Nams.csproj`'s `InternalsVisibleTo` grant to `AgentMemory.Tests.Integration`, mirroring
`AgentMemory.AgentFramework`'s identical existing grant to both test projects.

## 3. Verification

- `dotnet build AgentMemory.slnx -c Release` -- 0 warnings, 0 errors.
- `dotnet test tests/AgentMemory.Tests.Unit` -- full suite green, +2 new unit tests (success path + retry).
- `dotnet test tests/AgentMemory.Tests.Integration --filter "...NamsLiveConnectivityTests"` -- **13/13 live**
  (11 previous + 2 new), against the real NAMS SaaS.

## 4. Definition of done

- [x] `DeleteConversationAsync` added to `INamsClient`/`Neo4jNamsClientAdapter`, instrumented, unit tested.
- [x] 2 live tests added: delete-then-verify-degraded-state, and delete-twice-is-idempotent.
- [x] Full unit + live suites green.
- [ ] Self-reviewed, PR opened, CI green, merged to `main`.
