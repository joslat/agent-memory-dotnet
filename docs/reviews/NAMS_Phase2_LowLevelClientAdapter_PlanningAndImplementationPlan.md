# NAMS Phase 2 — Low-Level Client Adapter and Error Model: Planning and Implementation Plan

**Prepared:** 2026-07-17
**Branch:** `nams/phase2-client-adapter`
**Purpose:** Continues the NAMS backend effort after Phase 0 (baseline freeze, #129) and Phase 1 (package
skeleton, #130). Executes the "Phase 2: Low-level client adapter and error model" section of
`strategy/NAMS/AgentMemory_NAMS_Backend_Engineering_Plan_V04.md` (§7), the version now canonical after the
V03→V04 drift analysis (`strategy/NAMS/NAMS_V04_Alignment_Plan.md`).

**Why now, without Neo4j's answers yet:** the plan's own Phase 0 no-code gate blocks a *production release*
on unresolved unknowns, not a spike — *"a spike may proceed with explicit temporary assumptions, but it may
not be released."* Issue #128 has zero replies so far. `strategy/NAMS/Neo4j_Questions.md` (new, this phase)
breaks the 39 outstanding questions down by whether each one actually blocks release and whether it's
self-serve-testable against the free NAMS sandbox — most of the ones that matter for Phase 2 are self-serve.
Nothing in this phase depends on an unanswered question; where the plan's own text assumed an answer we
don't have, this phase makes the more conservative choice instead (below).

---

## 1. Task and scope

Build the internal, package-contained boundary between `AgentMemory.Nams` and the outside world: an
HTTP-based client, a typed error model, and a credential-lifecycle abstraction. **No identity/conversation
mapping, no recall/context formatting, no MAF integration** — those are Phases 3+ and out of scope here.

| Plan requirement | Plan text (§7 Phase 2) | This implementation |
|---|---|---|
| Planned files | `Client/{INamsClient,Neo4jNamsClientAdapter,NamsClientFactory,NamsClientExceptionMapper}.cs`, `Domain/{NamsFailureKind,NamsOperationException,NamsOperationResult}.cs` | Same, plus the domain request/response records the interface needs (`NamsConversation`, `NamsContext`, `NamsMessage`, `NamsMessageInput`, `NamsEntity` — referenced by the plan's own interface example but not separately enumerated in its file list) and an `Authentication/` folder for the credential-lifecycle abstraction (new in V04, not in the original file list either) |
| Rule: app/MAF code must not depend on `Neo4j.AgentMemory.MemoryClient` | — | **Satisfied more strongly than required**: we don't take a dependency on `Neo4j.AgentMemory` (the TCK C# client) at all. See §2 for why. |
| `INamsClient` — 5 methods (incl. `SearchMessagesAsync`) | Interface example in §7 | **4 methods.** `SearchMessagesAsync` is dropped — the plan's own text already flags it as unconfirmed ("may not map to anything the API actually exposes... be prepared to drop or redesign"), and we have no live sandbox account yet to verify it. Building against a guessed endpoint shape would be worse than not building it; add it in a later phase once confirmed (`Neo4j_Questions.md` — this is self-serve-testable once we have an account). |
| Retry matrix | Idempotent ops always retry transient failures; writes retry "only with idempotency" | Interpreted conservatively: **idempotency keys don't exist yet** (that's Neo4j-Questions #12-15, unanswered), so writes (`CreateConversationAsync`, `AddMessagesAsync`) get **zero** transient retries for now — a single attempt, fail closed. Reads (`GetContextAsync`, `SearchEntitiesAsync`) retry transient failures per the matrix. Revisit once #12-15 are answered (self-serve, once we have a sandbox account). |
| Authentication and credential lifecycle (new in V04) | `INamsAccessTokenProvider`, refresh-before-expiry, stampede-safe, 401-vs-403 distinction | Implemented, but **API-key-only** for this phase. JWT/Auth0 support is explicitly deferred — its refresh/expiry contract is unconfirmed (Neo4j-Questions #30), and the plan itself says to "prefer the raw API key path... if refresh semantics are unclear." `NamsOptions.ApiKey` becomes a hard requirement (validated, not type-level — see §3). |
| `Neo4jNamsClientAdapter` naming | Implies wrapping the external client | Repurposed: it's **our own** `HttpClient`-based implementation of `INamsClient`, calling the pinned OpenAPI endpoints directly (`docs/reviews/nams-openapi-snapshot-2026-07-17.json`). Name kept as-is since it still means "the Neo4j-NAMS-backed implementation of the client interface" — just not a wrapper around a third-party package. |

### Explicitly out of scope for this phase

- `SearchMessagesAsync` (unconfirmed endpoint — see above).
- JWT/Auth0 token support (deferred pending confirmed refresh contract).
- Idempotency keys on writes (nothing to key against yet — Phase 3's identity/conversation mapping and the
  unanswered idempotency questions come first).
- Any reference to the `Neo4j.AgentMemory` TCK C# client package — deliberately not taken as a dependency at
  all in this plan revision (§2).
- Identity/conversation mapping, recall/context formatting, MAF provider, MCP tools — Phases 3, 4, 6, 8.

## 2. Design decision: build our own HTTP client, don't depend on the TCK C# client

V04 §4.8's "Decision for this plan" gates depending on `Neo4j.AgentMemory` on Neo4j confirming it's the
canonical SDK, that it's stably published, and that its API/license/extensibility are understood — none of
which are answered (Neo4j-Questions #1-6). Waiting on that would block Phase 2 entirely for no
technical reason: **NAMS is a free, self-serve, publicly documented REST API** (we already have its pinned
OpenAPI spec from Phase 0). Building a small internal `HttpClient`-based implementation against that spec:

- Removes the entire "is the TCK client stable/licensed/publishable" question from this phase's critical
  path — it becomes purely a product-relationship/roadmap question for the Neo4j meeting, not an engineering
  blocker.
- Still satisfies the plan's actual *rule* (no app/MAF code depends on an external NAMS client type directly
  — everything is contained inside `AgentMemory.Nams` behind `INamsClient`).
- Costs slightly more code now (implementing 4 REST calls ourselves instead of wrapping an existing client),
  which is a fair trade for zero dependency risk on an unversioned `0.x` external package.
- Doesn't foreclose switching to the TCK client later if Neo4j confirms it's the right long-term dependency
  — `INamsClient` is the seam either way; only `Neo4jNamsClientAdapter`'s internals would change.

## 3. Detailed design

### `Client/`

- **`INamsClient`** (internal, matches the plan's own example) — `CreateConversationAsync`,
  `GetContextAsync`, `AddMessagesAsync`, `SearchEntitiesAsync`. All take a `CancellationToken`.
- **`Neo4jNamsClientAdapter`** — the `HttpClient`-based implementation. Serializes/deserializes with
  `System.Text.Json`, builds requests against `NamsOptions.Endpoint`, delegates retry decisions to
  `NamsRetryPolicy` (new, not separately named in the plan but required to implement the matrix), maps
  non-success responses through `NamsClientExceptionMapper`.
- **`NamsRetryPolicy`** (new) — internal helper taking an `isIdempotent` flag; idempotent calls retry
  network/timeout/429/5xx with exponential backoff (honoring a `Retry-After` header when present on 429);
  non-idempotent calls make one attempt only. Mirrors the dependency-free style of
  `AgentMemory.Enrichment/Http/RetryHttpMessageHandler.cs` rather than adding a Polly dependency (no
  precedent for Polly anywhere in this repo).
- **`NamsClientFactory`** — DI wiring: a named/typed `HttpClient` (base address `NamsOptions.Endpoint`,
  timeout `NamsOptions.RequestTimeout`) via `Microsoft.Extensions.Http`, with a `NamsAuthenticationHandler`
  attached.
- **`NamsClientExceptionMapper`** — maps an `HttpResponseMessage`/exception to a `NamsOperationResult`
  (success or a typed failure), which the adapter then either returns or throws as `NamsOperationException`.

### `Authentication/` (new folder, not in the plan's file list — required by its new §"Authentication and
credential lifecycle")

- **`INamsAccessTokenProvider`** — exact shape from the plan (`GetTokenAsync`, `InvalidateAsync`).
- **`NamsAccessToken`** — value holder (token string + optional expiry); `ToString()`/logging never expose
  the raw value, only a low-cardinality fingerprint/age, per the plan's explicit requirement.
- **`StaticApiKeyNamsAccessTokenProvider`** — the only implementation registered for this phase. Wraps
  `NamsOptions.ApiKey` as a non-expiring token. `InvalidateAsync` is a no-op (a static key can't be
  refreshed; a 401 on this path is a hard failure, not a retry trigger).
- **`NamsAuthenticationHandler`** (`DelegatingHandler`) — attaches `Authorization: Bearer <token>` per
  request; on a 401, invalidates and re-fetches the token and retries the request exactly once (safe even
  for writes: a 401 means the server never authenticated, so it never executed the operation); a 403 is
  never retried and maps straight to `NamsFailureKind.Authorization`.

### `Domain/`

- **`NamsFailureKind`** (internal enum) — `Network`, `Timeout`, `RateLimited`, `ServerError`,
  `Authentication`, `Authorization`, `Validation`, `NotFound`, `Unknown`.
- **`NamsOperationException`** (internal) — carries a `NamsFailureKind` and the original status
  code/message; derives directly from `System.Exception`, **not** `AgentMemory.Abstractions.MemoryException`
  — `AgentMemory.Nams` has zero sibling-package references by design (B9), so it cannot share the
  Abstractions exception hierarchy.
- **`NamsOperationResult<T>`** (internal) — a success/failure envelope `NamsClientExceptionMapper` produces,
  which `Neo4jNamsClientAdapter` unwraps into either a return value or a thrown `NamsOperationException`.
  Keeps the "map a response" logic pure and separately testable from the "throw" side effect.
- **`NamsConversation` / `NamsContext` / `NamsMessage` / `NamsMessageInput` / `NamsEntity`** (internal
  records) — minimal shapes matching the confirmed REST responses for the 4 implemented operations only;
  not an attempt to model the full NAMS schema.

### Options change

`NamsOptionValidator` gains `HasApiKey` (non-null, non-whitespace) — required because this phase's only
supported auth mechanism is the static API key. `NamsOptions.ApiKey` stays `string?` at the type level (not
`required`) since a future JWT-only mode may not need it; the runtime validation is where "required for now"
actually lives, matching how `Endpoint`'s own `required` keyword is already enforced at runtime by
`ValidateOnStart` in this package (Phase 1's own definition-of-done note on this exact point). Existing Phase
1 tests that configured only `Endpoint` are updated to also set `ApiKey`, since a NAMS registration without
any credential can no longer usefully validate successfully.

### DI wiring

`NamsServiceCollectionExtensions.AddNamsAgentMemory` (extended, not replaced) now also registers:
`INamsAccessTokenProvider` → `StaticApiKeyNamsAccessTokenProvider`, an `HttpClient` for `INamsClient` via
`AddHttpClient<INamsClient, Neo4jNamsClientAdapter>()` with `NamsAuthenticationHandler` attached.

### Forward note on ADR-9 (ties to the V04 alignment plan)

`INamsClient` stays `internal` per the plan's own example. Phase 6 (dedicated MAF provider) lands in a
*separate* package per ADR-9 (`AgentMemory.AgentFramework.Nams`), which won't have compile-time access to
`AgentMemory.Nams`'s internals by default. Deferred, not solved now: add
`[InternalsVisibleTo("AgentMemory.AgentFramework.Nams")]` to `AgentMemory.Nams.csproj` when that package is
created — a one-line addition, not a redesign.

## 4. Tests

Per the plan's own Phase 2 test list, using a fake `DelegatingHandler` (deterministic, no real network):

| Plan requirement | Test coverage |
|---|---|
| Successful serialization/mapping | One test per `INamsClient` method, happy path |
| All error classes map correctly | One test per `NamsFailureKind` (400→Validation, 401→Authentication, 403→Authorization, 404→NotFound, 429→RateLimited, 5xx→ServerError, network exception→Network) |
| Timeout / caller cancellation | Distinct tests — a per-request timeout retries (idempotent ops); caller-token cancellation never retries and propagates `OperationCanceledException` |
| 429 with `Retry-After` | Honors the header's delay before retrying (idempotent ops only) |
| Transient 5xx then success | Retries then returns the eventual success (idempotent ops only) |
| Retry exhaustion | Throws the mapped exception after the configured attempt budget |
| Malformed JSON / missing required fields / unknown enum values | Each maps to a clean `NamsOperationException`, never an unhandled deserialization exception |
| Redaction of tokens | No log line or exception message ever contains the raw API key |
| No retry for permanent failures (4xx other than 429) | Single attempt, immediate throw |
| No retry of unsafe writes without idempotency | `CreateConversationAsync`/`AddMessagesAsync` never retry on network/429/5xx |
| Concurrent token requests perform one refresh | Stampede test: N parallel `GetTokenAsync` calls when a refresh is needed produce exactly one underlying fetch |
| A 401 triggers exactly one bounded retry after refresh; a 403 never does | Both paths tested explicitly, including for a write operation (proving it's still safe) |

## 5. Definition of done

- [x] `src/AgentMemory.Nams/{Client,Authentication,Domain}/` populated per §3, builds clean (0 warnings) on
  net8.0/net9.0/net10.0.
- [x] `PackageBoundaryGuardTests`'s existing B9 rule for `AgentMemory.Nams` still passes unmodified (no new
  sibling/framework-SDK references introduced — only `Microsoft.Extensions.Http`/`Logging.Abstractions`
  package refs, consistent with `AgentMemory.Enrichment`'s existing pattern).
- [x] All plan-mandated tests pass (46 new tests under `tests/AgentMemory.Tests.Unit/Nams/`: 7 retry-policy,
  15 exception-mapper, 5 authentication-handler, 4 client-factory, 10 client-adapter, 5 DI-wiring/options).
- [x] Full existing unit/SK/integration suites remain green: **3106 unit (+46) / 54 SK unit / 308
  live-Neo4j integration**, 0 build warnings across the whole solution.
- [x] Self-reviewed via parallel finder agents (real business logic this time, not a config-only skeleton —
  full review applies).
- [ ] PR opened, CI green, merged.

## 6. Self-review findings and dispositions

Ran 3 parallel finder agents (correctness / cross-file impact / cleanup-conventions) against the staged diff.

- **Real bug, fixed:** `NamsRetryPolicy.RetryDelayFor` only read `Retry-After`'s delta-seconds form
  (`RetryConditionHeaderValue.Delta`), never the absolute HTTP-date form (`.Date`) RFC 9110 §10.2.3 also
  permits. If NAMS ever returns the date form on a 429, the code silently fell back to exponential backoff
  instead of honoring the server-specified wait — a real (if minor) deviation from the plan's own "honors a
  `Retry-After` header" requirement. Fixed to check both forms; added a regression test
  (`Idempotent_RateLimitedWithRetryAfterDateForm_RetriesThenSucceeds`).
- **Doc-hygiene gaps, fixed:** `docs/architecture.md`'s B9 verification bullet and `docs/specification.md`'s
  package-count note both still described `AgentMemory.Nams` as "a configuration-surface-only skeleton...
  with no client/HTTP-call behavior yet," pointing at the Phase 1 planning doc — stale the moment this PR's
  own diff added a real client. Same mistake as Phase 1's own CI-caught lesson, this time caught by
  self-review instead of CI. Both updated to describe the Phase 2 state and point at this doc.
- **Cleanup, applied:** `NamsClientExceptionMapper.SafeReadBodyAsync` caught a bare `Exception` (guarded only
  by "not `OperationCanceledException`"), broader than the specific-exception-type idiom used everywhere
  else in this diff and in the `RetryHttpMessageHandler.cs` precedent it models itself on. Narrowed to
  `HttpRequestException or IOException or ObjectDisposedException` — the actual exception types
  `HttpContent.ReadAsStringAsync` can throw.
- **Cleanup, clarified (no code change):** `Neo4jNamsClientAdapter`'s `PropertyNameCaseInsensitive = true` is
  currently dead configuration (every `Domain/*.cs` record already carries an explicit `JsonPropertyName`
  matching the pinned OpenAPI snapshot exactly). Kept as deliberate defense-in-depth against a future field
  added without one; added a one-line comment saying so instead of removing it.
- **Test-coverage gap, closed:** `AddNamsAgentMemory_CalledTwice_DoesNotThrow` proved the second
  registration call didn't throw, but never actually resolved `INamsClient` — so it didn't prove the new
  `AddHttpClient<INamsClient,...>` double-registration path was itself conflict-free. Added an
  `INamsClient`-resolution assertion to close the gap.
- **Assessed, not a defect:** the correctness reviewer flagged, then independently retracted after re-tracing,
  a hypothetical content-buffering-order bug in `NamsAuthenticationHandler`'s 401-retry path — buffering
  happens before either send attempt, so it's safe regardless of content type. No action needed.
- **Assessed, not a defect:** the cross-file reviewer independently re-verified every `Domain/*.cs` record's
  `JsonPropertyName` against `docs/reviews/nams-openapi-snapshot-2026-07-17.json`'s actual schemas
  field-for-field (a second, independent pass beyond the one done during implementation) — all matched, no
  drift.

Final counts after fixes: 3107 unit (+1 regression test) / 54 Semantic Kernel unit, 0 build warnings across
the whole solution. Package boundary (B9) and `dotnet pack` re-verified clean by the cross-file reviewer.
