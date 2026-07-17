# Stabilization — Issue Analysis and Implementation Plan

**Prepared:** 2026-07-17
**Branch:** `stabilization/phase0-issue-analysis`
**Purpose:** Phase 0 of a 3-phase plan (stabilization → NAMS Phase 0 baseline freeze → NAMS package skeleton). A 5-angle parallel audit of the whole repository looking for issues, gaps, problems, misalignments, and errors, run before starting NAMS work so that work begins on a genuinely clean baseline.

**Method:** 5 independent audit passes (build/test hygiene, documentation-to-code alignment, cross-adapter consistency, configuration/validation gaps, CI/CD and package hygiene), each scoped to a specific angle and asked to report only concrete, verified findings. 27 findings total. Each is triaged below as **Fix now** (part of this stabilization PR) or **Defer** (logged, not blocking, with rationale).

---

## Angle 1: Build & test hygiene

**Result: clean.** Release build: 0 warnings/0 errors. Zero `TODO`/`FIXME`/`HACK`/`XXX`/`BUG` markers in actual code (repo has an enforced zero-marker policy per `CONTRIBUTING.md`, genuinely upheld). Zero skipped/disabled tests. All 6 `#pragma warning disable` sites are narrowly-scoped, justified suppressions for experimental APIs (`SKEXP0001`, `MAAI001`) or an intentionally-tested obsolete-alias path — none masking a real problem. No dead code or orphaned test files found. Full unit (3021) and Semantic Kernel unit (52) suites green.

No action needed on this angle.

---

## Angle 2: Cross-adapter consistency

### 2.1 [HIGH — security, Fix now] `Neo4jChatHistoryProvider.ProvideChatHistoryAsync` and `Neo4jChatMessageStore.GetMessagesAsync` replay recalled messages with zero admission-check and zero role-gating

**Files:** `src/AgentMemory.AgentFramework/Neo4jChatHistoryProvider.cs:90-93`, `src/AgentMemory.AgentFramework/Neo4jChatMessageStore.cs:91-94`

Both map `RecentMessages.Items` straight through the bare `MafTypeMapper.ToChatMessage(Message)` helper — no `IMemoryContextAdmissionPolicy` check, no `RecalledMessageRoleGate.EffectiveRole` demotion. This is the exact same underlying data (`RecentMessages`) that `MafTypeMapper.ToContextMessages` correctly gates. A message persisted with a caller-chosen `"system"`/`"tool"` role via `memory_store_message` (MCP) or `Neo4jMemoryPlugin.AddMessageAsync` (SK) — both accept an unvalidated role string — would resurface with **full, unrestricted authority** on every future turn of the *same session* via either of these two default-registered surfaces, not just via cross-session recall. This is the same vulnerability class #92 Phase 7 closed, on two more surfaces Phase 7's own self-review didn't reach.

Both are registered by default in `AddAgentMemoryFramework` (`services.TryAddScoped<Neo4jChatMessageStore>()`, `services.TryAddScoped<Neo4jChatHistoryProvider>(...)`), not obscure escape hatches — `Neo4jChatHistoryProvider` is the primary MAF 1.1.0 `ChatHistoryProvider` integration point, and `Neo4jChatMessageStore.GetMessagesAsync` is `Neo4jMicrosoftMemoryFacade.GetContextForRunAsync`'s query-less fallback path.

**Verified safe to fix:** `Admit`/`RecalledMessageRoleGate` only change behavior when a host explicitly configures `SecurityMode=Strict` or raises `MinimumTrustForSystemRole` above its default (`Untrusted`). Default-configured hosts see zero behavior change — this is purely closing a previously-unprotected path, matching the same safe, additive shape every #92 phase has shipped.

**Fix plan:**
1. Extract the existing `Admit` closure logic inside `MafTypeMapper.ToContextMessages` into a new reusable `internal static` method (pure refactor, `ToContextMessages`'s own behavior unchanged — its local closure just delegates to the new static method).
2. Add a new `internal static List<ChatMessage> MafTypeMapper.ToGatedChatMessages(IEnumerable<Message>, ContextFormatOptions, IMemoryContextAdmissionPolicy, ILogger?)` that applies the same admission-check + role-gate + `ToChatMessage` pipeline, for reuse across all "replay this session's own history" surfaces (directly addresses the audit's own suggestion: "factor a shared helper so the three call sites can't drift again").
3. `Neo4jChatHistoryProvider`: add an optional `IMemoryContextAdmissionPolicy? admissionPolicy = null` constructor parameter (DI already registers the default policy, so this resolves automatically); call `ToGatedChatMessages` in `ProvideChatHistoryAsync`.
4. `Neo4jChatMessageStore`: add optional `IOptions<ContextFormatOptions>? formatOptions = null` and `IMemoryContextAdmissionPolicy? admissionPolicy = null` constructor parameters (both already registered whenever `AddAgentMemoryFramework` is called, which is the only place this class is registered); call `ToGatedChatMessages` in `GetMessagesAsync`.
5. Tests: mirror the existing Phase 7/8 test shape — default options unaffected (regression), Strict mode excludes instruction-like content, raised `MinimumTrustForSystemRole` demotes a privileged-role message, ordinary user/assistant messages never touched. Both a unit-test and a live-Neo4j integration test (mirroring `RecalledMessageRoleGatingIntegrationTests`).

### 2.2 [MED — DI/config drift, Fix now] `AddNeo4jTextSearch` doesn't pick up DI-configured security options

**File:** `src/AgentMemory.SemanticKernel/KernelMemoryExtensions.cs:65-72`

`AddNeo4jTextSearch` closes over the `securityOptions` parameter directly instead of falling back to `IOptions<MemoryRecallSecurityOptions>` from the container when the parameter is omitted, unlike `AddNeo4jMemoryPlugin` (same file) which registers/configures that exact options type. A host who configures security via `AddNeo4jMemoryPlugin(configureSecurity: ...)` and separately calls `AddNeo4jTextSearch(sessionId, userId)` without re-passing options silently gets `Neo4jTextSearch` running under hardcoded `Permissive` defaults, diverging from the rest of the same kernel.

**Fix plan:** change the factory to `securityOptions ?? sp.GetService<IOptions<MemoryRecallSecurityOptions>>()?.Value`. Additive, no behavior change for callers who already pass options explicitly.

### 2.3 [LOW — consistency, Fix now] Two MCP entity tools bypass the isolation-policy call convention

**File:** `src/AgentMemory.McpServer/Tools/EntityTools.cs` — `MemoryGetEntity` and `MemoryRecordEntityFeedback`

Both build `MemoryScope` directly from `userId` instead of calling `isolationPolicy.ResolveReadScope(...)`, unlike every sibling tool in the same file (`MemoryGetEntityProvenance`) and package. Currently harmless only because `LongTermMemoryService`'s internal `Resolve()` re-applies `ResolveReadScope` centrally, so `StrictMultiTenant` still fails closed today — but it's an easy-to-miss inconsistency that would silently reopen if that Core safety net were ever refactored without a corresponding MCP-layer test catching it.

**Fix plan:** call `isolationPolicy.ResolveReadScope(...)` explicitly in both methods, matching the file's own established convention. Defense-in-depth, no behavior change (the resolved scope is already identical via the Core fallback).

### 2.4 [MED — extensibility gap, Defer] The `IMemoryContextAdmissionPolicy` extensibility point is MAF-only

**Files:** `src/AgentMemory.SemanticKernel/Neo4jTextSearch.cs:170-172`, `src/AgentMemory.Core/Services/MemoryContextFormatter.cs:65-89`

Only `Neo4jMemoryContextProvider`/`Neo4jMicrosoftMemoryFacade` (MAF) accept a host-supplied `IMemoryContextAdmissionPolicy`; SK's equivalent call sites always call the internal static `RecalledMemoryAdmission.ShouldAdmit`/`InstructionLikeContentDetector` directly, with no DI seam. A host who registers a custom, stricter policy gets it honored in MAF but silently keeps the stock heuristic in SK.

**Why deferred, not fixed now:** this needs a real design decision — promoting a shared admission-policy abstraction into `AgentMemory.Core`/`Abstractions` (so both MAF and SK can accept the same pluggable interface) is a cross-package, potentially-public-API change, not a small additive fix. It deserves its own scoped PR with an explicit before/after API surface review, not a rushed decision bundled into a stabilization pass. Logged as a #92 Phase 9+ backlog candidate.

---

## Angle 3: Configuration & validation gaps

No `IValidateOptions<T>`/`ValidateDataAnnotations()` is used anywhere in this codebase — every validated options class uses the fluent `.Validate(...).ValidateOnStart()` pattern, applied inconsistently. All 10 findings below are **Fix now** — each is a small, mechanical, well-precedented addition (the codebase already has the right pattern in ~10 other places; these are gaps in applying it, not a new pattern to invent).

| # | File | Gap | Fix |
|---|---|---|---|
| 1 | `src/AgentMemory.Core/ServiceCollectionExtensions.cs:29` | `MemoryOptions` (incl. `MemoryIsolationOptions.Mode`) has zero validation; `DefaultMemoryIsolationPolicy`'s switch `default:` case silently falls back to the most permissive `SingleTenant` behavior for any unmapped value | Add `.Validate(o => Enum.IsDefined(typeof(MemoryIsolationMode), o.Isolation.Mode), ...)`; make the switch's `default:` throw instead of falling through |
| 2 | Same `MemoryOptions` registration | `LongTermMemoryOptions.MinConfidenceThreshold`/`DeduplicationSimilarityThreshold`/`DeduplicationConfidenceBump`/`FeedbackConfidenceDelta` (meant `[0,1]`), `ShortTermMemoryOptions.DefaultRecentMessageLimit`/`MaxMessagesPerQuery`, `ReasoningMemoryOptions.MaxTracesPerSession` all unguarded | Add `.Validate()` rules for each; a non-positive `MaxMessagesPerQuery` currently only fails at Neo4j query time via a bad `LIMIT` |
| 3 | `src/AgentMemory.Neo4j/Infrastructure/ServiceCollectionExtensions.cs:46` | `MemoryStoreOptions` has no validation (unlike sibling `Neo4jOptions` four lines above); blank `DatabasePrefix` under `DatabasePerApplication` only surfaces at first provisioning | Validate `DatabasePrefix` is non-blank when `Strategy == DatabasePerApplication` |
| 4 | `src/AgentMemory.SemanticKernel/KernelMemoryExtensions.cs:27-29` | `MemoryRecallSecurityOptions` has no `.Validate()`/`.ValidateOnStart()`, unlike its AgentFramework counterpart `ContextFormatOptions` | Add `.ValidateOnStart()` at minimum, mirroring the AgentFramework registration |
| 5 | `ContextFormatOptions.cs`, `MemoryRecallSecurityOptions.cs`, `MemoryContextFormatterOptions.cs` | None of the four #92 trust/security enum knobs (`SecurityMode`, `MinimumTrustForAdmissionBypass`, `DefaultMemoryRole`, `MinimumTrustForSystemRole`) are range-checked; `IConfiguration`'s enum binder accepts any integer for a numeric enum, so e.g. `"MinimumTrustForAdmissionBypass": 99` silently binds to an undefined `MemoryTrustLevel` | Add `Enum.IsDefined` validation for each, alongside the existing `MaxChatHistoryMessages` check |
| 6 | `src/AgentMemory.Abstractions/Options/ExtractionOptions.cs` | `AutoMergeThreshold`/`SameAsThreshold` (documented as ordered) have zero validation anywhere; setting them backwards silently inverts merge-vs-relate behavior | Validate both in `[0,1]` and `SameAsThreshold <= AutoMergeThreshold` |
| 7 | `src/AgentMemory.McpServer/ServiceCollectionExtensions.cs:74-80` | `AgentMemoryMcpOptions.DefaultConfidence` (stamped onto every MCP-created entity/fact/preference) has no range validation | Add `.Validate(o => o.DefaultConfidence is >= 0 and <= 1, ...)` |
| 8 | `src/AgentMemory.Neo4j/Infrastructure/Neo4jOptions.cs:10` | `ConnectionAcquisitionTimeout` is the one unguarded field in an otherwise well-validated class | Add `.Validate(o => o.ConnectionAcquisitionTimeout > TimeSpan.Zero, ...)` |
| 9 | `src/AgentMemory.Extraction.AzureLanguage/...ServiceCollectionExtensions.cs:24-30` | `PreferenceSentimentThreshold`/`KeyPhraseFactConfidence`/`LinkedEntityFactConfidence` unchecked while sibling fields in the same validator are checked | Add `[0,1]` range checks for the three confidence fields |
| 10 | `src/AgentMemory.Enrichment/ServiceCollectionExtensions.cs:49` | `EnrichmentCacheOptions` has no validation, unlike its two siblings registered in the same method; a non-positive cache duration only fails inside `IMemoryCache.Set` | Add `.Validate(o => o.GeocodingCacheDuration > TimeSpan.Zero && o.EnrichmentCacheDuration > TimeSpan.Zero, ...)` |

**Confirmed clean, no collision risk:** `NamsOptions` does not exist anywhere in the repo today — the planned `AgentMemory.Nams` package (Phase 2 of this plan) is free to use that name.

---

## Angle 4: CI/CD and package hygiene

| # | Finding | Severity | Decision | Fix |
|---|---|---|---|---|
| 1 | `.github/workflows/release.yml`'s sample-smoke-build step is missing `AgentMemory.Sample.ShoppingAssistant`, present in `ci.yml`'s equivalent step | Med | **Fix now** | Add the missing `dotnet build` line to `release.yml` |
| 2 | `src/AgentMemory.Enrichment/AgentMemory.Enrichment.csproj`: `Microsoft.Extensions.Caching.Memory` still at `10.0.5` while its file-mates were bumped to `10.0.10` by PR #115 | Med | **Fix now** | Bump to `10.0.10` |
| 3 | `samples/AgentMemory.Sample.ShoppingAssistant/...csproj`: `Microsoft.Extensions.Hosting` pinned at `10.0.5`, all 11 other referencing projects at `10.0.10` | Med | **Fix now** | Bump to `10.0.10` |
| 4 | `Directory.Build.props` net10.0-multi-targeting rationale comment cites `OpenTelemetry.Api 1.12.0`; actual pinned version is `1.15.3` since PR #106 | Low | **Fix now** | Update the comment |
| 5 | `AgentMemory.Sample.BlendedAgent`/`AgentMemory.Sample.MinimalAgent` pin `Microsoft.Extensions.Hosting` with a floating `Version="*"` — also why Dependabot's PR #115 silently skipped both files | Med | **Fix now** | Pin to explicit `10.0.10`, matching every other sample, so Dependabot tracks them going forward |
| 6 | No NuGet restore caching in any of the 3 workflows | Low | **Fix now** | Add `cache: true` (+ `cache-dependency-path`) to each `actions/setup-dotnet` step |
| 7 | `ci.yml`/`release.yml` are near-duplicate step-for-step (finding #1 above is a direct symptom of that duplication drifting) | Low | **Defer** | Factoring shared steps into a reusable `workflow_call` workflow is a real improvement but is itself a CI-risk change (could break either pipeline in a way that's only visible on the next real release) — deserves its own isolated PR with a release dry-run, not bundled into a stabilization pass that's also touching several other things at once |

**Confirmed clean (verified, not just assumed):** GitHub Actions pins fully consistent post-#125; `Microsoft.Extensions.Options` is consistently `10.0.10` everywhere (the specific risk flagged going into this audit — the manual conflict-resolution bump in `AgentMemory.SemanticKernel.csproj` leaving siblings behind — did NOT materialize); no hardcoded secrets; `AgentMemory.slnx` matches disk exactly; `.gitignore`/`git status` clean.

---

## Angle 5: Documentation-to-code alignment

All 6 findings are doc-only text corrections — **Fix now**, zero code risk, mechanical edits.

| # | File | Finding | Fix |
|---|---|---|---|
| 1 | `docs/architecture.md` §3.4.6 | MCP surface table says "25 MCP tools, 6 resources, 3 prompts"; actual is 33/12/6 | Update to real counts |
| 2 | `docs/architecture.md` (~13 locations) | Dependency version table stale post-PR #106/#115: `Microsoft.Extensions.AI.Abstractions` shown as `10.5.1` (actual `10.8.0`), `Microsoft.Extensions.{DI,Logging,Options}` shown as `10.0.5` (actual `10.0.10`), `OpenTelemetry.Api` shown as `1.12.0` (actual `1.15.3`) | Bulk-update all version strings to match current `.csproj` values |
| 3 | `docs/agent-framework.md` | Trust-boundary section only describes #92 Phase 1 and explicitly claims Phases 2+ are "open work" — Phases 2-8 all shipped | Add a summary of Phases 2-8 (or a pointer to `architecture.md` §3.2.2-3.2.4), remove the "open work" framing |
| 4 | `docs/architecture.md` header | "Last Updated: 2026-07-11" while `git log` shows the file was last edited 2026-07-17 and its own body already documents Phase 5-7 work | Bump the header date |
| 5 | `docs/specification.md` | Claims "Status: Current, code-aligned" with a stale 2026-07-09 date (actual last edit 2026-07-14); #100 (`IMemoryIsolationPolicy`) and #92 Phases 1-8 unmentioned in Isolation/Integration Requirements | Bump date; add a line naming `IMemoryIsolationPolicy`/trust boundaries in the relevant requirements sections |
| 6 | `docs/README.md` | Docs index omits both `docs/security/threat-model.md` and `docs/security/production-checklist.md`, despite the root README prominently linking both | Add both as rows in the docs index table |

**Confirmed accurate, no finding:** package/type/schema counts across `architecture.md`/`specification.md` (all independently re-verified against source); `docs/security/threat-model.md` ("clearly the best-maintained doc in the repo"); `CHANGELOG.md`; root `README.md`; all relative markdown links across the 7 audited docs resolve correctly.

---

## Summary: what's in this PR

**Fixing:** all of Angle 3 (10 items), all of Angle 4 except #7 (6 of 7 items), all of Angle 5 (6 items), and Angle 2's items 2.1-2.3 (3 items) — **25 fixes total**, prioritized with 2.1 (the HIGH security gap) first.

**Deferring, logged as backlog candidates (not blocking, no regression risk from deferring):**
- Angle 2.4 — promoting a shared, pluggable admission-policy abstraction to Core so SK gets the same extensibility MAF has. Needs its own design/API-surface review.
- Angle 4.7 — collapsing `ci.yml`/`release.yml` duplication into a shared reusable workflow. Real improvement, but higher-risk to bundle into a multi-fix stabilization pass; do it in isolation with a release dry-run.

Every fix above changes either doc text, adds a validation guard, corrects a version pin, or closes a previously-unprotected path whose gating is a no-op under default configuration — no fix in this list changes default runtime behavior for an existing, correctly-configured consumer.

## Implementation notes (found while fixing, not part of the original audit)

- **2.3's fix changes an internal representation, not behavior.** Routing `MemoryGetEntity`/`MemoryRecordEntityFeedback` through `IMemoryIsolationPolicy.ResolveReadScope` means an absent `userId` now resolves to `MemoryScope.Global` (a concrete sentinel, `OwnerId = null`, `HasOwnerFilter = false`) instead of a literal `null` `MemoryScope?`. Both are behaviorally identical — "no owner filter, see all records" — but two pre-existing unit tests asserted `scope == null` specifically and needed updating to assert `!scope.HasOwnerFilter` instead, matching the convention `MemoryGetEntityProvenance`'s tests already used.
- **`MafTypeMapper.ToGatedChatMessages`** (new, shared) is the actual fix for 2.1 — extracted from `ToContextMessages`'s local `Admit` closure (now delegates to a new static `AdmitItem` helper, zero behavior change) so `Neo4jChatHistoryProvider`/`Neo4jChatMessageStore` don't each re-implement the same admission+role-gate dance a third and fourth time. `Neo4jChatHistoryProvider.ProvideChatHistoryAsync` was refactored into a thin wrapper around a new internal `PerformProvideAsync(sessionId, userId, ct)` (mirroring the existing `PerformStoreAsync` pattern) specifically so it's unit-testable without constructing MAF's `InvokingContext`.
- Test counts after all fixes: 3041 unit (+35), 54 Semantic Kernel unit (+6), 308 live-Neo4j integration (+4), 0 build warnings.
