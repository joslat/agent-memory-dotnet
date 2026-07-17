# NAMS Phase 10 — Live Scenario Expansion — Planning and Implementation Plan

**Prepared:** 2026-07-17
**Branch:** `nams/phase10-expansion`
**Purpose:** Expands live-integration coverage from the earlier live-validation branch's 3 scenarios toward
the engineering plan's own "mandatory scenario matrix" (§7 Phase 10). Not an attempt at all 18 matrix areas
in one PR -- see §2 for what's covered elsewhere already, and §3 for what's explicitly deferred.

## 1. What this adds

4 new tests in `tests/AgentMemory.Tests.Integration/Nams/NamsLiveConnectivityTests.cs`, all run against the
real NAMS SaaS (`agent-memory-dotnet-dev` workspace):

- **`ResolveAsync_SameIdentityResolvedConcurrently_ReconcilesToOneConversation`** (matrix: Conversation ->
  concurrent create) -- resolves the SAME identity twice concurrently; asserts both calls agree on one
  conversation ID and exactly one of the two actually created it. Phase 3's `KeyedAsyncLock` reconciliation
  was unit-tested against a fake client; this is the first time it's proven against the real API's actual
  concurrent-create race behavior.
- **`PersistTurnAsync_TwoConcurrentUsers_DoNotCrossContaminateChatHistory`** (matrix: Concurrency -> parallel
  turns, different users) -- two different identities' conversations, persisted concurrently, each recalling
  only its own marker text, never the other's. Deliberately scoped to the **chat-history/recent-message
  tier** (conversation-scoped), not entity extraction -- the `AgentMemory.Sample.NamsAgent` README already
  documents that NAMS's entity search is workspace-wide, not conversation-scoped, as a separate, known
  characteristic; this test doesn't re-litigate that, it confirms the tier that IS properly isolated.
- **`RecallAsync_CancelledBeforeTheHttpRoundTripCompletes_PropagatesOperationCanceledException`** (matrix:
  Cancellation -> during request) -- cancels 1ms after issuing a live recall call; confirms cancellation
  propagates as `OperationCanceledException` against the real service, not just the fake-handler-backed unit
  tests.
- **`PersistTurnAsync_UnicodeAndEmojiContent_RoundTripsByteForByte`** (matrix: Payload -> Unicode) --
  persists Japanese text + an emoji + accented characters, confirms it recalls back byte-for-byte.

## 2. Already covered elsewhere (not duplicated here)

- **Identity** (missing/valid), **Conversation** (create/reuse), **Recall** (empty/normal), **Persistence**
  (success) -- the original 3 live tests (this session's earlier live-validation branch).
- **Service failure** (401/403/404/429/500/502, malformed JSON) -- `NamsClientExceptionMapperTests.cs`,
  already comprehensive via HTTP-simulation (a real network call can't deterministically produce a 500 from
  the live service on demand; simulation is the correct tool here, not live).
- **Rate limit** (retry, `Retry-After`), **Cancellation** (before request, during retry delay) --
  `NamsRetryPolicyTests.cs`, HTTP-simulation.
- **Observability** (expected metrics, no secrets/PII) -- Phase 9, just merged.
- **Eventual consistency** (immediate absence, bounded poll, completion) -- the original 3 live tests'
  bounded-poll pattern, now also exercised implicitly by this PR's new tests.
- **Security** (prompt injection, delimiter forgery, wrong user/workspace) -- these are properties of the
  MAF-layer mapping/gating logic (`NamsMafTypeMapper`, Phase 6), already unit-tested there; they're not
  live-NAMS-specific behavior to re-verify against the real service.

## 3. Explicitly deferred

- **Multi-instance mapping** (separate resolver processes, crash reconciliation) -- needs actual
  multi-process orchestration, a bigger investment than fits this increment.
- **Session restore as an asserted integration test** (vs. the `NamsAgent` sample's informal demonstration)
  -- real value, but a distinct piece of work (building a live MAF-agent test harness), left for later.
- **DNS/TLS failure simulation** -- not practically triggerable against a real external service on demand.
- **MCP separation, Tools** -- N/A, Phase 8 not built yet (next in this session's queue).
- **Data lifecycle** (conversation deletion, export) -- needs live testing of destructive operations and/or
  an organizational decision; `INamsClient` doesn't even expose a delete operation yet.
- **Payload: empty / max size / multi-message / non-text** -- Unicode was judged the highest-value single
  addition for this pass; the rest are reasonable follow-ups, not done here.

## 4. Verification

- `dotnet build AgentMemory.slnx -c Release` -- 0 warnings, 0 errors.
- `dotnet test tests/AgentMemory.Tests.Integration --filter "FullyQualifiedName~Nams.NamsLiveConnectivityTests"`
  -- **7/7 live, ~7s total** (3 original + 4 new).
- `dotnet test tests/AgentMemory.Tests.Unit` -- full suite green, unaffected (no unit-level changes this
  phase).

## 5. Definition of done

- [x] 4 new live scenarios added and passing against the real NAMS SaaS.
- [x] Coverage overlap and deferred scope explicitly documented (no silent gaps).
- [ ] Self-reviewed, PR opened, CI green (live tests report Skipped there, as established), merged to `main`.
