# NAMS Phase 8 — MCP and Capability-Aware Tools — Planning and Implementation Plan

**Prepared:** 2026-07-17
**Branch:** `nams/phase8-mcp-tools`
**Purpose:** Executes engineering plan Phase 8, the last phase in this session's authorized autonomous
sequence (9 → 10 → 8). Explicitly optional per the plan's own text ("optional for initial preview").

## 1. Plan deviation, documented up front

The plan's own text says "NAMS tools delegate through `IAgentMemoryBackend`, not directly to REST." That
type doesn't exist -- it's a Phase 7 (backend-neutral convergence) concept, and Phase 7 is explicitly not
being started (its own entry gate isn't met; see `strategy/NAMS/NAMS_STATUS_AND_NEXT_STEPS.md`). Substituted
with the equivalent that DOES exist: tools delegate through the public `INamsRecallService`/
`INamsPersistenceService` interfaces -- the same interfaces `NamsMemoryContextProvider` (Phase 6) uses, never
`INamsClient`/raw REST directly. This satisfies the actual intent (no tool bypasses the service layer to
hit the wire itself) without inventing the Phase 7 abstraction early.

## 2. New package: `AgentMemory.McpServer.Nams` (B11)

Isolated in its own package for the same reason as B9/B10: `AgentMemory.Nams` can't take on a
`ModelContextProtocol` dependency (B9), and this package has no reason to depend on MAF (`Microsoft.Agents.*`)
at all -- unlike B10, that's forbidden here, not allowed. References only `AgentMemory.Nams` + the
`ModelContextProtocol` SDK.

### Tools

- **`nams_recall`** (read) -- wraps `INamsRecallService.RecallAsync`. Always registered by
  `AddNamsAgentMemoryMcpTools()`.
- **`nams_remember`** (write) -- wraps `INamsPersistenceService.PersistTurnAsync` for a single message.
  Registered **only** by a separate, explicit `AddNamsAgentMemoryMcpWriteTools()` call -- never bundled into
  the read registration, matching the plan's "write tools are never automatically enabled" rule exactly.
  Rejects any `role` other than `"user"`/`"assistant"` before ever calling the persistence service (no
  `"system"`/`"tool"` role can be smuggled through this tool).

### "Identity ambient, never a free model argument" -- how this is actually satisfied

The plan's literal text asks for ambient identity. The **existing, shipped** direct-backend MCP tools
(`EntityTools`, etc.) don't do this today -- they accept an optional `userId` string parameter, with the
production checklist documenting host-side validation as the real mitigation. Rather than either copying
that precedent (weaker) or building genuine ambient-identity plumbing through an MCP request (a bigger
infrastructure piece with no existing precedent to reuse), both NAMS tools take an explicit
**`namsConversationId`** parameter instead of `userId`/`workspaceId`. A conversation ID is an opaque,
already-resolved per-conversation handle (produced by `INamsConversationResolver`, itself driven by
trusted host-supplied session identity) -- not a tenant/workspace selector a model could use to reach
another user's data the way a raw `userId` theoretically could. Verified structurally: both tool methods'
parameter lists are reflected over in tests to confirm neither `userId` nor `workspace` appears anywhere in
their signatures.

### Capability matrix

`NamsMcpToolDescriptor { Name, IsWriteTool, Func<bool> IsSupported }` + `NamsMcpToolRegistry.AllTools`/
`SupportedToolNames` -- the plan's own shape (`AgentMemoryToolDescriptor { Name, Func<capabilities, bool>
IsSupported} `), scoped down since NAMS has no partial-capability tiers today (both tools are
unconditionally available once `AddNamsAgentMemory()` is registered). `IsSupported` stays a delegate, not a
hardcoded `true`, so a real predicate can be plugged in later without changing the shape -- deliberately not
unified with the direct backend's own (differently-shaped, always-all-33-tools) MCP registration mechanism;
that consolidation belongs with Phase 7 convergence, not bolted on here.

## 3. Explicitly out of scope

- **`search_messages`/reasoning-trace/tool-call-recording tools** -- `INamsClient` doesn't expose these
  operations yet (Phase 2 deliberately dropped `SearchMessagesAsync` pending confirmation; now confirmed
  live per `NAMS_LiveValidationAndIntegrationTestScaffold_PlanningAndImplementationPlan.md`, but adding the
  client method itself is a separate, smaller follow-up, not bundled into this MCP-tools phase).
- **Unifying with the direct backend's existing MCP tool/capability mechanism** -- a bigger refactor of
  stable, shipped code; belongs with Phase 7 if it ever happens.
- **MCP-vs-lifecycle distinguishable telemetry** (plan's own Phase 8 test list item) -- Phase 9's metrics
  already tag every operation by name (`resolve_conversation`, `get_context`, `store_turn`, etc.); an MCP
  tool call reaching `INamsRecallService`/`INamsPersistenceService` emits the exact same metrics as the
  automatic path today, which are already distinguishable by caller if a host wants to add its own
  MCP-specific span around the tool invocation. Not adding NAMS-tool-specific metrics in this pass -- there
  are only 2 tools, and the automatic-path metrics already prove the underlying operations are observable.

## 4. Self-review findings and fixes

3 parallel reviewers (correctness / cross-file impact / cleanup-conventions), matching this session's pattern:

- **Correctness (1 fixed):** neither tool method guarded its `string` parameters against null/empty/
  whitespace before use -- a malformed or adversarial MCP call (argument binding isn't guaranteed to enforce
  non-null for a plain `string` parameter) could throw an unhandled exception out of the tool invocation
  instead of the intended clean `{ error: "..." }` JSON response. Fixed in both `NamsRecallTools.NamsRecall`
  and `NamsPersistenceTools.NamsRemember` with explicit `string.IsNullOrWhiteSpace` guards returning a clean
  error response; added 2 new test methods (7 additional test results) covering null/empty/whitespace for
  every required argument.
- **Cross-file impact (0 fixed, 1 doc fix):** confirmed the B11 boundary rule, `eng/release-packages.txt`
  consistency, `AgentMemory.slnx` entry, `ModelContextProtocol` version alignment (all three consuming
  projects pin `1.2.0`), and zero tool-name collision with the direct backend's 24 existing tools -- all
  clean. Caught this doc's own test-count claim ("16 new") was wrong; corrected below to the actual count.
- **Cleanup/conventions (0 fixed):** one purely cosmetic attribute-line-splitting divergence from
  `EntityTools.cs`'s style noted, not worth changing.

## 5. Verification

- `dotnet build AgentMemory.slnx -c Release` -- 0 warnings, 0 errors.
- `dotnet test tests/AgentMemory.Tests.Unit` -- full suite green, **23 new tests** (7 `NamsRecallToolsTests` +
  13 `NamsPersistenceToolsTests` + 3 `NamsMcpToolRegistryTests`), including the structural no-userId/
  no-workspace guards and the null/empty/whitespace-argument guards added during self-review.

## 6. Definition of done

- [x] `AgentMemory.McpServer.Nams` package built, wired into `AgentMemory.slnx`/`eng/release-packages.txt`.
- [x] Read tool (`nams_recall`) and write tool (`nams_remember`, separately opt-in) implemented and tested.
- [x] B11 boundary rule added and enforced.
- [x] Plan deviation (`IAgentMemoryBackend` substitution) and "ambient identity" design decision documented.
- [x] Self-reviewed and fixes applied.
- [ ] PR opened, CI green, merged to `main`.
