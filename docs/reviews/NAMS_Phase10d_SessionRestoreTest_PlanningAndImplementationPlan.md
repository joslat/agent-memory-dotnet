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

## 4. Verification

- `dotnet build tests/AgentMemory.Tests.Integration -c Debug` -- clean.
- `dotnet test tests/AgentMemory.Tests.Integration --filter "...NamsSessionRestoreTests"` -- **2/2 live**,
  first try (including the "without reapplying identity" test, confirming the empirical finding), against the
  real NAMS SaaS.
- `dotnet build AgentMemory.slnx -c Release` -- 0 warnings, 0 errors.
- `dotnet test tests/AgentMemory.Tests.Unit -c Release` -- full suite green, 3262/3262 (no new unit tests --
  this phase is integration-only; the fixture/csproj changes are purely additive and touch no unit-tested code).
- `dotnet test tests/AgentMemory.Tests.Integration --filter "FullyQualifiedName~Nams"` -- **17/17 live** (15
  previous + 2 new), against the real NAMS SaaS.

## 5. Definition of done

- [x] Two new live tests proving session identity (and the underlying NAMS conversation it maps to) survives
  `SerializeSessionAsync`/`DeserializeSessionAsync`, both with and without re-applying `.WithMemoryIdentity`.
- [x] Full unit + live suites green.
- [ ] Self-reviewed via parallel fork agents.
- [ ] PR opened, CI green, merged to `main`.
