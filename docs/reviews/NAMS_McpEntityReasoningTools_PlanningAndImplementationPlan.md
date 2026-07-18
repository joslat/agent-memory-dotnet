# NAMS MCP Tools: Entity and Reasoning Operations — Planning & Implementation Plan

## Purpose

`INamsClient` grew from 7 to 21 operations across the Phase 10e-10j / TCK-bridge push (#145-153), but
`AgentMemory.McpServer.Nams` still only exposes the original two tools from Phase 8 (`nams_recall`,
`nams_remember`). Every newer operation's own doc comment says "deliberately not wired into any higher-level
service or MCP tool yet -- a separate, later exposure decision." This phase makes that decision for the
operations that are safe to make autonomously, and explicitly defers the one that isn't.

## Scope

**In scope** (9 new tools, all "administrative/introspection" operations -- entity graph/feedback/creation,
reasoning steps/tool calls/traces/provenance):

| Tool | `INamsClient` method | Tier |
|---|---|---|
| `nams_entity_graph` | `GetEntityGraphAsync` | read |
| `nams_expand_graph` | `ExpandGraphAsync` | read |
| `nams_create_entity` | `CreateEntityAsync` | write |
| `nams_entity_feedback` | `SetEntityFeedbackAsync` | write |
| `nams_list_reasoning_steps` | `ListReasoningStepsAsync` | read |
| `nams_reasoning_trace` | `GetReasoningTraceAsync` | read |
| `nams_entity_provenance` | `GetEntityProvenanceAsync` | read |
| `nams_record_reasoning_step` | `RecordReasoningStepAsync` | write |
| `nams_record_tool_call` | `RecordToolCallAsync` | write |

**Deliberately OUT of scope: `nams_graph_query` (`ExecuteCypherQueryAsync`).** `INamsClient`'s own doc comment
on this method says it "is the last TCK Platinum capability added deliberately behind explicit user approval
(like Phase 12) rather than the general autonomous-execution authorization... exposing this to an
agent/end-user is a separate, later decision from adding the client capability itself." That gate was never
lifted for the MCP-exposure decision specifically -- only for adding the client method. Building an MCP tool
that lets an LLM agent execute arbitrary (server-enforced-read-only) Cypher is a materially different risk
than a C# caller doing so, and deserves the same explicit go-ahead Phase 10i itself required. Not built here;
flagged to the user as a follow-up decision.

## Why these 9 are safe for autonomous execution

Unlike `nams_recall`/`nams_remember`, these 9 operations are workspace-metadata and provenance-trail
operations (create/score an entity, inspect the entity graph, record/read a reasoning trail) with no bearing
on the #92 trust-boundary threat model -- with one caught-and-fixed exception. **Self-review found that
`nams_expand_graph` is NOT actually exempt**: `ExpandGraphAsync` can surface non-Entity nodes, and Phase 10e's
live probe confirmed a `Message` node can appear with raw, unescaped conversation content in its `properties`
bag -- the exact class of untrusted content `NamsRecalledItem`'s own SECURITY doc comment says must never
reach a model without admission/delimiting. Fixed by eliding `properties` for any node labeled `"Message"`
before this tool's response is built (see `NamsEntityReadTools.NamsExpandGraph`'s own SECURITY comment) --
id/labels alone are harmless metadata, so the tool's main use case (exploring the entity-graph neighborhood)
still works. Every other one of the 9 tools matches the risk tier of the direct backend's own `EntityTools`/
`ReasoningTools`/`GraphQueryTools` (`AgentMemory.McpServer`), which are already shipped and ungated beyond the
write/read split (`GraphQueryTools` itself IS separately gated, behind `AgentMemoryMcpOptions.EnableGraphQuery`
-- consistent with excluding `nams_graph_query` above).

## Design decisions

1. **`INamsClient` accessed directly from the new tool classes, not through a new public service.** The
   existing pattern for `nams_recall`/`nams_remember` is to go through `INamsRecallService`/
   `INamsPersistenceService` -- but those exist specifically *because* Phase 4-6 needed a security-gating
   layer between raw NAMS data and an LLM context, not as a generic API-layering convention. These 9
   operations don't need that layer, and the codebase already has a precedent for a narrow, justified
   `InternalsVisibleTo` grant straight to `INamsClient` when a whole service layer isn't warranted:
   `AgentMemory.TckBridge.Nams` (#153) does exactly this. Adds one more explicit grant line to
   `AgentMemory.Nams.csproj`, following the same pattern. Deliberately avoids committing new public API
   surface (a real design cost under this repo's SemVer lock) for a thin MCP-tool-only need.
2. **Split into 4 new tool classes by read/write tier**, matching `AddNamsAgentMemoryMcpTools`/
   `AddNamsAgentMemoryMcpWriteTools`'s existing two-method opt-in split (an `[McpServerToolType]` class is the
   unit `WithTools<T>()` registers, so tier separation has to happen at the class boundary):
   `NamsEntityReadTools`, `NamsEntityWriteTools`, `NamsReasoningReadTools`, `NamsReasoningWriteTools`. Both
   existing extension methods are extended to also register the new classes for their tier -- no new
   extension method, no new opt-in surface for the host to learn.
3. **`nams_expand_graph`'s `loadedIds` parameter takes a comma-separated string, not a list.** No existing MCP
   tool in either package takes a list/array-typed parameter; rather than being the first to find out whether
   the MCP SDK's schema generation handles that cleanly, this follows the codebase's own established
   "keep tool parameters to primitives" pattern and splits internally.
4. **Explicit field projection, not raw domain-record passthrough**, matching `NamsRecallTools`/
   `NamsPersistenceTools`'s own style -- protects the MCP wire contract from silently changing if a domain
   record's shape changes, and keeps `NamsMcpToolJson`'s camelCase output intentional rather than incidental.
   Most Nams domain records used here happen to already carry explicit camelCase `[JsonPropertyName]`
   attributes, so passthrough would mostly coincide with the intended output anyway -- but not universally:
   `NamsCreateEntityResult.DuplicateOf`/`MergedInto` are explicitly `snake_case` (`duplicate_of`/`merged_into`,
   NAMS's own real wire inconsistency, see that record's doc comment), which passthrough would have leaked
   verbatim. Explicit projection avoids that class of bug entirely, not just here but for any future field this
   package projects.
5. **Same defensive-argument-validation discipline as the existing tools**: every string/id parameter is
   checked for null/whitespace and returns a clean JSON `{ "error": "..." }` response rather than throwing,
   since MCP argument binding for a plain `string` parameter isn't guaranteed to reject a missing/null value
   before the tool body runs.

## Test plan

- New `NamsMcpToolRegistryTests` assertions covering all 11 tools (2 existing + 9 new) and their
  read/write classification.
- New `NamsEntityToolsTests.cs`/`NamsReasoningToolsTests.cs` unit tests using the existing
  `ThrowingNamsClientStub` pattern (`tests/AgentMemory.Tests.Unit/Nams/ThrowingNamsClientStub.cs`), covering
  the happy path and every validation-error path for each of the 9 new tool methods -- closing a pre-existing
  gap where `NamsRecallTools`/`NamsPersistenceTools` themselves (as opposed to the services they call) had no
  direct unit test coverage.
- Full `dotnet build AgentMemory.slnx -c Release` (0 warnings) + full unit suite before merge.
- No live-NAMS integration test added for this phase -- these are thin wrappers around already-live-tested
  `INamsClient` methods (Phase 10e-10j), so the coverage that matters (does the real NAMS endpoint behave as
  documented) already exists at the client layer; this phase only tests the wrapping/projection logic.

## Self-review result

Two parallel self-review agents (correctness+security, conventions) found 2 real, fixed issues, both covered
above in "Why these 9 are safe for autonomous execution" and the design-decision notes:

1. **Security (substantive): `nams_expand_graph` could leak raw message content.** Caught before merge --
   fixed by eliding `properties` for any `"Message"`-labeled node. New regression test:
   `NamsExpandGraph_MessageLabeledNode_ElidesProperties`.
2. **Convention (minor): `nams_entity_feedback`'s error response dropped the `updated` key** that its success
   path always includes, unlike `nams_remember`'s established `persisted`-present-on-both-paths precedent.
   Fixed; test updated to assert `updated: false` on the validation-error path.

No other findings survived review -- parameter order/types, JSON projections, `Truncated`/`Provenance`
null-handling, `ConfigureAwait` consistency, and the deliberate non-reference of `ExecuteCypherQueryAsync`
were all independently confirmed correct.
