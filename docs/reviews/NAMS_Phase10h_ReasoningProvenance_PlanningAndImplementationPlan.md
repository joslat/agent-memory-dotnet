# NAMS Phase 10h: Reasoning & Provenance — Planning & Implementation Plan

## Scope

Add the TCK Platinum reasoning/provenance capabilities to `INamsClient` — an entirely new domain
`INamsClient` has never touched before (repeatedly flagged across Phase 4/5/10a docs as "no
reasoning-endpoint method yet"):

- `record_step` (`POST /reasoning/steps`)
- list steps (`GET /reasoning/steps?conversation_id=`) — not itself a named Platinum scenario, but the
  natural read counterpart to `record_step`, already fully shape-confirmed, and needed to make
  `record_step` testable without only relying on the trace endpoint
- `record_tool_call` (`POST /reasoning/tool-calls`)
- `get_trace_by_conversation` (`GET /reasoning/trace/{conversationId}`)
- `get_provenance` (`GET /reasoning/provenance/{entityId}`)

Same tier as every other Phase 10e-10g addition: low-level `INamsClient` capability only, not wired into
any higher-level service or MCP tool.

## Design, informed by the Phase 10e live-probe spike

All five shapes were already confirmed live in Phase 10e — no new probing needed before implementing.
Two of them (`record_step`'s response and the list/trace responses) share one underlying record, since
NAMS returns strict subsets of the same fields depending on which endpoint is called (matches how
`NamsMessage` already covers multiple endpoints' near-identical shapes in this codebase):

```csharp
internal sealed record NamsReasoningStep(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("conversationId")] string? ConversationId,
    [property: JsonPropertyName("reasoning")] string Reasoning,
    [property: JsonPropertyName("actionTaken")] string ActionTaken,
    [property: JsonPropertyName("result")] string? Result,
    [property: JsonPropertyName("createdAt")] string? CreatedAt);
```

(`record_step`'s own response omits `createdAt`; the list/trace endpoints include it — nullable covers
both without a second near-duplicate type.)

```csharp
internal sealed record NamsToolCall(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("stepId")] string? StepId,
    [property: JsonPropertyName("toolName")] string ToolName,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("input")] string? Input,
    [property: JsonPropertyName("output")] string? Output,
    [property: JsonPropertyName("durationMs")] int? DurationMs,
    [property: JsonPropertyName("createdAt")] string? CreatedAt);
```

(Same reasoning: `record_tool_call`'s own response only echoes `{id, stepId, toolName, status}`; the
trace endpoint's `toolCalls` array includes the rest.)

```csharp
internal sealed record NamsReasoningTrace(
    [property: JsonPropertyName("conversationId")] string ConversationId,
    [property: JsonPropertyName("steps")] IReadOnlyList<NamsReasoningStep> Steps,
    [property: JsonPropertyName("toolCalls")] IReadOnlyList<NamsToolCall> ToolCalls);
```

### Provenance — genuinely unconfirmed item shape, modeled honestly

The Phase 10e probe confirmed the **envelope** field name (`provenance`, not `steps` as the pinned
snapshot's schema name wrongly implied) but never observed a **non-empty** provenance array — the probe
queried an entity with no recorded reasoning link, so the shape of an individual provenance entry is
still unconfirmed. The pinned OpenAPI snapshot itself only documents the array items as
`additionalProperties: true` (untyped). Rather than guess a concrete shape that might not match reality —
exactly the mistake the pinned snapshot already made once on the envelope field name — provenance entries
are modeled as raw `JsonElement`:

```csharp
internal sealed record NamsEntityProvenance(
    [property: JsonPropertyName("entityId")] string EntityId,
    [property: JsonPropertyName("provenance")] IReadOnlyList<JsonElement> Provenance);
```

If a later phase confirms the entry shape live, it can be tightened then without breaking callers (they
already only see an opaque, correctly-enveloped list).

### Test design: steps/tool-calls/trace are direct writes (genuinely testable); provenance is not

Unlike Phase 10f's observations (which depend on an async, non-deterministic server-side worker),
`record_step`/`record_tool_call`/`get_trace` are **direct, synchronous writes and reads** — recording a
step and immediately listing/tracing it back is not subject to any eventual-consistency delay (confirmed
live in the Phase 10e probe: step recorded and immediately visible via both list and trace, no polling
needed). These get genuine positive-assertion live tests.

`get_provenance`, by contrast, links reasoning to *entity extraction* — an async, worker-driven process
(the same family as Phase 10f's observations). Forcing a non-empty provenance result would mean
recording reasoning steps and then waiting for NAMS's backend to (a) extract an entity and (b) link it
back to those steps, an even less certain and slower chain than the observations timing that already
failed a real test run in Phase 10f. Per that phase's hard-won lesson, this phase does **not** attempt to
force a positive provenance result — only a shape/wiring test against an existing entity (empty result,
correctly typed, no error).

## Implementation checklist

1. New domain records: `NamsReasoningStep`, `NamsToolCall`, `NamsReasoningTrace`, `NamsEntityProvenance`
   (`src/AgentMemory.Nams/Domain/`, one file per record).
2. `INamsClient`: add `RecordReasoningStepAsync`, `ListReasoningStepsAsync`, `RecordToolCallAsync`,
   `GetReasoningTraceAsync`, `GetEntityProvenanceAsync`, each with a doc comment following the
   established convention.
3. `Neo4jNamsClientAdapter`: implement all five. Recording a step/tool-call is a genuine write
   (`isIdempotent: false`, matches `CreateConversationAsync`/`AddMessagesAsync` — resending would create
   a duplicate step/tool-call, unlike Phase 10g's PUT feedback). Listing steps, getting the trace, and
   getting provenance are reads (`isIdempotent: true`).
4. Live tests (`tests/AgentMemory.Tests.Integration/Nams/NamsReasoningTests.cs`, new file,
   `LiveNamsFactAttribute`-gated):
   - `RecordReasoningStepAsync_ThenListReasoningStepsAsync_ReturnsTheRecordedStep`: create a conversation,
     record a step with distinctive reasoning/action text, list steps for that conversation, assert the
     recorded step appears with matching content — genuine because it's a fresh conversation with exactly
     one step, no pre-existing data to accidentally match against.
   - `RecordToolCallAsync_LinkedToAStep_AppearsInTheTrace`: record a step, record a tool call linked to
     it (distinctive tool name/input), fetch the trace, assert both the step and the tool call appear
     with matching content and the tool call's `StepId` matches the recorded step's id.
   - `GetEntityProvenanceAsync_OnExistingEntity_ReturnsWellTypedResult`: use `ListEntitiesAsync` (Phase 9)
     to find an existing entity, call `GetEntityProvenanceAsync`, assert it returns without throwing and
     the envelope's `EntityId` echoes the queried id — deliberately not asserting non-empty `Provenance`,
     per the design note above.
   - All tests delete their conversations afterward via the existing `DeleteConversationAsync` for
     workspace hygiene.
5. Unit-level wire-shape tests in `Neo4jNamsClientAdapterTests.cs` for all five methods, using the exact
   confirmed JSON shapes above.
