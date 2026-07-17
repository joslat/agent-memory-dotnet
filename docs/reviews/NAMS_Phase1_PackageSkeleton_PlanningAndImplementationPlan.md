# NAMS Phase 1 — Package Skeleton: Planning and Implementation Plan

**Prepared:** 2026-07-17
**Branch:** `nams/phase1-package-skeleton`
**Purpose:** Phase 2 (final phase) of a 3-phase plan (stabilization [#127] → NAMS Phase 0 baseline freeze [#129] → **this phase**). Executes the "Phase 1: Additive package skeleton" section of `strategy/AgentMemory_NAMS_Backend_Engineering_Plan_V03.md` (§7).

---

## 1. Task and scope

Create `AgentMemory.Nams` — an empty-of-behavior, additive package containing only configuration surface (options, validation, DI registration, a backend-identity descriptor). **No HTTP client, no NAMS API calls, no recall/persistence logic** — that's Phase 2+ of the *engineering plan itself* (not this repo-planning phase), gated on Neo4j answering the questions in issue #128 first.

Per the plan's own Phase 1 spec:

| Plan requirement | Plan text | This implementation |
|---|---|---|
| Planned files | `AgentMemory.Nams/{AgentMemory.Nams.csproj, NamsOptions.cs, NamsServiceCollectionExtensions.cs, NamsBackendDescriptor.cs, Internal/NamsOptionValidator.cs}` | Same, plus `NamsPersistenceFailureMode.cs` (the enum `NamsOptions.PersistenceFailureMode` needs, referenced but not defined in the plan's own snippet) |
| Test files | `tests/.../Nams/{NamsOptionsTests.cs, NamsServiceCollectionExtensionsTests.cs}` | Same |
| Nullable enabled, consistent warning policy | — | Inherited for free from `Directory.Build.props` (any `AgentMemory.*` non-test/non-sample project gets `Nullable=enable`, `TreatWarningsAsErrors=true`, net8/9/10 multi-targeting, and full NuGet packaging metadata automatically, by naming convention alone — verified by reading the file, not assumed) |
| No reference from Core/Neo4j/Abstractions back to NAMS | — | **Verified already enforced, no test change needed** (see §2 below) — a real correction to the plan's own text, which assumed a test update was required |
| No registration unless `AddNamsAgentMemory` is called | — | Only extension method touches DI; nothing else in the repo references `AgentMemory.Nams` |
| Credentials via a secure abstraction, not a long-lived options object | "Credentials should preferably be supplied through a token provider..." | Deferred: Phase 1 needs *some* place to hold an API key for the eventual HTTP client (Phase 2 of the engineering plan), and the plan's own suggested `NamsOptions` snippet includes `ApiKey` directly. Kept as `string?` for now (matches the plan's own snippet exactly), with the "no secrets in ToString/logs" requirement satisfied structurally (plain class, not a record — see §3) rather than deferring the property entirely. A token-provider abstraction is real future work, not a Phase 1 blocker. |

### Explicitly out of scope for this phase

- `INamsClient`, `Neo4jNamsClientAdapter`, any HTTP call, any reference to the `Neo4jAgentMemory` TCK C# client (that's the engineering plan's own Phase 2, which needs Tier-1 Neo4j answers from issue #128 first — this repo-planning phase does not advance the engineering plan past its own Phase 0 gate).
- ~~Adding `AgentMemory.Nams` to `eng/release-packages.txt`~~ **Correction (CI caught this, not the plan): the manifest is a mandatory consistency-checked inventory of every `src/*` directory, not an opt-in publish gate.** `.github/workflows/ci.yml`/`release.yml`'s own "Repository consistency checks" step fails the build if any `src/*` directory is absent from this file — confirmed by a real CI failure on this PR, not by re-reading the workflow first. `AgentMemory.Nams` is now listed. Being listed means it WOULD be packed and pushed to NuGet at whatever the next real release tag is — readiness for that is a maintainer judgment call about *when to cut a release*, not something the manifest itself gates. The package's own `Description` (in its `.csproj`) already discloses it's a configuration-surface-only skeleton, so even a premature publish is honestly labeled.
- `NamsMemoryContextProvider` / any MAF integration (engineering plan Phase 6).

## 2. Correction to the plan's own text (found while planning, not after implementing)

The engineering plan states (§3.2): *"Phase 1's 'no reference from Core, Neo4j, or Abstractions back to NAMS' requirement is therefore not a new rule to invent; it is an existing, automated one that needs `AgentMemory.Nams` added to its allowed/forbidden package lists, and Phase 1's exit criteria should say so explicitly."*

Read both existing guard tests before writing any code:

- `tests/AgentMemory.Tests.Unit/Infrastructure/PackageBoundaryGuardTests.cs`: `Core`/`Neo4j`'s `AllowedInternalReferences` are explicit allow-lists (`["AgentMemory.Abstractions"]` / `["AgentMemory.Abstractions", "AgentMemory.Core"]`). Any sibling `AgentMemory.*` reference NOT in that list — including a future `AgentMemory.Nams` — already fails `CompiledReferences_HonorBoundary`/`Csproj_HonorsBoundary` today, with zero changes. This is an allow-list, not a name-specific deny-list; it needs no edit to cover a package that doesn't exist yet.
- `tests/AgentMemory.Tests.Unit/Infrastructure/AbstractionsContractGuardTests.cs` line 76: `referenced.Should().NotContain(n => n.StartsWith("AgentMemory.", ...))` — a blanket rule, already covers Nams the same way.

**Conclusion: no changes needed to either guard test file to satisfy this specific requirement.** Documenting this here (mirroring the stabilization phase's own practice of correcting a plan/audit claim before acting on it rather than implementing a fix for a problem that doesn't exist).

What *is* worth adding proactively (not required by the plan's text, but a natural extension of the existing pattern): a **new** `PackageBoundaryGuardTests` row for `AgentMemory.Nams` itself, encoding the boundary this package should hold going forward — no `Neo4j.Driver`, no framework-adapter SDKs, and (for this phase) no project references at all, since the skeleton is self-contained. This gives Phase 2+ (client adapter) an automated tripwire the moment it's tempted to reach into `Core`/`Neo4j` internals instead of going through whatever narrow contract eventually gets designed (ADR-3).

## 3. Detailed design

- **`NamsOptions`** — a plain `sealed class` (not a `record`), matching most option types in this repo. This is a deliberate security choice, not just style consistency: a `record`'s compiler-generated `ToString()` prints every property including `ApiKey`; a plain class's inherited `object.ToString()` does not. Shape matches the plan's own suggested snippet (`Endpoint`, `ApiKey`, `WorkspaceId`, `RequestTimeout`, `MaxRetryAttempts`, `InitialRetryDelay`, `PersistenceFailureMode`), plus one addition: `AllowInsecureEndpointForLocalDevelopment` (bool, default `false`) — the explicit escape hatch the plan's own test list requires ("non-HTTPS endpoint emits validation failure outside an explicitly enabled local-development mode").
- **`NamsPersistenceFailureMode`** — `BestEffort` (default) / `FailInvocation`, matching the plan's Phase 5 design (referenced now so the options shape is stable, even though nothing reads it yet).
- **`Internal/NamsOptionValidator`** — static predicate methods (not an `IValidateOptions<T>` class), matching this repo's established convention of fluent `.Validate()` chains in `*ServiceCollectionExtensions.AddX` methods rather than a dedicated validator class per options type (every other package in this repo does it this way — `Neo4jOptions`, `AgentMemoryMcpOptions`, `ContextFormatOptions`, etc., several of which the stabilization phase just extended). The plan's file list names `NamsOptionValidator.cs`; keeping that file name while using the repo's actual established pattern (extracted predicates the fluent chain calls into) satisfies both the plan's structure and this repo's conventions, rather than introducing a second competing validation idiom.
- **`NamsBackendDescriptor`** — a minimal, NAMS-only identity record (`Name = "nams"`, `DisplayName`). Deliberately does **not** import or anticipate the engineering plan's own later `MemoryBackendDescriptor`/`MemoryBackendCapabilities` (§5.6, Phase 7 of the engineering plan) — designing that shared, backend-neutral shape now, before a second real backend's behavior has ever been observed, is exactly what ADR-4 warns against one level up (don't refactor/generalize before you've built the thing you'd be generalizing from).
- **`NamsServiceCollectionExtensions.AddNamsAgentMemory`** — registers `IOptions<NamsOptions>` (configured + validated + `ValidateOnStart`) and a singleton `NamsBackendDescriptor`. Nothing else. `ArgumentNullException.ThrowIfNull` on both parameters, matching `AddGraphRagAdapter`'s existing style in `AgentMemory.Neo4j`.

## 4. Tests (mirrors the plan's own Phase 1 list exactly)

| Plan's requirement | Test |
|---|---|
| Missing endpoint fails validation | `AddNamsAgentMemory_MissingEndpoint_FailsValidation` |
| Non-HTTPS fails outside local-dev mode | `AddNamsAgentMemory_HttpEndpoint_FailsValidation` / `..._HttpEndpoint_AllowedWhenLocalDevelopmentFlagSet_PassesValidation` |
| Negative timeout/retry values fail | One test per field (`RequestTimeout`, `MaxRetryAttempts`, `InitialRetryDelay`) |
| Registration is idempotent | `AddNamsAgentMemory_CalledTwice_DoesNotThrow` |
| Direct Neo4j services are not registered | `AddNamsAgentMemory_DoesNotRegisterDirectNeo4jServices` |
| NAMS services are absent without opt-in | `NamsBackendDescriptor_NotRegistered_WithoutAddNamsAgentMemory` |
| Secrets not in ToString/logs/exceptions/validation text | `NamsOptions_ToString_DoesNotContainApiKey`, plus asserting every validation failure message string doesn't contain the configured `ApiKey`/`WorkspaceId` value |

## 5. Definition of done

- [x] `src/AgentMemory.Nams/` created with the 6 files above (`AgentMemory.Nams.csproj`, `NamsOptions.cs`, `NamsPersistenceFailureMode.cs`, `NamsBackendDescriptor.cs`, `NamsServiceCollectionExtensions.cs`, `Internal/NamsOptionValidator.cs`), builds clean (0 warnings) on net8.0/net9.0/net10.0.
- [x] Added to `AgentMemory.slnx`.
- [x] New `PackageBoundaryGuardTests` row for `AgentMemory.Nams` (no Neo4j.Driver, no framework SDKs, no project references).
- [x] All plan-mandated tests pass (17 new in `tests/AgentMemory.Tests.Unit/Nams/`); full existing unit/SK/integration suites remain green: **3060 unit (+19, includes 2 new `PackageBoundaryGuardTests` theory cases) / 54 SK unit / 308 live-Neo4j integration**, 0 build warnings.
- [x] `dotnet pack` succeeds for the new package (`AgentMemory.Nams.1.2.0.nupkg` + `.snupkg` produced cleanly) — verified even though it's not added to the publish manifest yet.
- [x] Self-reviewed via 3 parallel finder agents (this phase touches real source code — a whole new package — so the full review applies, unlike Phase 0's docs-only diff).
- [ ] PR opened, CI green, merged.

## 6. Self-review findings and dispositions

- **Real bug, fixed:** `NamsOptionValidator.HasSecureOrExplicitlyAllowedEndpoint` accessed `Endpoint.Scheme` without checking `Endpoint.IsAbsoluteUri` first. A scheme-less `Uri` (e.g. `new Uri("memory.neo4jlabs.com/v1")`, a plausible copy-paste typo missing `https://`) parses as *relative*, and `.Scheme` throws `InvalidOperationException` on a relative URI — so this exact misconfiguration crashed with a confusing, unrelated exception instead of the package's whole reason for existing: a clean `OptionsValidationException`. Fixed by adding a new `HasAbsoluteEndpoint` rule that the other rule now defers to, plus a regression test (`AddNamsAgentMemory_RelativeEndpoint_FailsValidation_DoesNotThrowUnrelatedException`) that asserts the exception thrown is NOT an `InvalidOperationException`.
- **Convention fix, applied:** `NamsBackendDescriptor` originally used a hand-rolled private-constructor-plus-static-`Instance` singleton — the only such pattern anywhere in this repository's `src/` tree. Every other ambient/identity singleton (`DefaultMemoryOwnerContext`, `DefaultMemoryStoreContext`) uses a public constructor and `services.TryAddSingleton<T>()`, letting DI own the instance. Changed to match; the `AddNamsAgentMemory_CalledTwice_DoesNotThrow` test was updated to verify singleton identity by resolving twice from the same provider rather than comparing against a static field that no longer exists.
- **Doc-hygiene gaps, fixed:** `docs/architecture.md` §5 didn't have a boundary row for the new `AgentMemory.Nams` constraint (added as **B9**, plus a verification bullet), even though `PackageBoundaryGuardTests`'s own class doc-comment says any new rule should update both the test and that section. `docs/specification.md`'s "Current Package Set" list didn't disclose the new 13th `src/` package — added a one-line note.
- **Assessed, not a defect:** `NamsOptions.Endpoint` being `required` doesn't provide compile-time safety in the delegate-configuration usage pattern this package actually uses (`services.AddNamsAgentMemory(o => ...)` never goes through object-initializer syntax, so `required` is enforced by `NamsOptionValidator.HasEndpoint` + `ValidateOnStart` at runtime, not by the compiler). Kept as-is: this exact shape (`required` on a mutable options property, validated at runtime) already exists elsewhere in this repo (`AzureLanguageOptions`) and matches the engineering plan's own suggested snippet verbatim.
- **CI caught a real planning mistake (not found by any self-review agent — found by an actual failing CI run on this PR):** §2's original text asserted `AgentMemory.Nams` should be deliberately left out of `eng/release-packages.txt` since "a skeleton with no working backend has nothing to publish." This was wrong: `ci.yml`/`release.yml`'s "Repository consistency checks" step hard-fails if any `src/*` directory is absent from that manifest — it's a mandatory inventory, not an opt-in publish gate. Added the entry; corrected every doc that had repeated the original, incorrect framing (this file's §1/§2, `docs/specification.md`, `docs/architecture.md`). This is exactly the kind of thing a full CI run catches that even thorough self-review agents can miss when reasoning from a plan's stated assumption instead of the actually-enforced rule.

Final counts after fixes: 3061 unit (+1 regression test) / 54 Semantic Kernel unit / 308 live-Neo4j integration, 0 build warnings. Package still packs cleanly.
