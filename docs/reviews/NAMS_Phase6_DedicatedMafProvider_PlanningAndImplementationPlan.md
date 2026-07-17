# NAMS Phase 6 — Dedicated MAF Provider: Planning and Implementation Plan

**Prepared:** 2026-07-17
**Branch:** `nams/phase6-dedicated-maf-provider`
**Purpose:** Final phase of the current NAMS push, after Phase 0 (#129) through Phase 5 (#134). Executes
"Phase 6: Dedicated MAF provider" from `strategy/NAMS/AgentMemory_NAMS_Backend_Engineering_Plan_V04.md`
(§7) and ADR-9. This is where every gating deferred from Phases 4/5 (escaping, delimiting, admission policy,
trust-level mapping) finally gets wired in — the largest phase of the six.

---

## 1. New package: `AgentMemory.AgentFramework.Nams`

Per ADR-9 and §5.3's desired package structure, this is a **new, separate package** — not added to
`AgentMemory.Nams` (which must stay framework-free, B9) or `AgentMemory.AgentFramework` (which must stay
backend-neutral for the direct provider). Dependency direction (plan's own diagram, verified against real
code before writing any):

```
AgentMemory.AgentFramework.Nams
    +-- AgentMemory.Nams          (Phases 1-5's public surface: INamsConversationResolver, INamsRecallService,
    |                               INamsPersistenceService, NamsRecallResult/NamsRecallProvenance, etc.)
    +-- AgentMemory.AgentFramework (IMemoryContextAdmissionPolicy, IAutomaticRecallPolicy, ContextFormatOptions,
    |                               RecalledMemoryMessageRole, AgentFrameworkOptions, MemoryIdentity/
    |                               GetMemoryIdentity — all PUBLIC, confirmed by reading each file directly)
    +-- AgentMemory.Core           (RecalledMemoryDelimiter, RecalledMessageRoleGate — both internal;
    |                               needs InternalsVisibleTo, see §2)
    +-- AgentMemory.Abstractions   (MemoryTrustLevel — public, transitively available via AgentFramework too)
    +-- Microsoft.Agents.AI        (AIContextProvider, AgentSession, AIAgent)
    +-- Microsoft.Extensions.AI    (ChatMessage, ChatRole, AIContext)
```

New boundary rule (**B10** in `docs/architecture.md` §5, alongside B1-B9): must not reference `Neo4j.Driver`,
`Microsoft.SemanticKernel`, or `ModelContextProtocol` (this is the MAF adapter specifically, so
`Microsoft.Agents.*` is *allowed*, unlike B9's blanket ban for `AgentMemory.Nams`). Must not create a
reverse reference from `AgentMemory.Nams`/`AgentMemory.AgentFramework`/`AgentMemory.Core`/
`AgentMemory.Abstractions` back to this package (verified structurally impossible to violate accidentally,
since none of those four projects will gain a `ProjectReference` to this new one).

## 2. The one cross-package visibility change this phase requires

`RecalledMemoryDelimiter.Wrap` and `RecalledMessageRoleGate.EffectiveRole` are `internal` to
`AgentMemory.Core`, reachable today only by `AgentMemory.AgentFramework`/`AgentMemory.SemanticKernel` via
`InternalsVisibleTo` (added in #92 Phase 6 specifically "so both adapters share one implementation rather
than each carrying its own copy"). A third adapter needs the same sharing for the same reason. One line
added to `AgentMemory.Core.csproj`:

```xml
<InternalsVisibleTo Include="AgentMemory.AgentFramework.Nams" />
```

`IMemoryContextAdmissionPolicy`/`IAutomaticRecallPolicy`/`ContextFormatOptions`/`RecalledMemoryMessageRole`/
`AgentFrameworkOptions`/`AgentSessionMemoryExtensions.GetMemoryIdentity`/`MemoryIdentity` are all confirmed
`public` in `AgentMemory.AgentFramework` — reachable via a normal `ProjectReference`, no further
`InternalsVisibleTo` needed anywhere else.

## 3. Design

### `Mapping/NamsMafTypeMapper.cs` (internal)

- **`ToTrustLevel(NamsRecallProvenance)`** — a direct `(MemoryTrustLevel)(int)` cast. Safe specifically
  because Phase 4 built `NamsRecallProvenance` to mirror `MemoryTrustLevel` name-for-name/value-for-value
  for exactly this moment.
- **`ToContextMessages(NamsRecallResult, ContextFormatOptions, IMemoryContextAdmissionPolicy, ILogger?)`** —
  the NAMS equivalent of `MafTypeMapper.ToContextMessages`, adapted to `NamsRecallResult`'s flat
  `Items`/`Category` shape instead of `MemoryContext`'s separately-typed lists:
  - Optional `ContextPrefix` lead message (reused verbatim from `ContextFormatOptions` — same untrusted-
    reference-data framing already used by the direct backend).
  - `RecentMessage`/`RelevantMessage` category items: admission-checked per item (`Admit("messages", ...)`),
    role-gated via `RecalledMessageRoleGate.EffectiveRole` (a `NamsRecalledItem.Role` of `"system"`/`"tool"`
    demotes to `"user"` below `MinimumTrustForSystemRole` — identical protection to #92 Phase 7, now
    extended to NAMS), mapped to `ChatRole` via a small local `ToMafRole` (re-implemented rather than
    reaching for another `InternalsVisibleTo` into `AgentMemory.AgentFramework` for one 6-line switch),
    **not delimited** (matches #92 Phase 8's own reasoning: a recalled message renders as an individual
    conversation turn, not an injected block — wrapping it would look bizarre). Bounded by
    `MaxChatHistoryMessages` (newest-first, per Phase 4's own recall ordering).
  - `Reflection`/`Observation`/`Entity` category items: admission-checked per item, grouped by effective
    block role (`System`/`User` per `MinimumTrustForSystemRole`/`DefaultMemoryRole`), **delimited** via
    `RecalledMemoryDelimiter.Wrap` (#92 Phase 1 protection, now extended to NAMS) — always kept, never
    truncated by `MaxChatHistoryMessages` (#91's same "durable memory isn't subject to the chat budget"
    reasoning).
  - `Reasoning` category: never populated by Phase 4 (no data source exists yet) — nothing to map, a no-op
    branch, not silently dropped logic.
- **`ToPersistenceMessages(requestMessages, responseMessages)`** → `(userMessages, assistantMessages)` for
  `INamsPersistenceService.PersistTurnAsync`. Filters `requestMessages` to `ChatRole.User` with non-blank
  text (matches the direct backend's own request-message filter) and `responseMessages` to
  `ChatRole.Assistant` with non-blank text. **Deliberately excludes non-assistant-role response messages**
  (e.g. tool-call results) rather than mislabeling them — matches the plan's content policy ("tool calls...
  recorded through the reasoning endpoints, not flattened into chat text") given `INamsClient` has no
  reasoning-endpoint method yet (same disclosed gap as Phases 4/5).

### `NamsMemoryContextProvider.cs` (public, `AIContextProvider`)

Mirrors `Neo4jMemoryContextProvider`'s shape closely (same base-class usage, same
`ProvideAIContextAsync`/`StoreAIContextAsync` split, same `internal` test-seam methods), backed by Phases
3/4/5 instead of `IMemoryService`:

- **Identity extraction**: reuses `AgentMemory.AgentFramework`'s existing, already-backend-neutral
  `session.GetMemoryIdentity(options)` (a public extension method reading the session's `StateBag` — no
  Neo4j-specific logic in it at all) to get `(UserId, SessionId, ConversationId, ApplicationId)`.
- **A real semantic difference from the direct backend, handled deliberately**: `NamsConversationIdentity`
  (Phase 3) requires all four fields non-blank; the direct backend tolerates a null `UserId`/`ApplicationId`
  (unscoped/shared memory, default store). NAMS conversations are inherently user-scoped by the REST API's
  own model — there is no "anonymous NAMS conversation" concept. `ApplicationId` gets a documented literal
  fallback (`"default"`) when absent — a single-application host never needs to think about this. `UserId`
  gets **no fabricated fallback** — inventing one would silently merge every anonymous caller into one fake
  identity, exactly the kind of unscoped-write footgun this repo's own multi-tenant-isolation work (#100)
  exists to prevent. When `UserId` is missing, both `ProvideAIContextAsync` and `StoreAIContextAsync` log a
  warning and degrade to an empty/no-op result — consistent with every other degradation path in this
  provider, not a new failure mode.
- **`ProvideAIContextAsync`**: extract identity → resolve NAMS conversation
  (`INamsConversationResolver.ResolveAsync`) → invoke `IAutomaticRecallPolicy.DecideAsync` (the REAL policy
  type, reused as-is — it's backend-neutral, just a category/intent decision) → if the decision says skip,
  return empty `AIContext` → otherwise call `INamsRecallService.RecallAsync` with the joined user-turn text
  as the query → map via `NamsMafTypeMapper.ToContextMessages` → return. Every failure mode (conversation
  resolution failure, recall failure) is caught and logged, degrading to an empty context — matches the
  direct provider's own resilience contract exactly. Tool exposure: **always `null`** this phase — no
  NAMS-backed `MemoryToolFactory` equivalent exists yet (building NAMS-backed AI tools is out of scope here);
  this trivially satisfies "expose tools only when explicitly enabled" by exposing none.
- **`StoreAIContextAsync`**: skip entirely if `context.InvokeException is not null` (failed invocation never
  persists — matches the direct provider and the plan's own algorithm step 1) → extract identity → resolve
  conversation → map request/response messages via `NamsMafTypeMapper.ToPersistenceMessages` → one call to
  `INamsPersistenceService.PersistTurnAsync` (both user and assistant messages, from this one call — see
  §4 on single-writer scope). `AgentFrameworkOptions.AutoExtractOnPersist` is read from nowhere in this
  path — there is no extraction call anywhere in the NAMS write path (Phase 5 never calls anything
  extraction-related), so the option is inertly ignored by construction, exactly as the plan requires
  ("NAMS owns asynchronous extraction... the option is either ignored... or replaced"). Documented, not
  silently true by accident.

### `NamsAgentFrameworkServiceCollectionExtensions.cs` (public)

`AddNamsAgentMemoryFramework()` registers `NamsMemoryContextProvider` (transient — a new instance per agent,
matching how `Neo4jMemoryContextProvider` is typically composed into a `ChatClientAgent`). Requires
`AddNamsAgentMemory(...)` to have been called first (documented precondition, same as the direct backend's
own `AddAgentMemoryFramework()` requiring `AddNeo4jAgentMemory(...)` first) — not re-validated here; DI
resolution of the Phase 3/4/5 service dependencies will simply fail loudly if it wasn't.

## 4. Scope boundaries (explicitly out of scope, or handled differently than the plan's literal words)

- **Startup validation for "both direct and NAMS registered as default"**: this repo has no existing
  concept of a single "default backend" selector to validate against (the direct backend and a NAMS backend
  are two independently-composed sets of services, not slotted into one interchangeable-backend
  registration point) — building that selection/validation machinery is a larger, `IMemoryService`-level
  concern this phase does not invent. Not silently skipped: documented here as a real gap, deferred to
  whenever (if ever) a genuine backend-selection abstraction is built (Phase 7's own entry gate explicitly
  requires *both* providers to be observed in real use first, before any convergence work starts).
- **Single-writer *registration* validation across MAF components**: with only one NAMS-backed writer
  existing after this phase (`NamsMemoryContextProvider` itself, handling both request and response
  persistence in one call — see below), there is nothing else registered yet to conflict with. The
  plan's "one canonical golden path" is satisfied by construction: this phase deliberately does *not* build
  a separate NAMS-backed `ChatHistoryProvider`, so there is only ever one writer.
- **Tool exposure**: no NAMS-backed `MemoryToolFactory` equivalent — always `null`.
- **`SearchMessagesAsync`/reasoning-endpoint-backed categories**: still absent (Phase 2's own dropped
  method); `RelevantMessage`/`Reasoning` categories remain unpopulated, exactly as Phase 4 already disclosed.
- **NAMS sample against a live sandbox** (an explicit Phase 6 exit criterion): no live NAMS account exists
  yet (per `strategy/NAMS/Neo4j_Questions.md` — self-serve testable once one exists, not blocking
  development). Not attempted here; this phase's tests are all against fakes, same posture as every prior
  phase.

## 5. Tests

Mirrors `Neo4jMemoryContextProviderTests.cs`'s own structure, adapted to what's real for this phase (tool
exposure and extraction-related cases collapse to "always off/never called" assertions rather than
configurable-behavior tests, per §4):

- Default recall maps reflections/observations/messages/entities into gated `ChatMessage`s.
- Recall skipped when `IAutomaticRecallPolicy` decides not to recall.
- No user message → empty context, no conversation resolution attempted.
- Conversation-resolution failure and recall-service failure each degrade to empty context, logged, not
  thrown.
- Cancellation propagates from both `ProvideAIContextAsync` and `StoreAIContextAsync`.
- Missing `UserId` degrades (no fabricated identity), both for recall and persistence.
- Security admission: a `Strict`-mode-excluded item is absent from the result; `Permissive` includes it
  delimited.
- Recalled message role gating: a NAMS message persisted with role `"system"` demotes to `"user"` below
  `MinimumTrustForSystemRole`.
- Delimiting: a reflection/observation/entity block is wrapped via `RecalledMemoryDelimiter`; a chat message
  is not.
- Tool exposure always `null`.
- Post-run persistence: failed invocation skips persistence entirely; a successful turn persists user-then-
  assistant messages in one call; non-assistant-role response messages are excluded, not flattened.
- `NamsMafTypeMapper.ToTrustLevel` round-trips every `NamsRecallProvenance` value onto the matching
  `MemoryTrustLevel` value.

Plus package-level tests: the new B10 `PackageBoundaryGuardTests` rule; `dotnet pack`.

## 6. Definition of done

- [x] `src/AgentMemory.AgentFramework.Nams/` created, added to `AgentMemory.slnx` and
  `eng/release-packages.txt`, builds clean (0 warnings) on net8.0/net9.0/net10.0.
- [x] `AgentMemory.Core.csproj` gains the one `InternalsVisibleTo` line.
- [x] New `PackageBoundaryGuardTests` B10 rule.
- [x] All plan-mandated (applicable) tests pass (29 new: 13 recall-side provider tests, 6 persistence-side
  provider tests, 6 trust-level mapping tests, 2 DI wiring, 2 new B10 boundary theory cases via the existing
  parameterized test).
- [x] Full existing unit/SK/integration suites remain green — **3203 unit (+28) / 54 SK unit / 308
  live-Neo4j integration**, 0 build warnings, including every existing `Neo4jMemoryContextProviderTests`
  test (49/49) unchanged (plan's own exit criterion, explicitly re-run and confirmed, not assumed).
- [x] `dotnet pack` succeeds for the new package; the exact CI `eng/release-packages.txt` consistency-check
  logic replicated locally (learned from Phase 1's own CI-caught mistake) — clean, no unlisted directories.
- [x] Self-reviewed via parallel finder agents.
- [ ] PR opened, CI green (no Copilot review — out of credits), merged.

## 7. Self-review findings and dispositions

Ran 3 parallel finder agents (correctness / cross-file impact / cleanup-conventions) against the staged diff
— the largest phase yet, so extra scrutiny on the new package's boundary and the direct-backend comparison.

- **Real fidelity gap, fixed (independently flagged by 2 of the 3 reviewers):** `NamsMafTypeMapper.ToContextMessages`'s
  reflections/observations/entities block-building loop dropped the human-readable category prefix the
  direct backend's `MafTypeMapper.CategoryMessages` always includes (e.g. "Relevant entities: ") — the model
  would only ever see the category name via the delimiter tag's attribute
  (`category="nams.reflection"`), never as plain-language framing inside the visible content. Not a security
  regression (the category attribute still exists and blocks are still delimited), but a real prompt-quality
  loss versus the pattern this phase explicitly mirrors. Added a `CategoryPrefix` helper alongside the
  existing `CategoryName` lookup; added a regression test
  (`BuildContextAsync_ObservationBlock_HasHumanReadablePrefix`).
- **Doc-hygiene, fixed:** this planning doc's own §6 test-count breakdown didn't match the actual test files
  (claimed 15 recall-side/7 persistence-side; the real counts were 12/6, not even summing to the doc's own
  stated 28 total) — a self-inflicted arithmetic error caught by the cross-file reviewer independently
  re-counting rather than trusting the label. Corrected to the real, verified counts (now 13/6 after the
  prefix-test addition, 29 total).
- **Minor clarity, fixed:** added an XML doc note to `NamsIdentity` cross-referencing
  `AgentMemory.AgentFramework.MemoryIdentity` and calling out the field-order/nullability difference between
  the two similarly-shaped, similarly-named types in different packages, to remove ambiguity for a future
  reader.
- **Assessed, not changed (pre-existing, not introduced by this PR):** `ExtractIds`'s three-way session-id
  fallback (`identity.SessionId ?? agent?.Id ?? Guid.NewGuid()...`) could theoretically generate two
  different session IDs across `ProvideAIContextAsync`/`StoreAIContextAsync` for the same turn if both
  fallbacks were exhausted on both calls. Verified this is a **verbatim copy** of the already-merged,
  unmodified `Neo4jMemoryContextProvider.ExtractIds` — an inherited risk from the existing direct backend,
  not a new gap this phase introduces; out of scope to fix here (would need to be addressed, if at all, in
  the shared pattern both providers use).
- **Assessed, not changed:** `CategoryName`/`ToMafRole`'s small local re-implementations (rather than another
  `InternalsVisibleTo` grant into `AgentMemory.AgentFramework` for one 6-line switch) — both reviewers who
  looked at this on reflection agreed the wider, more permanent surface-area cost of another cross-package
  grant isn't worth it for a switch this small and unlikely to need independent changes.
- **Assessed, not changed:** the DI test's stand-in-registration approach (NSubstitute fakes instead of a
  real `AddNamsAgentMemory()` call) — confirmed a reasonable simplification, not a real gap; each of
  `AddNamsAgentMemory()`/`AddAgentMemoryFramework()` already has its own dedicated DI tests proving its own
  registrations resolve, and a Scoped consumer of Singleton dependencies is DI-valid regardless of how those
  singletons got registered.
- **Verified clean by all 3 reviewers:** the `NamsRecallProvenance`→`MemoryTrustLevel` 1:1 cast (both enums'
  members and explicit values compared directly, not just the comment trusted), every recall/resolution
  failure path degrading to an empty context without throwing (except cancellation and, deliberately,
  nothing else), the `UserId is null` guards preventing any `NamsConversationIdentity` from ever being
  constructed with a null `UserId`, `PerformStoreAsync`'s empty-message short-circuit skipping conversation
  resolution entirely, `ToPersistenceMessages`'s `ChatRole` equality never matching a custom role, the B10
  package-boundary rule (8/8, including both new theory cases), zero changes to the existing
  `AgentMemory.AgentFramework` package (49/49 `Neo4jMemoryContextProviderTests` unchanged, confirmed via
  `git diff --stat`), the exact CI `eng/release-packages.txt` consistency-check logic replicated cleanly,
  and `dotnet pack` succeeding.

Final counts after fixes: 3204 unit (+1 regression test) / 54 Semantic Kernel unit / 308 live-Neo4j
integration, 0 build warnings across the whole solution.
