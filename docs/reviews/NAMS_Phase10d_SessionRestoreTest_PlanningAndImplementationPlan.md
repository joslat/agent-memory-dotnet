# NAMS Phase 10d — Session-Restore Asserted Live Test — Planning and Implementation Plan

**Prepared:** 2026-07-18
**Branch:** `nams/phase10d-session-restore-test`
**Purpose:** Fourth of five follow-up Phase 10 increments (10a-10d + TCK Platinum research).

## 1. What this formalizes

`AgentMemory.Sample.NamsAgent`'s console demo shows session serialize/restore working (step 2 of its script:
`SerializeSessionAsync` → `DeserializeSessionAsync` → re-apply `.WithMemoryIdentity(...)` → the agent still
answers "what did I tell you?" correctly) -- but only as printed console output a human has to eyeball. No
asserted test exercised this. This phase builds a minimal, real (not scripted-stub) MAF-agent harness and
turns it into two asserted live tests.

## 2. Design decisions

**Real `ChatClientAgent`, stubbed model only.** Unlike `MemoryOwnerScopingAgentIntegrationTests`'
`ScriptedInnerAgent` (whose `SerializeSessionCoreAsync`/`DeserializeSessionCoreAsync` are trivial stubs
returning `default(JsonElement)`), this phase needs MAF's REAL session-serialization machinery, since that is
exactly what's under test. So: a real `ChatClientAgent` built via `stubChatClient.AsAIAgent(...)` (matching
the sample exactly), with only the model call stubbed via an NSubstitute `IChatClient` returning a canned
response -- no real LLM dependency, but genuine `SerializeSessionAsync`/`DeserializeSessionAsync` behavior.

**DI setup extended, not duplicated.** `NamsLiveFixture` previously only wired `AddNamsAgentMemory`. Added
`AddAgentMemoryFramework()` + `AddNamsAgentMemoryFramework()` (purely additive registrations -- new
`AgentFrameworkOptions`/`ContextFormatOptions` options plus a new scoped `NamsMemoryContextProvider` -- no
change to anything the other 15 NAMS live tests already resolve) rather than standing up a second, duplicate
DI container inside this new test file. Also added the `Microsoft.Agents.AI` package reference (the concrete
package with `ChatClientAgent`/`AsAIAgent`, vs. the `.Abstractions`-only reference the project already had)
and a project reference to `AgentMemory.AgentFramework.Nams`.

**Empirically probed MAF's real serialized-session JSON before writing any assertion** (per this whole Phase
10 push's discipline). A throwaway test dumped `(await agent.SerializeSessionAsync(session)).GetRawText()`
after calling `.WithMemoryIdentity(...)` and running one turn:

```
{"stateBag":{"user_id":"...","conversation_id":"...","InMemoryChatHistoryProvider":{"messages":[...]},
"application_id":"...","session_id":"..."}}
```

Two things this revealed, both shaping the final test design:

1. **The memory-identity state bag (`user_id`/`session_id`/`conversation_id`/`application_id`) is already
   embedded in `ChatClientAgent`'s own serialized JSON.** This means the sample's re-application of
   `.WithMemoryIdentity(...)` after `DeserializeSessionAsync` is likely defensive, not strictly load-bearing --
   a genuinely surprising, non-obvious finding worth its own test (see Test 2 below) rather than just assuming
   the sample's recipe is the only way it works.
2. **`ChatClientAgent` also carries its OWN turn history** (`InMemoryChatHistoryProvider`) inside the same
   state bag. This ruled out the originally-planned assertion strategy of inspecting the stub `IChatClient`'s
   received messages for the pre-restore marker text: a restored session's next turn would include that
   marker via MAF's own built-in history regardless of whether NAMS recall did anything, so finding it there
   would prove nothing about NAMS specifically. A `<recalled_memory category="...">`-wrapper search (the
   sample's own `MemoryTraceChatClient` approach) was considered and also ruled out: per
   `NamsMafTypeMapper.ToContextMessages`, the `RecentMessage`/`RelevantMessage` categories a plain persisted
   turn recalls under are deliberately left undelimited ("a recalled message renders as an individual
   conversation turn, not an injected block"), so a plain marker message carries no distinguishing wrapper to
   search for either.

**Final assertion strategy: direct, live NAMS reads outside the agent pipeline.** Each test resolves the
underlying `NamsConversationId` once (via `INamsConversationResolver`, from the same DI scope) purely to know
which conversation to poll -- not as the thing being proven, since a second direct resolver call with the
same identity values would trivially agree regardless of whether restore worked (the shared singleton
`INamsConversationStateStore` already remembers the mapping from the first call). The actual proof is
that content persisted through the RESTORED session's own real `agent.RunAsync(...)` call -- which internally
extracts identity from the restored session's own post-deserialize state and persists through it -- lands in
that SAME conversation, confirmed via a bounded `PollUntilAsync` against `INamsRecallService.RecallAsync`
called independently of the agent pipeline.

## 3. What was added

New file `tests/AgentMemory.Tests.Integration/Nams/NamsSessionRestoreTests.cs`:

- `SessionRestore_WithMemoryIdentityReapplied_ContinuesPersistingToTheSameNamsConversation` -- the documented
  recipe: run a turn, serialize, deserialize, re-apply `.WithMemoryIdentity(...)` (matching the sample
  exactly), run a second turn, and confirm (via direct NAMS recall) that it persisted to the same conversation
  the first turn did.
- `SessionRestore_WithoutReapplyingMemoryIdentity_StillPersistsToTheSameNamsConversation` -- identical, but
  deliberately skips re-applying `.WithMemoryIdentity(...)` after restore, proving the empirically-discovered
  finding above: the state bag survives `ChatClientAgent`'s own serialization on its own.

Both also assert `restored.Should().NotBeSameAs(session, ...)` -- a basic sanity check that this is a genuine
restore-from-JSON, not an accidentally-reused object reference.

Modified: `NamsLiveFixture.cs` (added the two AgentFramework registrations, see §2), and
`AgentMemory.Tests.Integration.csproj` (added the `Microsoft.Agents.AI` package reference and the
`AgentMemory.AgentFramework.Nams` project reference).

## 4. Self-review findings and fixes

2 parallel reviewers (correctness/test-soundness, cross-file/conventions).

**Correctness reviewer found one HIGH finding, fixed:** `SessionRestore_WithMemoryIdentityReapplied_...`
re-applied `.WithMemoryIdentity(...)` after restore using the SAME closed-over identity values the test had
already used before serialization. Since that call unconditionally overwrites the state bag, the second
turn's persistence target was fully determined by the test's own known values, not by anything the JSON
round-trip actually preserved -- reintroducing, one layer down (`WithMemoryIdentity → ExtractIds →
ResolveAsync`), exactly the "second direct resolver call with the same identity, which would trivially agree
regardless of whether restore worked" triviality this file's own doc comment says the design avoids. Only the
second test (which never re-applies) was actually non-vacuous as originally written. Fixed by reading the
restored session's identity back via the same public `GetMemoryIdentity()` extension the production code
itself uses, and asserting it already matches BEFORE re-applying `WithMemoryIdentity` -- this reads what
restore actually produced, independent of the known values, making the "state survived" claim genuine. The
re-apply step (matching the sample's recipe) still follows afterward, so the test now proves both things: the
state bag itself survived, and the documented recipe subsequently works end-to-end.

A second, lower-severity point (both pre- and post-restore turns share one `agent`/`ChatClientAgent`
instance, so a hypothetical bug that dropped only `SessionId`/`ConversationId` while `UserId` survived could
hide behind `ExtractIds`'s `agent.Id` fallback) was noted as a narrow residual seam, not required to fix: all
four state-bag keys share one read/write code path, making that scenario very unlikely, and the new explicit
`GetMemoryIdentity()` assertions in Test 1 already check all four fields independently.

**Conventions reviewer found 3 low/medium findings, all fixed:**

- **Missing orphan-tracing convention (Medium):** `UniqueIds()` didn't embed the calling test's name via
  `[CallerMemberName]` the way `NamsLiveConnectivityTests.UniqueIdentity`/`NamsMultiInstanceMappingTests.UniqueIdentity`
  do -- relevant here too, since these tests also never delete the NAMS-side conversations they create. Fixed
  by replacing `UniqueIds()` with a `UniqueIdentity([CallerMemberName] string testName = "")` returning a
  `NamsConversationIdentity` directly (see next point), matching the sibling convention exactly.
- **`PollUntilAsync` duplicated verbatim** from `NamsLiveConnectivityTests.cs`. Extracted into a new shared
  `NamsLiveTestHelpers.PollUntilAsync`; `NamsLiveConnectivityTests.cs` now forwards its own private
  `PollUntilAsync` to the shared implementation (kept as a thin wrapper rather than rewriting that file's ~8
  call sites, to keep this phase's blast radius on an already-merged, heavily-used file minimal).
- **Minor redundancy:** `UniqueIds()` returned a raw 4-string tuple, so both tests separately reconstructed a
  `NamsConversationIdentity` from it just to call the resolver. Fixed by having `UniqueIdentity()` return
  `NamsConversationIdentity` directly; both tests now read `.UserId`/`.SessionId`/`.LocalConversationId`/
  `.ApplicationId` off one object for both `WithMemoryIdentity` and `resolver.ResolveAsync`.

Re-verified after all fixes: 17/17 live tests still green (including both session-restore tests).

## 5. Verification

- `dotnet build tests/AgentMemory.Tests.Integration -c Debug` -- clean.
- `dotnet test tests/AgentMemory.Tests.Integration --filter "...NamsSessionRestoreTests"` -- **2/2 live**,
  first try (including the "without reapplying identity" test, confirming the empirical finding), against the
  real NAMS SaaS.
- `dotnet build AgentMemory.slnx -c Release` -- 0 warnings, 0 errors.
- `dotnet test tests/AgentMemory.Tests.Unit -c Release` -- full suite green, 3262/3262 (no new unit tests --
  this phase is integration-only; the fixture/csproj changes are purely additive and touch no unit-tested code).
- `dotnet test tests/AgentMemory.Tests.Integration --filter "FullyQualifiedName~Nams"` -- **17/17 live** (15
  previous + 2 new), against the real NAMS SaaS.

## 6. Definition of done

- [x] Two new live tests proving session identity (and the underlying NAMS conversation it maps to) survives
  `SerializeSessionAsync`/`DeserializeSessionAsync`, both with and without re-applying `.WithMemoryIdentity`.
- [x] Full unit + live suites green.
- [x] Self-reviewed via 2 parallel independent reviewers; 1 HIGH + 3 low/medium findings, all fixed.
- [ ] PR opened, CI green, merged to `main`.
