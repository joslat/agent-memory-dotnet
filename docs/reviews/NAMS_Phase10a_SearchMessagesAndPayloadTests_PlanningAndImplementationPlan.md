# NAMS Phase 10a — SearchMessagesAsync + Payload Edge-Case Tests — Planning and Implementation Plan

**Prepared:** 2026-07-17
**Branch:** `nams/phase10a-search-messages-and-payload-tests`
**Purpose:** First increment of the follow-up Phase 10 work (10a-10d + a TCK Platinum research task), per the
user's explicitly requested order: smallest/cleanest items first.

## 1. `INamsClient.SearchMessagesAsync`

Confirmed live: `POST /v1/conversations/{id}/search` with `{query, limit}`, returning
`{messages: [...], searchType}` in exactly the existing `NamsMessage` shape (score/tokenCount present,
conversationId absent -- matching the `recentMessages` context shape, not the bulk-add response shape).
Phase 2 originally dropped this (`SearchMessagesAsync`) for lack of confirmation; added now, standalone.

**Deliberately NOT wired into `INamsRecallService.RecallAsync`** -- that's Phase 4-6's already-shipped, live-
tested, unit-tested behavior. Changing what automatic recall does is a separate decision from adding a new
client capability, and isn't part of this increment's scope. A future MCP `nams_search_messages` tool (a
natural Phase 8 follow-up, per the plan's own `search_messages` span name already anticipated in Phase 9) is
also explicitly deferred, not built here.

## 2. Payload edge-case live tests

Added to `NamsLiveConnectivityTests.cs`. Live-verified each edge case's REAL behavior before writing the
test's assertions, rather than assuming what "should" happen:

- **Empty content** -- verified live that this is a genuine **NAMS API inconsistency**: the single-message
  endpoint (`POST /v1/conversations/{id}/messages`) rejects empty content with 400, but the bulk endpoint
  (`POST /v1/conversations/{id}/messages/bulk`) -- the *only* one `PersistTurnAsync` ever calls -- accepts it
  and returns a real message ID. The test asserts the actual (accepted) behavior, not the wrong assumption
  that empty content fails uniformly.
- **Large message** -- verified live that NAMS's write path accepts at least 50,000 characters. For the
  round-trip half of the test, deliberately used 5,000 characters (comfortably under
  `NamsRecallOptions.MaxTotalCharacters`'s 8,000 default) after discovering the first version of this test
  failed for an unrelated reason: a single item larger than the entire recall character budget is silently
  excluded by `NamsRecallService`'s own `ApplyCharacterBudget` (correct, pre-existing, separately-tested
  behavior) -- the test was accidentally measuring our own truncation logic, not NAMS's payload handling.
- **Multi-message single turn** -- 2 user + 2 assistant messages in one `PersistTurnAsync` call; asserts all
  4 come back with IDs from the one bulk request.
- **Code-like content with special characters** (braces, quotes, escaped newlines, backticks) -- the plan's
  "non-text" payload category, interpreted as structured/code-like text (there's no binary/attachment
  concept anywhere in `NamsMessageToPersist`, so "non-text" can't mean literal binary).

## 3. Self-review findings and fixes

2 parallel reviewers (correctness/test-soundness + combined cross-file/conventions):

- **Test over-claiming its name** (correctness angle): `PersistTurnAsync_MultipleMessagesInOneTurn_PersistsAllInOrder`
  asserted only a bare count (4 IDs), never actually verifying order -- `NamsPersistenceResult.PersistedMessageIds`
  has no content/role correlation, and NAMS's own bulk-response ordering guarantee (if any) isn't independently
  confirmed, so nothing in this test could prove ordering even if it tried. Renamed to
  `...PersistsAllFour` and added a doc comment stating exactly what is and isn't proven, rather than building a
  shakier ordering-verification mechanism to match an overclaimed name.
- **Stale interface doc comment** (cross-file angle): `INamsClient.cs`'s top-of-interface comment still said
  "covers only the 4 operations" and called `SearchMessagesAsync` "dropped" -- both wrong after this branch
  (and already wrong before it, post-Phase-9's `ListEntitiesAsync`). This branch is exactly the "add it once
  verified live" trigger that comment itself named, so fixed it here rather than leaving it stale again.

Re-verified after fixes: 11/11 live tests still green.

## 4. Verification

- `dotnet build AgentMemory.slnx -c Release` -- 0 warnings, 0 errors.
- `dotnet test tests/AgentMemory.Tests.Unit` -- full suite green, +1 new unit test (`SearchMessagesAsync`
  client test) plus 5 test-fake updates (every `INamsClient` implementer needed the new method).
- `dotnet test tests/AgentMemory.Tests.Integration --filter "...NamsLiveConnectivityTests"` -- **11/11 live**
  (7 previous + 4 new), ~7s total, against the real NAMS SaaS.

## 5. Definition of done

- [x] `SearchMessagesAsync` added to `INamsClient`/`Neo4jNamsClientAdapter`, instrumented (Phase 9 metrics,
      operation name `search_messages` matching the plan's own span-name anticipation), unit tested.
- [x] 4 payload edge-case live tests added, each verified against real live behavior first.
- [x] Full unit + live suites green.
- [x] Self-reviewed and fixes applied.
- [ ] PR opened, CI green, merged to `main`.
