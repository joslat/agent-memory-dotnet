# NAMS Phase 4 — Recall and Context Mapping: Planning and Implementation Plan

**Prepared:** 2026-07-17
**Branch:** `nams/phase4-recall-context-mapping`
**Purpose:** Continues the NAMS backend effort after Phase 0 (#129), Phase 1 (#130), Phase 2 (#131), and
Phase 3 (#132). Executes "Phase 4: NAMS recall and context mapping" from
`strategy/NAMS/AgentMemory_NAMS_Backend_Engineering_Plan_V04.md` (§7).

---

## 1. The critical architectural constraint this phase runs into

Phase 4's plan text assumes reuse of `IAutomaticRecallPolicy`, `IMemoryContextAdmissionPolicy`, and
`MemoryTrustLevel` for escaping/delimiting/admission-gating recalled content. Checked where these actually
live before writing any code:

| Type | Actual location |
|---|---|
| `MemoryTrustLevel` | `AgentMemory.Abstractions` |
| `IAutomaticRecallPolicy` | `AgentMemory.AgentFramework` |
| `IMemoryContextAdmissionPolicy` | `AgentMemory.AgentFramework` |
| `RecalledMemoryDelimiter` | `AgentMemory.Core` |

`AgentMemory.Nams` has **zero** sibling-package references by design (B9 — `PackageBoundaryGuardTests`'
`AllowedInternalReferences: []` for this package, enforced since Phase 1). It cannot reference any of the
four types above — not even `AgentMemory.Abstractions`, which every *other* package in this repo is allowed
to reference. This isn't a gap to work around; it's the deliberate point of ADR-9 (isolate the Stage-1
MAF/NAMS adapter in its own package) and the reason §5.3's desired package structure splits "NAMS model
mapping" (inside `AgentMemory.Nams`) from "NAMS/MAF turn and context mapping" (inside the not-yet-built
`AgentMemory.AgentFramework.Nams`, Phase 6 — which *will* be allowed to reference `AgentMemory.Core`/
`AgentMemory.AgentFramework` for these exact shared security primitives, the same way the existing
`AgentMemory.AgentFramework` package already does for the direct backend).

**Scope decision:** this phase builds the "NAMS model mapping" half only — raw NAMS API responses mapped
into a neutral, backend-owned internal shape, with a *locally-defined* trust-level mirror and zero escaping/
delimiting/admission logic. Phase 6 is where that neutral shape gets escaped, delimited, admission-checked,
and actually inserted into a MAF `AIContext` — this phase produces its *input*, not the final safe-to-render
content. This is loudly documented on the result type itself (§3) so nobody mistakes it for something safe
to hand to a model directly.

## 2. Task and scope

Retrieve NAMS-hosted memory (reflections, observations, recent messages, entities) for a resolved
conversation and map it into a neutral internal shape, ready for a future Phase 6 to escape/delimit/gate and
insert into a `ChatClientAgent`'s context.

| Plan requirement | Plan text (§7 Phase 4) | This implementation |
|---|---|---|
| Baseline behavior | `GetContextAsync(namsConversationId)`, map reflections/observations/recent messages | Same, via Phase 2's `INamsClient.GetContextAsync` |
| Query-specific retrieval | Search messages using current turn; search entities using current turn | Entity search only — `SearchMessagesAsync` doesn't exist on `INamsClient` (Phase 2 dropped it, unconfirmed REST mapping). "Relevant messages" degrades to reusing recent messages, exactly as the plan's own recall-policy-integration table anticipates for this case |
| Recall policy integration | "Reuse `IAutomaticRecallPolicy`" | **Cannot** — that type lives in `AgentMemory.AgentFramework` (§1). Replaced with a minimal, package-local `NamsRecallOptions` (which categories to fetch, entity-search limit, character budget). Phase 6 is where the *real* `IAutomaticRecallPolicy` decisions get translated into calls against this simpler surface |
| Neutral recall result | `NamsRecallResult` (exact shape given) | Same shape, split into named types (`NamsRecalledItem`, `NamsRecallWarning`) for clarity; `Items`/`IsPartial`/`Warnings` match verbatim |
| Categories | `nams.reflection`/`nams.observation`/`nams.recent_message`/`nams.relevant_message`/`nams.entity`/`nams.reasoning` | `NamsRecallCategory` enum with the same six values. `Reasoning` is never populated — no reasoning-trace-retrieval method exists on `INamsClient` yet (the REST endpoint is confirmed to exist per Phase 2's own notes but wasn't implemented); documented as a known gap, not silently dropped |
| Formatting rules: map to `MemoryTrustLevel`, escape, delimit, pass through `IMemoryContextAdmissionPolicy` | — | **Only the trust-level mapping is done here** (via a local mirror enum, §3) — escaping/delimiting/admission-policy application is impossible at this layer (§1) and explicitly deferred to Phase 6 |
| Failure behavior | Cancellation propagates; identity/security violations propagate; retrieval failure logs a sanitized warning and degrades; diagnostics record degraded recall | Implemented exactly as specified (§3) |

### Explicitly out of scope for this phase

- Escaping, delimiting, `IMemoryContextAdmissionPolicy` gating, and `IAutomaticRecallPolicy` integration —
  architecturally impossible in `AgentMemory.Nams` (§1), deferred to Phase 6.
- Message/entity search via `SearchMessagesAsync` — doesn't exist (Phase 2 dropped it).
- Reasoning-trace recall — `INamsClient` has no method for it yet.
- Any MAF/`AIContext`/`ChatClientAgent` reference — Phase 6, a separate package.
- Real token-based context budgeting matching `ContextFormatOptions` — this phase applies a simple character
  count as a local safety net only; Phase 6 applies the real, shared budget on top when composing the final
  prompt.

## 3. Detailed design

### `Recall/` (new folder under `src/AgentMemory.Nams/`)

- **`NamsRecallCategory`** (public enum) — `Reflection`, `Observation`, `RecentMessage`, `RelevantMessage`,
  `Entity`, `Reasoning`.
- **`NamsRecallProvenance`** (public enum) — **deliberately mirrors `MemoryTrustLevel` name-for-name and
  value-for-value** (`Untrusted = 0`, `UserProvided = 1`, `ModelGenerated = 2`, `ToolDerived = 3`,
  `VerifiedExternal = 4`, `ApplicationTrusted = 5`), defined locally so Phase 6 can map it onto the real
  enum with a trivial 1:1 cast/switch, without this package ever referencing `AgentMemory.Abstractions`.
  This phase's own mapping logic only ever emits `Untrusted`/`UserProvided`/`ModelGenerated`/`ToolDerived` —
  never `VerifiedExternal` (plan: "verified external data only") or `ApplicationTrusted` (plan: "no NAMS
  content to `ApplicationTrusted` without an application-side verification step" — that step is a host/Phase
  6 decision, never automatic).
- **`NamsRecalledItem`** (public record) — `SourceId`, `Category`, `Content`, `Provenance`, `Role` (nullable,
  messages only), `CreatedAt` (nullable). **XML doc carries an explicit, unmissable security warning**: this
  content is raw, unescaped, undelimited, and has not passed through any admission policy — it must be
  gated by the consuming MAF adapter before ever reaching a model prompt.
- **`NamsRecallWarning`** (public record) — `Category`, `Message`.
- **`NamsRecallResult`** (public record) — `Items`, `IsPartial`, `Warnings`, matching the plan's shape
  verbatim; carries the same security warning as `NamsRecalledItem`.
- **`NamsRecallOptions`** (public class) — `IncludeEntitySearch` (default `true`), `EntitySearchLimit`
  (default `5`), `MaxTotalCharacters` (default `8000`, a local safety-net truncation, not the real budget).
- **`INamsRecallService`** (public interface) — `RecallAsync(namsConversationId, queryText, cancellationToken)`.
  Takes an *already-resolved* NAMS conversation ID (Phase 3's job, not this phase's) and an optional current-turn
  query string (drives entity search; `null`/empty skips it).
- **`NamsRecallService`** (internal) — the implementation: calls `GetContextAsync` (reflections/observations/
  recent messages) and, if a query is given and entity search is enabled, `SearchEntitiesAsync`; maps each
  into `NamsRecalledItem`; deduplicates by `SourceId` (`DistinctBy`, first occurrence wins); orders
  reflections, then observations, then recent messages, then entities (matching the tiers' own
  highest-to-lowest-level ordering, entities appended as an orthogonal category); applies the character
  budget by greedy truncation from the end of that order, marking `IsPartial` if anything was dropped.

### Provenance mapping

| Source | `NamsRecallProvenance` |
|---|---|
| Reflection / Observation | `ModelGenerated` (plan: "hosted reflections and extracted observations default to `ModelGenerated`") |
| Entity | `ModelGenerated` (same class of extracted/synthesized content) |
| Recent message, role `user` | `UserProvided` |
| Recent message, role `assistant` | `ModelGenerated` |
| Recent message, role `tool` | `ToolDerived` (plan: "tool-derived records to `ToolDerived`") |
| Recent message, role `system` or anything else unrecognized | `Untrusted` — the most conservative default, consistent with the #92 trust-boundary epic's established stance that a recalled `system`-role message is exactly the injection vector to be suspicious of by default |

### Failure behavior

- A cancelled token propagates `OperationCanceledException` unconditionally, from either the context call or
  the entity-search call.
- A `NamsOperationException` with `FailureKind` `Authentication` or `Authorization` **propagates** — "identity/
  security violations propagate", never silently degraded.
- Any other `NamsOperationException` (network/timeout/rate-limited/server-error/validation/not-found) is
  caught, logged as a sanitized warning (the exception's own message is already redaction-safe per Phase 2),
  and turns into a `NamsRecallWarning` plus `IsPartial = true` — the caller gets a result with whatever
  succeeded rather than nothing.
- Context retrieval and entity search fail independently — a broken entity search doesn't discard
  successfully-retrieved context, and vice versa.

## 4. Tests

Per the plan's own Phase 4 test list, adapted to what's actually implementable at this layer (escaping/
delimiting/admission-policy tests are Phase 6's, not repeated here):

| Plan requirement | Test coverage |
|---|---|
| Reflections/observations/recent messages mapped | One test per category, asserting category + content + role (messages only) |
| Query-specific results mapped | Entity search results mapped when a query is given |
| NAMS source categories map to expected provenance | Theory over the provenance table above |
| No hosted extraction admitted as `ApplicationTrusted`/`VerifiedExternal` by default | Explicit assertion across all mapped categories |
| Duplicate context entries removed by stable source ID | A duplicate `Id` across categories collapses to one item |
| Exact ordering documented and tested | Reflections, then observations, then recent messages, then entities |
| Context budget applied | `MaxTotalCharacters` truncates and marks `IsPartial` |
| Empty context returns empty result | `GetContextAsync` returning all-empty tiers → empty `Items` |
| Partial result remains observable | `IsPartial`/`Warnings` populated correctly on a degraded path |
| Cancellation propagates | From both the context call and the entity-search call |
| Auth/tenant failures do not degrade silently | `NamsOperationException` with `Authentication`/`Authorization` propagates, isn't swallowed |
| Service outage degrades per configuration | A `ServerError`/`Network` failure degrades to a warning + partial result, doesn't throw |
| No entity search without a query | `IncludeEntitySearch=true` but `queryText=null` never calls `SearchEntitiesAsync` |
| Entity search disabled via options | `IncludeEntitySearch=false` never calls `SearchEntitiesAsync` even with a query |

## 5. Definition of done

- [x] `src/AgentMemory.Nams/Recall/` populated per §3, builds clean (0 warnings) on net8.0/net9.0/net10.0.
- [x] `PackageBoundaryGuardTests`'s B9 rule still passes unmodified (no new package/sibling references —
  `Recall/` only uses BCL + `Microsoft.Extensions.Logging.Abstractions`/`Options`, already referenced).
- [x] All plan-mandated tests pass (20 new tests: 18 recall-service, 2 DI wiring). One real gap caught by the
  cancellation test itself: the test fake's default `GetContextAsync` ignored the cancellation token
  entirely (unlike the real `Neo4jNamsClientAdapter`), so the cancellation-propagation test initially failed
  for the wrong reason — fixed the fake to check `ThrowIfCancellationRequested()`, matching real behavior,
  not a service bug.
- [x] Full existing unit/SK/integration suites remain green: **3155 unit (+20) / 54 SK unit / 308
  live-Neo4j integration**, 0 build warnings.
- [x] Self-reviewed via parallel finder agents.
- [ ] PR opened, CI green (no Copilot review — out of credits), merged.

## 6. Self-review findings and dispositions

Ran 3 parallel finder agents (correctness / cross-file impact / cleanup-conventions) against the staged diff.

- **Doc-hygiene, fixed:** `docs/architecture.md`'s B9 verification bullet and `docs/specification.md`'s
  package note both still said "still with no recall/persistence integration into the memory pipeline" —
  true about the *pipeline* wiring (that's Phase 6) but misleading about this phase's own recall-retrieval
  logic, which now genuinely exists. Both reworded to disclose `INamsRecallService` while keeping the
  correct distinction: retrieval exists, prompt/pipeline wiring doesn't yet.
- **Real robustness gap, fixed:** `ProvenanceForRole`'s role match was case-sensitive (`"user"` literal).
  If NAMS ever returns different casing, this silently under-trusted (never over-trusted, since the fallback
  is the most conservative tier) rather than crashing — still a real gap, not a security hole. Fixed with
  `.ToLowerInvariant()`; added a case-variant regression test (`"User"` → `UserProvided`).
- **Test-coverage gap, closed:** no test exercised `MapEntity`'s `entity.Description ?? entity.Name` fallback
  with an entity actually lacking a description. Added `RecallAsync_EntityWithNoDescription_FallsBackToName`.
- **Cleanup, applied:** added one-line comments justifying `NamsRecallOptions`' two magic-number defaults
  (`EntitySearchLimit = 5`, `MaxTotalCharacters = 8000`) — honest labels ("not tied to any NAMS-side limit",
  "not tuned against any specific model's context window"), not a claim they're carefully chosen.
- **Assessed, not changed:** the two structurally-identical try/catch blocks in `RecallAsync` (context vs.
  entity search) — both reviewers who looked at this agreed extracting a shared helper would need more
  indirection (a delegate + output-list parameter) than the ~15 lines it would save. Left as-is.
- **Verified clean by all 3 reviewers:** exception-filter ordering (auth/authz always reaches its guarded
  catch clause first, in both try blocks), `DistinctBy`'s order-preserving first-occurrence-wins semantics,
  character-budget boundary behavior (exact-fit not marked partial; a first item exceeding the whole budget
  returns empty without throwing), cancellation propagating from both call sites, options-validation wiring
  matching the sibling `NamsOptions` pattern exactly, package boundary (B9 still 6/6, confirmed in code via
  `grep`, not just docs), and `dotnet pack` still succeeding cleanly.

Final counts after fixes: 3157 unit (+2 regression tests) / 54 Semantic Kernel unit, 0 build warnings across
the whole solution.
