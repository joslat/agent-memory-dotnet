# NAMS Phase 3 — Identity and Conversation Mapping: Planning and Implementation Plan

**Prepared:** 2026-07-17
**Branch:** `nams/phase3-identity-conversation-mapping`
**Purpose:** Continues the NAMS backend effort after Phase 0 (baseline freeze, #129), Phase 1 (package
skeleton, #130), and Phase 2 (low-level client adapter, #131). Executes "Phase 3: Identity and conversation
mapping" from `strategy/NAMS/AgentMemory_NAMS_Backend_Engineering_Plan_V04.md` (§7).

---

## 1. Task and scope

Guarantee that a given (application, user, session, local-conversation) identity always maps to the same
NAMS conversation — creating one on first use, reusing it afterward, and refusing to let one tenant's
identity resolve to another's conversation. **Stays entirely inside `AgentMemory.Nams`** — confirmed against
§5.3's "Desired package structure" table, which explicitly lists "conversation resolution" under
`AgentMemory.Nams/`, not `AgentMemory.AgentFramework.Nams/` (that package, per ADR-9, is Stage-1 MAF/NAMS
*turn and context mapping* — Phase 6, not yet started). No MAF/AgentSession types are referenced here.

| Plan requirement | Plan text (§7 Phase 3) | This implementation |
|---|---|---|
| Planned contracts | `INamsConversationResolver`, `INamsConversationStateStore`, `NamsConversationIdentity`, `NamsConversationResolutionResult` | Same, plus `NamsConversationStateKeys` (the versioned state-bag key constant), `NamsConversationIdentityConflictException` (the plan doesn't name an exception type for the "rejects stale mapping" security invariant — introduced to make that failure mode a distinct, catchable type rather than a generic `InvalidOperationException`), and an internal `KeyedAsyncLock` (the process-local creation-lock primitive the resolution algorithm calls for but doesn't name) |
| Required data (`NamsConversationIdentity`) | Exact record shown in §7 | Same, verbatim |
| State key | `AgentMemory.Nams.v1.ConversationId`, versioned/namespaced, not generic | Defined as `NamsConversationStateKeys.ConversationId` — a constant for whatever session-state-backed `INamsConversationStateStore` a **future** Phase 6 builds (e.g. backed by MAF's `AgentSession` state bag). This phase's own default store (below) doesn't need it internally — it has its own purpose-built index — but the constant is defined now so Phase 6 doesn't have to invent it later. |
| "Read NAMS conversation ID from serialized AgentSession state" (resolution algorithm step 2) | Implies a MAF-specific persistence detail | **Deliberately abstracted away.** `INamsConversationStateStore` is the framework-agnostic persistence seam; a MAF-backed implementation using `NamsConversationStateKeys.ConversationId` against `AgentSession` state is Phase 6's job, consuming this phase's public contracts from a separate package. This phase ships `InMemoryNamsConversationStateStore` as the default, host-usable-for-single-instance-deployments implementation. |
| Cross-process duplicate-creation guarantee | "Production multi-instance support requires NAMS-side idempotency or a shared state store with atomic compare-and-set semantics. ... must not claim exactly-once conversation creation unless supported by the service contract." | **Explicitly not claimed.** `InMemoryNamsConversationStateStore` is single-process only (documented in its own XML doc and in this plan's exit criteria). A horizontally-scaled host **must** supply its own durable `INamsConversationStateStore` (registered before calling `AddNamsAgentMemory`, so `TryAddSingleton` doesn't overwrite it) with real atomic compare-and-set semantics. `NamsConversationResolver`'s own process-local `KeyedAsyncLock` only prevents same-process duplicate creation — it is explicitly documented as providing no cross-process guarantee, matching the plan's own warning almost verbatim. |
| Strict multi-tenant "fails before request if user identity absent" | References the plan's own strict-multi-tenant concept | `AgentMemory.Nams` cannot reference `AgentMemory.Core`'s `IMemoryIsolationPolicy` (B9 — zero sibling references). Implemented instead as an unconditional requirement: `NamsConversationIdentity`'s four key fields are all non-nullable `required string`, and `NamsConversationResolver.ResolveAsync` additionally rejects blank/whitespace values at runtime with an `ArgumentException` — functionally equivalent fail-closed behavior without needing to know about Core's isolation-mode enum. Whether to call this resolver at all under a given isolation mode is a **host** decision (Phase 6's concern), not this package's. |

### Explicitly out of scope for this phase

- Any MAF/`AgentSession` reference — that's Phase 6 (`AgentMemory.AgentFramework.Nams`, a separate package
  per ADR-9).
- A production-grade, cross-process-safe `INamsConversationStateStore` implementation (e.g. Neo4j-backed,
  Redis-backed) — `AgentMemory.Nams` has zero sibling-package references by design (B9), so it cannot ship
  one backed by this repo's own Neo4j persistence layer; a host wanting cross-process safety supplies its own.
- Recall/context mapping and post-turn persistence — Phases 4 and 5.

## 2. Detailed design

### Public vs. internal surface

Unlike Phase 2's `INamsClient` (kept `internal` since nothing outside the package consumes it yet),
`INamsConversationStateStore` is an explicit **host/Phase-6 extension point** — the plan itself says a
production host must supply its own implementation. Extension points a separate package (Phase 6) needs to
implement or consume must be `public`, not reached via `InternalsVisibleTo`. Public surface for this phase:
`NamsConversationIdentity`, `NamsConversationResolutionResult`, `INamsConversationStateStore`,
`InMemoryNamsConversationStateStore`, `NamsConversationStateKeys`, `INamsConversationResolver`,
`NamsConversationIdentityConflictException`. Kept internal: `NamsConversationResolver` (the concrete
implementation — consumed via DI through the public interface, matching `Neo4jNamsClientAdapter`'s pattern)
and `KeyedAsyncLock` (a pure implementation detail). `AgentMemory.Nams` has never been published to NuGet
(confirmed — no version tag exists since it was added in Phase 1), so none of this is SemVer-constrained yet.

### `INamsConversationStateStore`

```csharp
public interface INamsConversationStateStore
{
    Task<NamsConversationIdentity?> TryGetAsync(
        string applicationId, string userId, string sessionId, string localConversationId,
        CancellationToken cancellationToken);

    /// Atomically records identity (NamsConversationId populated) iff no mapping already exists for the same
    /// key. Returns false if a concurrent writer won the race.
    Task<bool> TryCreateAsync(NamsConversationIdentity identity, CancellationToken cancellationToken);
}
```

Lookups take the four raw key components (not a `NamsConversationIdentity` with a meaningless
`NamsConversationId` field) to avoid the ambiguity of "looking up an identity by a field that's the answer
being looked up." `TryCreateAsync` takes a fully-populated identity to persist.

### `InMemoryNamsConversationStateStore`

A `ConcurrentDictionary<string, NamsConversationIdentity>` keyed by
`applicationId\0userId\0sessionId\0localConversationId`. `TryCreateAsync` uses `ConcurrentDictionary.TryAdd`
— atomic within one process. Documented as single-process-only in its own XML doc, matching the plan's
explicit warning.

### `NamsConversationResolver` (resolution algorithm)

Implements the plan's 5-step algorithm exactly: validate → read → (if absent) acquire process-local
per-key lock → re-check under lock → create via `INamsClient.CreateConversationAsync` with correlation
metadata → atomically persist → on a lost race, re-read and reconcile onto the winning mapping rather than
returning an orphaned conversation ID. A found-but-blank `NamsConversationId` on a read (the "invalid saved
ID" case) is treated as absent, not returned. A found mapping bound to a different user or application throws
`NamsConversationIdentityConflictException` rather than silently reusing it.

Correlation metadata matches the plan's suggested JSON exactly (`agentMemoryApplicationId`,
`agentMemorySessionId`, `agentMemoryConversationId`, `integration`, `integrationVersion` — the last read from
the assembly's own version, not hardcoded).

### `KeyedAsyncLock`

A `ConcurrentDictionary<string, SemaphoreSlim>`-backed per-key async lock, scoped as a *private instance
field* of `NamsConversationResolver` (not static/shared across resolver instances) — this is what makes it
possible to test the cross-process reconciliation path in-process: two separate `NamsConversationResolver`
instances have independent locks but can share one `INamsConversationStateStore`, simulating two independent
processes racing against a shared durable store. Known, accepted limitation (not a defect): semaphore entries
are never evicted, so long-lived processes accumulate one per distinct identity ever seen — acceptable for a
"process-local optimization" per the plan's own framing, not a hardened production primitive; revisit only if
real usage shows it matters (ADR-4's "don't generalize before observing real behavior" applies here too).

## 3. Tests

Per the plan's own Phase 3 test list:

| Plan requirement | Test |
|---|---|
| First call creates one conversation | `FirstCall_CreatesOneConversation` |
| Repeated call reuses it | `RepeatedCall_ReusesExistingMapping_DoesNotCreateAgain` |
| Parallel calls create one conversation | `ParallelCalls_SameResolverInstance_CreateOnlyOneConversation` |
| Restored session reuses it | `RestoredSession_SecondResolverInstance_ReusesMapping` |
| Changed user rejects stale mapping | `ChangedUser_ThrowsIdentityConflictException` |
| Changed application rejects stale mapping | `ChangedApplication_ThrowsIdentityConflictException` |
| Missing identity fails closed | `MissingIdentityField_ThrowsArgumentException` (theory over all 4 required fields) |
| Creation failure leaves state unchanged | `CreationFailure_LeavesStateUnchanged` |
| Cancellation leaves state unchanged | `Cancellation_PropagatesAndLeavesStateUnchanged` |
| Invalid saved ID is detected | `BlankSavedConversationId_IsDetected_ThrowsInsteadOfSilentlyProceeding` (see §5 — detected means "fails loudly", not "silently proceeds") |
| Metadata contains expected correlation values only | `CorrelationMetadata_ContainsExpectedKeysAndValuesOnly` |
| No cross-user leakage under high concurrency | `NoCrossUserLeakage_ConcurrentDifferentUsersSameSession_OnlyOneSucceedsRestRejected` |
| Two independent resolver instances using the same durable store converge on one mapping | `TwoIndependentResolvers_SharedStore_ConvergeOnOneMapping` |
| Process-restart-between-creation-and-persistence reconciliation | Covered by the same test above (a fresh resolver instance with an independent lock, racing against a shared store, *is* the crash-window scenario — documented in that test's comment rather than duplicated) |

Plus `InMemoryNamsConversationStateStoreTests` (absent-key lookup, first-write-wins, concurrent-writes
atomicity) and `KeyedAsyncLockTests` (different keys don't block each other, same key serializes, disposal
releases). DI wiring tests added to `NamsServiceCollectionExtensionsTests.cs`: default store/resolver
resolve, and a host-registered store (registered before `AddNamsAgentMemory`) takes precedence over the
default.

## 4. Definition of done

- [x] `src/AgentMemory.Nams/Identity/` populated per §2, builds clean (0 warnings) on net8.0/net9.0/net10.0.
- [x] `PackageBoundaryGuardTests`'s B9 rule still passes unmodified (no new package/sibling references).
- [x] All plan-mandated tests pass (28 new tests: 4 in-memory-store, 4 keyed-lock, 13 resolver, 2 new DI wiring
  — plus 5 store/resolver-key tests beyond the plan's own list, added to cover a design mistake found and
  fixed during implementation, see §5).
- [x] Full existing unit/SK/integration suites remain green: **3135 unit (+28) / 54 SK unit / 308
  live-Neo4j integration**, 0 build warnings.
- [x] Self-reviewed via parallel finder agents.
- [ ] PR opened, CI green, merged.

## 5. A design mistake found (and fixed) by my own tests, before self-review

While writing the plan-mandated "changed user/application rejects stale mapping" tests, they **failed
outright** — not because of an assertion bug, but because my first implementation's `INamsConversationStateStore`
lookup key was `(ApplicationId, UserId, SessionId, LocalConversationId)`. With `UserId`/`ApplicationId` baked
into the key, two different users (or applications) sharing the same session/local-conversation ID could
never actually collide — each just got its own independent row, silently, with no conflict ever detected.
That directly contradicts the plan's own test list, which explicitly wants a *different* user or application
targeting the same session/local-conversation to be **rejected**, not quietly given its own separate mapping.

Fixed by re-deriving the key from the plan's own wording rather than from my first instinct: a
session/local-conversation slot is *one* logical binding (`(SessionId, LocalConversationId)` only); the
mapping's stored `UserId`/`ApplicationId` are *data* the resolver validates against the requester, not
independent key axes. This also surfaced a second, related bug: a store entry with a blank/corrupted
`NamsConversationId` couldn't be "healed" by falling through to a fresh creation attempt, since the
add-if-absent store contract would just collide on the same occupied key again — fixed by detecting that
case immediately and failing loudly (`InvalidOperationException`) instead of attempting a doomed creation.

Both fixes are captured in `INamsConversationStateStore`'s own XML doc (which explicitly notes the
assumption that `SessionId` is unique across applications sharing one store instance — a real, documented
trade-off, not an oversight) and in `NamsConversationResolver.TryResolveExisting`'s doc comment. Test names
were corrected to match actual behavior (e.g. `BlankSavedConversationId_IsDetected_ThrowsInsteadOfSilentlyProceeding`
rather than a "treated as absent, creates new" name that no longer matched what the code does).

## 6. Self-review findings and dispositions

Ran 3 parallel finder agents (correctness / cross-file impact / cleanup-conventions) against the staged diff.

- **Real doc bug, fixed (found independently by 2 of the 3 reviewers):** `INamsConversationStateStore`'s
  class-level doc and `TryCreateAsync`'s own doc both still said the store's key was "(application, user,
  session, local-conversation)" — leftover text from before the mid-implementation key redesign (§5), and
  directly contradicting `TryGetAsync`'s own doc comment right next to it. This is the more dangerous kind of
  staleness since `INamsConversationStateStore` is the public host/Phase-6 extension point — a future
  implementer skimming the wrong doc could rebuild the exact bug this phase's own tests already caught. Both
  reworded to `(session, local-conversation)`. Same fix applied to `NamsConversationResolver`'s class doc
  ("per-(application, session, local-conversation) creation lock" → "per-(session, local-conversation)").
- **Doc-hygiene, fixed:** this planning doc's own §3 test-mapping table still listed 2 pre-fix test names
  (`BlankSavedConversationId_TreatedAsAbsent_CreatesNew`, `NoCrossUserLeakage_DifferentUsersGetDistinctConversations`)
  that no longer matched the actual test methods after §5's fix — even though §5 itself already used the
  correct names. Updated both rows.
- **Doc-hygiene, fixed:** `docs/architecture.md`'s B9 verification bullet and `docs/specification.md`'s
  package note both stopped at Phase 2 and didn't disclose Phase 3's new public extension points
  (`INamsConversationStateStore`/`INamsConversationResolver`). Added a clause to both.
- **Cleanup, applied:** removed two unused `using` directives (`Microsoft.Extensions.DependencyInjection`,
  `Microsoft.Extensions.Options`) from `NamsConversationResolverTests.cs` — leftover from an earlier draft
  that constructed the resolver through DI before switching to direct `new(...)` construction.
- **Assessed, not changed:** the raw `InvalidOperationException` for the "should never happen" corrupted-store
  case (vs. the dedicated `NamsConversationIdentityConflictException` for the expected tenant-conflict case)
  is a deliberate, reasonable distinction between an invariant violation and a catchable business-rule
  exception — kept as-is.
- **Assessed, not changed:** the `Task.Delay(50)`-based synchronization in `KeyedAsyncLockTests.SameKey_SerializesAccess`
  and `NamsConversationResolverTests.TwoIndependentResolvers_SharedStore_ConvergeOnOneMapping` is timing-based
  rather than signal-based, which under extreme CI load could theoretically let a test exercise a sequential
  path instead of the intended race without causing a false failure. Both reviewers who found this rated it
  non-urgent (tests pass reliably); left as-is, noted here for visibility rather than fixed now.
- **Verified clean by both correctness and cross-file reviewers:** the double-checked-locking race logic
  (including the cross-tenant reconciliation path), `KeyedAsyncLock`'s exception-safety (semaphore always
  released via `using`, even when `NamsConversationIdentityConflictException`/`InvalidOperationException`
  propagates from inside the locked region), `ConcurrentDictionary.TryAdd`'s atomicity, cancellation leaving
  zero state written in every code path, correlation-metadata containing no secrets/extra PII, DI lifetimes
  (both `INamsConversationStateStore` and `INamsConversationResolver` correctly `Singleton` — a
  Scoped/Transient registration would silently defeat the per-instance `KeyedAsyncLock`), package boundary
  (B9 still 6/6, zero new sibling/framework references), and `dotnet pack` still succeeding cleanly.

Final counts after fixes: 3135 unit (unchanged — these were doc/using fixes, no test behavior changed) / 54
Semantic Kernel unit, 0 build warnings across the whole solution.
