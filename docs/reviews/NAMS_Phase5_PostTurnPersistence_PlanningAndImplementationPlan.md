# NAMS Phase 5 — Post-Turn Persistence: Planning and Implementation Plan

**Prepared:** 2026-07-17
**Branch:** `nams/phase5-post-turn-persistence`
**Purpose:** Continues the NAMS backend effort after Phase 0 (#129), Phase 1 (#130), Phase 2 (#131), Phase 3
(#132), and Phase 4 (#133). Executes "Phase 5: NAMS post-turn persistence" from
`strategy/NAMS/AgentMemory_NAMS_Backend_Engineering_Plan_V04.md` (§7).

---

## 1. Scope split (same B9 analysis as Phase 4)

Several of Phase 5's plan items reference MAF-level or direct-backend-level concepts `AgentMemory.Nams`
structurally cannot touch (B9 — zero sibling-package references):

| Plan item | Where the referenced type/concept actually lives | This phase's scope |
|---|---|---|
| Single-writer enforcement across `NamsMemoryContextProvider`/a NAMS-backed `ChatHistoryProvider`/custom middleware | `ChatHistoryProvider`, `AIContextProvider` — MAF/`AgentMemory.AgentFramework` concepts; none of these NAMS-specific components exist until Phase 6 | This phase guarantees one thing only: one call to `PersistTurnAsync` submits exactly one physical bulk-write request (no internal retry-splitting). *Which* MAF component is allowed to call it at all is Phase 6's registration/validation job, once those components exist |
| `AgentFrameworkOptions.AutoExtractOnPersist` must not trigger local extraction when NAMS is selected | `AgentFrameworkOptions` lives in `AgentMemory.Core`/`AgentMemory.AgentFramework` | Not referenceable here at all — deferred entirely to Phase 6's documentation/wiring |
| "Optionally schedule or expose extraction-status observation" | Would need an extraction-status method on `INamsClient` | Doesn't exist — Phase 2 only implemented 4 confirmed operations. Documented as a known gap, not silently dropped, same treatment as Phase 4's `Reasoning` category gap |

**What stays fully in scope, no MAF/Core reference needed:** submitting the ordered bulk write itself,
classifying the outcome (including the ambiguous-timeout case), and applying `NamsPersistenceFailureMode`
(already a `NamsOptions` field since Phase 1, unused until now) to decide whether a failure escalates to an
exception.

## 2. A design simplification found while modeling the input shape

The plan's "Content policy" list (text persisted by default; system prompts excluded; tool calls go through
separate reasoning endpoints; no secrets in metadata) reads like something this layer must *filter for*. But
`AgentMemory.Nams` has no concept of "system prompt" vs. "user turn" vs. "tool result" — those are MAF
`ChatMessage`-level distinctions Phase 6 will actually see. Rather than giving `NamsMessageToPersist` a
`Role` string field a caller could get wrong (accidentally passing `"system"` through), the API takes
**two separate parameter lists** — `userMessages` and `assistantMessages` — and the service itself assigns
the wire role from which parameter a message appears in. There is structurally no way to submit a
system/tool-role message through this method at all; a caller who wants that persisted has to go through the
confirmed `POST /reasoning/tool-calls`/`POST /reasoning/steps` endpoints instead (not yet implemented on
`INamsClient` — same gap as above), never through this path. This satisfies "system prompts are not
persisted by default" and "only allowed message content" by construction rather than by a filter that could
be bypassed by a caller passing the wrong string.

**Correlation metadata** (plan step 5 of the algorithm): the confirmed `addMessageRequest` wire schema
(`content`/`role` only, per the pinned OpenAPI snapshot Phase 2 built against) has no per-message metadata
field. Correlation metadata is already attached once, at conversation-*creation* time
(`NamsConversationResolver.BuildCorrelationMetadata`, Phase 3) — there is nothing further to attach per
bulk-message call, and inventing a client-side-only metadata field the server would silently ignore isn't
useful. Documenting this rather than silently skipping the plan's step 5.

## 3. Detailed design

### `Persistence/` (new folder under `src/AgentMemory.Nams/`)

- **`NamsMessageToPersist`** (public record) — `Content` only. No `Role` field (§2).
- **`NamsPersistenceOutcome`** (public enum) — `Persisted`, `Failed`, `UnknownWriteOutcome`. The three-way
  split (not a bool) is the plan's own explicit requirement: "must report `UnknownWriteOutcome`, not
  blindly retry" after an ambiguous timeout — a network/timeout failure is genuinely different from a
  definitive 4xx/5xx rejection, since the server may or may not have processed the write before the response
  was lost.
- **`NamsPersistenceResult`** (public record) — `Outcome`, `PersistedMessageIds` (empty unless `Persisted`),
  `FailureReason` (nullable, sanitized — populated for `Failed`/`UnknownWriteOutcome`).
- **`NamsPersistenceFailedException`** (public) — thrown only when `NamsOptions.PersistenceFailureMode` is
  `FailInvocation` and the outcome is `Failed`/`UnknownWriteOutcome`; carries the `NamsPersistenceResult`.
  Phase 6 calling this *after* the model response was already produced is what gives the plan's ordering
  requirement ("propagated after the model response") for free — this phase doesn't need to know anything
  about "when" that happened.
- **`INamsPersistenceService`** (public interface) — `PersistTurnAsync(namsConversationId, userMessages,
  assistantMessages, cancellationToken)`. Takes an *already-resolved* conversation ID (Phase 3's job) — this
  phase doesn't resolve conversations itself, matching Phase 4's same pattern.
- **`NamsPersistenceService`** (internal) — builds one ordered list (all `userMessages` first, role
  `"user"`; then all `assistantMessages`, role `"assistant"`) as `Domain.NamsMessageInput`s (Phase 2's
  existing wire-DTO — reused, not duplicated), submits **one** `INamsClient.AddMessagesAsync` call (no
  retry — matches Phase 2's own conservative stance on non-idempotent writes), classifies the result:

| Condition | Outcome | Propagates as exception? |
|---|---|---|
| Both message lists empty | `Persisted`, empty ID list | No — nothing to persist is trivially done |
| `AddMessagesAsync` succeeds | `Persisted`, accepted IDs | No |
| `NamsOperationException`, `FailureKind` `Authentication`/`Authorization` | — | **Yes, always** — identity/security violations propagate unconditionally, matching Phases 3/4 |
| `NamsOperationException`, `FailureKind` `Network`/`Timeout` | `UnknownWriteOutcome` | Only if `PersistenceFailureMode == FailInvocation` |
| Any other `NamsOperationException` (Validation/NotFound/RateLimited/ServerError) | `Failed` | Only if `PersistenceFailureMode == FailInvocation` |
| `OperationCanceledException` (caller token) | — | **Yes, always** |

### Delivery guarantee (documented per the plan's explicit "don't say 'exactly once' informally" requirement)

Without confirmed NAMS-side idempotency (`strategy/NAMS/Neo4j_Questions.md` #15, unanswered) and without a
durable outbox, this phase's persistence is **best-effort and at-most-once from the client's perspective** —
a single submission attempt, no automatic retry, with an explicit `UnknownWriteOutcome` on ambiguous
network/timeout failures rather than a false "it definitely failed" or "it definitely succeeded" claim. A
durable outbox or retry-with-idempotency-key is future work gated on Neo4j confirming #15 — not something
this phase fakes.

### Explicitly out of scope for this phase

- Filtering/deciding *what* counts as excluded system-prompt/binary/secret content — that requires MAF
  `ChatMessage`-level knowledge Phase 6 has and this package doesn't; this phase only guarantees the
  structural impossibility of passing a non-user/assistant role through its own API (§2).
- Single-writer *registration* validation across MAF components — Phase 6.
- `AgentFrameworkOptions.AutoExtractOnPersist` interaction — Phase 6, different package.
- Extraction-status observation — no `INamsClient` method exists for it yet.
- Automatic retry of any kind — matches Phase 2's existing non-retry-for-writes stance; not revisited until
  Neo4j confirms idempotency support.

## 4. Tests

| Plan requirement | Test coverage |
|---|---|
| Failed invocation does not persist | N/A at this layer — "invocation failed, skip persistence" is a Phase 6 orchestration decision (don't call this method at all); nothing to test here |
| Complete successful turn persists in correct order | User messages submitted before assistant messages, correct role assigned to each |
| Only text messages persisted by default / system prompt excluded | Structural — no test needed beyond confirming the API has no way to pass a system-role message (a compile-time property, not a runtime one) |
| Cancellation behavior defined | Propagates from the client call |
| Timeout after send yields unknown outcome | `Network`/`Timeout` failure → `UnknownWriteOutcome`, not `Failed` |
| Unsafe write is not retried | `AddMessagesAsync` called exactly once regardless of outcome |
| Local extraction not invoked / extraction not awaited | Trivially true — no extraction-related call exists anywhere in this phase's code path |
| Persistence failure does not replace response in best-effort mode | `BestEffort` + `Failed`/`UnknownWriteOutcome` → result returned, no exception |
| Strict mode propagates | `FailInvocation` + `Failed`/`UnknownWriteOutcome` → `NamsPersistenceFailedException` thrown; `FailInvocation` + success → no exception |
| User/application metadata correct | N/A here — attached once at conversation creation (Phase 3), not per message (§2) |
| No secret leakage | `FailureReason` never contains the configured API key (mirrors Phase 2's exception-mapper redaction test) |
| Duplicate-writer validation works | Phase 6 — no MAF components exist yet to validate against |
| Safe idempotent retry produces no duplicate | Not applicable — this phase has no retry logic at all (matches Phase 2); revisit once NAMS idempotency is confirmed |

## 5. Definition of done

- [x] `src/AgentMemory.Nams/Persistence/` populated per §3, builds clean (0 warnings) on net8.0/net9.0/net10.0.
- [x] `PackageBoundaryGuardTests`'s B9 rule still passes unmodified (no new package/sibling references).
- [x] All plan-mandated (applicable) tests pass (18 new: 17 persistence-service, 1 DI wiring).
- [x] Full existing unit/SK/integration suites remain green: **3175 unit (+18) / 54 SK unit / 308
  live-Neo4j integration**, 0 build warnings.
- [x] Self-reviewed via parallel finder agents.
- [ ] PR opened, CI green (no Copilot review — out of credits), merged.

## 6. Self-review findings and dispositions

Ran 3 parallel finder agents (correctness / cross-file impact / cleanup-conventions) against the staged diff.

- **Doc-hygiene, fixed:** `docs/architecture.md`'s B9 verification bullet and `docs/specification.md`'s
  package note both stopped at Phase 4 and didn't disclose this phase's own `Persistence/` subsystem or
  that `NamsOptions.PersistenceFailureMode` (unused since Phase 1) is finally read here. Both updated.
- **Real cleanup, applied (rule of three):** the identity/security-failure predicate
  (`FailureKind is Authentication or Authorization`) had grown to **three** duplicate occurrences — twice in
  `Recall/NamsRecallService.cs` (Phase 4) and once in this phase's `Persistence/NamsPersistenceService.cs`.
  Extracted a shared `Domain.NamsFailureClassification.IsIdentitySecurityFailure(ex)`, used at all three call
  sites. The surrounding catch-clause bodies still differ per caller (only the one repeated condition moved).
- **Verified clean by all 3 reviewers:** exception-filter ordering (Authentication/Authorization,
  Network/Timeout, and catch-all are mutually exclusive by `FailureKind`, verified no overlap in either
  direction), the `FailInvocation` escalation firing on exactly `Failed`/`UnknownWriteOutcome` and never on
  `Persisted` (including the empty-input early-return path, which bypasses the check entirely), message
  ordering (`AddRange` + `Select` genuinely preserves user-before-assistant with correct role strings),
  cancellation (confirmed a non-caller `TaskCanceledException` is already translated into a classified
  `NamsOperationException` by Phase 2's `NamsClientExceptionMapper` before this layer ever sees it — no gap),
  DI lifetime consistency (`TryAddSingleton`, matching all three sibling services), package boundary (B9
  still 6/6, confirmed in code), `dotnet pack` still succeeding, and `PersistenceFailureMode`'s history
  (confirmed genuinely unread anywhere until this phase, exactly as Phase 1's own planning doc predicted).
- **Assessed, not changed:** `NamsMessageToPersist`'s single-field shape (kept as a dedicated record over a
  bare string list — cheap future extension point, matches this package's existing small-record convention);
  `NamsPersistenceFailedException`'s structured-result shape vs. Phase 3's plainer conflict exception
  (justified — this one exists specifically so callers can inspect `Outcome`/`FailureReason` programmatically).

Final counts after fixes: 3175 unit (unchanged — these were doc/refactor-only fixes) / 54 Semantic Kernel
unit, 0 build warnings across the whole solution.
