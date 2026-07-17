# NAMS Phase 10c — Multi-Instance Mapping Live Test — Planning and Implementation Plan

**Prepared:** 2026-07-17
**Branch:** `nams/phase10c-multi-instance-mapping`
**Purpose:** Third of five follow-up Phase 10 increments (10a-10d + TCK Platinum research).

## 1. Why the earlier concurrency test didn't cover this

The Phase 10 first-pass expansion (PR #139) added
`ResolveAsync_SameIdentityResolvedConcurrently_ReconcilesToOneConversation` against a single
`NamsConversationResolver` instance. Tracing `NamsConversationResolver`'s own algorithm shows that test can only
prove **in-process lock serialization**: one resolver instance owns one `KeyedAsyncLock`, so a second concurrent
`ResolveAsync` call for the same identity blocks on the lock until the first call finishes and commits its mapping
to the shared store -- the second caller then always resolves via the **outer** `TryResolveExisting` check, before
ever acquiring the lock. It can never reach the resolver's own "lost the atomic write -- reconcile onto the
winner's mapping" branch, which only exists to handle a **cross-process** race the resolver's own in-process lock
cannot prevent.

That branch is real production logic (it fires whenever two separate host processes/instances race to create the
same conversation against a shared durable state store) and was untested until now.

## 2. Design: simulating two processes within one test process

Two independent `NamsConversationResolver` instances, each constructed with its own private `KeyedAsyncLock` (an
instance field, not shared), but both given the **same** `InMemoryNamsConversationStateStore` instance -- the one
thing two real processes sharing a durable store would actually share. Racing `ResolveAsync` calls on these two
instances therefore cannot be serialized against each other by either instance's own lock; only the shared store's
atomic `ConcurrentDictionary.TryAdd` inside `TryCreateAsync` can pick a winner.

The remaining question was whether the race is actually reachable deterministically, or whether it would be a
flaky "sometimes reaches it, sometimes doesn't" test depending on scheduling. Reasoning through
`NamsConversationResolver.ResolveAsync`'s control flow: every step before the real `_namsClient.CreateConversationAsync`
call resolves against the in-memory store via `Task.FromResult` -- an already-completed task, whose `await` does not
yield control back to the caller. That means both resolver instances race through their outer read, lock
acquisition (on their own independent locks, so neither blocks), and inner read, all synchronously, and **both**
reach their own real (network-bound, genuinely-suspending) `CreateConversationAsync` call before either can have
observed the other's write. This makes the race deterministic rather than a scheduling gamble: both calls always
create their own NAMS-side conversation, and the shared store's `TryAdd` always picks exactly one winner when both
attempt to commit.

This deliberately produces one orphaned NAMS-side conversation per race -- documented in `NamsConversationResolver`
as the accepted "residual duplicate-conversation risk" (the plan does not claim an exactly-once guarantee NAMS
itself doesn't provide), not something this test works around.

## 3. What was added

New file `tests/AgentMemory.Tests.Integration/Nams/NamsMultiInstanceMappingTests.cs` (kept separate from
`NamsLiveConnectivityTests.cs`, whose own doc comment says its tests are "exercised entirely through the public
surface" -- these tests construct the internal `NamsConversationResolver`/`InMemoryNamsConversationStateStore` types
directly, a structurally different scenario deserving its own file):

- `TwoResolverInstancesSharingOneStore_RaceForSameIdentity_ReconcileToOneWinner` -- races two resolver instances for
  one identity against the real NAMS SaaS; asserts both results agree on the same `NamsConversationId` and exactly
  one has `WasCreated == true`.
- `TwoResolverInstancesSharingOneStore_RaceWithDifferentUsersOnTheSameSessionSlot_LoserThrowsConflict` -- a
  cross-tenant negative case: two identities share the same `SessionId`/`LocalConversationId` (the store's actual
  lookup key, which deliberately excludes `UserId`/`ApplicationId`) but differ in `UserId`. Asserts exactly one
  resolution throws `NamsConversationIdentityConflictException` (the loser, on finding a mapping bound to the other
  tenant) and the other completes normally -- proving the reconciliation path enforces the tenant check rather than
  silently handing one tenant's turn to another tenant's conversation.

No production code changed -- both tests exercise existing `NamsConversationResolver`/
`InMemoryNamsConversationStateStore` logic that had no prior direct-construction test coverage. No new
`InternalsVisibleTo` grant needed (Phase 10b already added the `AgentMemory.Tests.Integration` grant these tests
rely on).

## 4. Self-review findings and fixes

2 parallel reviewers (correctness/test-soundness, cross-file/conventions).

The correctness reviewer found no issues -- confirmed the race design is genuinely deterministic (not a scheduling
gamble) and every assertion proves what its test name claims.

The conventions reviewer found 3 low-severity/cosmetic items, all fixed:

- **Duplication**: `new NamsConversationResolver(...)` was constructed inline 4 times across the two tests, where
  `NamsConversationResolverTests.cs`'s own `CreateResolver` helper pattern was available to mirror. Fixed by adding
  an equivalent local `CreateResolver` helper to this file.
- **Dropped tracing context**: this file's own `UniqueIdentity()` didn't embed the calling test's name in `UserId`
  the way `NamsLiveConnectivityTests.UniqueIdentity`'s `[CallerMemberName]` parameter does -- more relevant here
  than in the sibling file, since these tests deliberately leave orphaned NAMS-side conversations behind. Fixed by
  matching the sibling's `[CallerMemberName]` convention.
- **Doc imprecision**: this doc's own cleanup note said the positive test creates "at least one" orphaned
  conversation, understating what §2's own reasoning already proves (both racing calls always independently reach
  `CreateConversationAsync`, so it's always exactly two, not "at least one"). Fixed the wording.

Points specifically checked by the correctness reviewer and confirmed non-issues:

- `Task.WhenAll` in the first test surfaces only an aggregate/first exception if either task faults -- reviewed to
  confirm neither racing call in the *positive* test is expected to throw (only the negative test's calls can),
  so the simpler `Task.WhenAll` idiom is correct there; the negative test deliberately awaits each task individually
  instead, since it must observe one success and one specific exception rather than let a fault from one hide the
  other's outcome.
- `NamsConversationIdentity` is a positional record (`with` expression used in the negative test to vary only
  `UserId`) -- confirmed this produces a genuinely distinct identity object with an unrelated reference identity,
  not an aliasing hazard given both resolver calls run concurrently against it.
- Cleanup: neither test deletes the NAMS-side conversations it creates -- per this doc's own §2 reasoning, both
  racing calls always independently reach `CreateConversationAsync` before either commits, so each test always
  creates exactly two NAMS-side conversations (one is always orphaned once the shared store picks a winner).
  Consistent with every other live test in this suite -- none clean up NAMS-side state (a live-account hygiene
  concern already accepted plan-wide, not something to solve ad hoc in this one file).

## 5. Verification

- `dotnet build tests/AgentMemory.Tests.Integration -c Debug` -- clean.
- `dotnet test tests/AgentMemory.Tests.Integration --filter "...NamsMultiInstanceMappingTests"` -- **2/2 live**,
  first try, against the real NAMS SaaS.
- `dotnet build AgentMemory.slnx -c Release` -- 0 warnings, 0 errors.
- `dotnet test tests/AgentMemory.Tests.Unit -c Release` -- full suite green, 3262/3262 (no new unit tests -- this
  phase is integration-only).
- `dotnet test tests/AgentMemory.Tests.Integration --filter "FullyQualifiedName~Nams"` -- **15/15 live** (13
  previous + 2 new), against the real NAMS SaaS.

## 6. Definition of done

- [x] Two new live tests proving the cross-process reconciliation branch (positive + cross-tenant negative case).
- [x] Full unit + live suites green.
- [x] Self-reviewed; 3 low-severity/cosmetic findings from the conventions reviewer, all fixed.
- [ ] PR opened, CI green, merged to `main`.
