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

## 3. Self-review findings and fixes

2 parallel reviewers (correctness/test-soundness + combined cross-file/conventions):

- **Test-soundness gap** (correctness angle): `DeleteConversationAsync_ThenGetContext_...` persisted a
  message then immediately deleted, without confirming the message had actually been indexed into
  `recentMessages` first. Given NAMS's own asynchronous indexing (the same reason other tests in this file
  need bounded polling), the "empty tiers after delete" assertion could have passed vacuously -- true
  whether or not delete actually cleared anything, since there might never have been anything there to
  begin with. Fixed by adding a pre-delete bounded poll confirming the message is genuinely indexed before
  deleting, so the post-delete "empty" assertion actually proves deletion cleared real content.
- **Doc completeness** (cross-file angle, optional but applied for consistency): the B9 verification bullet
  in `docs/architecture.md` logs each phase's additions to `AgentMemory.Nams`'s dependency surface; added a
  clause for this branch's `InternalsVisibleTo` grant, noting explicitly that it's invisible to B9
  enforcement (confirmed by re-reading `PackageBoundaryGuardTests`'s actual check logic, not assumed).
- A second correctness-angle observation (`DeserializeAsync<T>` throwing if NAMS ever returns an empty body
  for a 200) was confirmed to be an existing assumption shared by all 6 client operations, not a new defect
  introduced here -- no fix needed, noted for awareness only.

Re-verified after fixes: 13/13 live tests still green.

## 4. Verification

- `dotnet build AgentMemory.slnx -c Release` -- 0 warnings, 0 errors.
- `dotnet test tests/AgentMemory.Tests.Unit` -- full suite green, +2 new unit tests (success path + retry).
- `dotnet test tests/AgentMemory.Tests.Integration --filter "...NamsLiveConnectivityTests"` -- **13/13 live**
  (11 previous + 2 new), against the real NAMS SaaS.

## 5. Definition of done

- [x] `DeleteConversationAsync` added to `INamsClient`/`Neo4jNamsClientAdapter`, instrumented, unit tested.
- [x] 2 live tests added: delete-then-verify-degraded-state, and delete-twice-is-idempotent.
- [x] Full unit + live suites green.
- [x] Self-reviewed and fixes applied.
- [ ] PR opened, CI green, merged to `main`.
