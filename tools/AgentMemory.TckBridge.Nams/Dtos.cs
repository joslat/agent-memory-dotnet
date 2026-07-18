namespace AgentMemory.TckBridge.Nams;

// Wire contract for the upstream neo4j-labs/agent-memory-tck Platinum bridge protocol. Property names are
// C# PascalCase; ConfigureHttpJsonOptions (Program.cs) maps them to snake_case on the wire via
// JsonNamingPolicy.SnakeCaseLower, matching the direct-backend AgentMemory.TckBridge's own convention.
//
// These are bridge-local translation records, NOT direct reuse of AgentMemory.Nams.Domain types -- see
// docs/reviews/NAMS_TckPlatinumBridge_PlanningAndImplementationPlan.md for the full field-by-field research
// this is based on. None of these reuse a Nams domain record directly, even where a shape looks like it
// matches exactly (e.g. NamsEntityFeedbackResult, NamsReasoningStep, NamsQueryResult): every Nams domain
// record carries explicit JsonPropertyName attributes in NAMS's own camelCase wire casing, which always
// overrides this bridge's snake_case naming policy -- see the IMPORTANT note further down for how that was
// found the hard way.

// ---- Conversations: TCK's _conversation_from_dict requires id/session_id/created_at, none of which map
// 1:1 onto NAMS's conversation model (no session concept; create-response has no created_at at all). ----

internal sealed record TckConversation(
    string Id,
    string SessionId,
    string? Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

// ---- Messages: TCK's _message_from_dict requires "timestamp", NAMS's field is "createdAt". ----

internal sealed record TckMessage(
    string Id,
    string Role,
    string Content,
    DateTimeOffset? Timestamp);

// ---- Context: reflections/observations/recent_messages. TCK's per-item parsers require id/conversation_id;
// content/created_at are optional -- these are exercised as empty lists by the actual Platinum scenarios
// (a fresh conversation has none yet), but modeled correctly rather than left untyped. ----

internal sealed record TckReflection(string Id, string? ConversationId, string? Content, string? CreatedAt);

internal sealed record TckObservationEntry(string Id, string? ConversationId, string? Content, string? CreatedAt);

internal sealed record TckContext(
    IReadOnlyList<TckReflection> Reflections,
    IReadOnlyList<TckObservationEntry> Observations,
    IReadOnlyList<TckMessage> RecentMessages);

// ---- Entities: TCK's _entity_from_dict requires id/name/type/created_at. NAMS's POST /entities response
// NEVER includes created_at (confirmed live, Phase 10j) and, on a "merged" resolution outcome, omits
// name/type entirely -- Program.cs synthesizes created_at always and falls back to the submitted name/type
// when NAMS's own response lacks them. ----

internal sealed record TckEntity(string Id, string Name, string Type, string? Description, DateTimeOffset CreatedAt);

// ---- Entity graph: TCK's edge parser requires id/source/target (not sourceId/targetId). ----

internal sealed record TckGraphNode(string Id, string? Name, string? Type);

internal sealed record TckGraphEdge(string Id, string Source, string Target, string? Type);

internal sealed record TckEntityGraph(IReadOnlyList<TckGraphNode> Nodes, IReadOnlyList<TckGraphEdge> Edges);

// ---- Reasoning trace: TCK's _agent_step_from_dict requires conversation_id on EACH step -- confirmed live
// that NAMS's own /reasoning/trace/{id} response omits conversationId per-step (only the top-level trace
// object has it), so Program.cs patches it onto every step from the request parameter before returning.
// _tool_call_from_dict requires id/tool_name -- NamsToolCall.ToolName carries its own explicit
// JsonPropertyName("toolName") attribute, so (per the IMPORTANT note below) tool calls need the same
// bridge-local treatment as everything else here; see TckToolCall. ----

internal sealed record TckAgentStep(
    string Id, string ConversationId, string? Reasoning, string? ActionTaken, string? Result, string? CreatedAt);

// IMPORTANT: reusing a Nams domain record directly as a bridge response type is NOT safe whenever that
// record has an explicit [JsonPropertyName] attribute -- confirmed the hard way running the real TCK suite:
// System.Text.Json's PropertyNamingPolicy only applies to properties WITHOUT an explicit JsonPropertyName
// attribute; an explicit attribute always wins. Every Nams domain record has explicit camelCase attributes
// matching NAMS's own wire casing (e.g. NamsReasoningStep.ConversationId is tagged "conversationId"), so
// reusing one directly here would silently emit camelCase instead of the snake_case this bridge's naming
// policy is supposed to produce -- exactly what broke record_step/get_trace_by_conversation on the first
// real conformance run. Every bridge-local DTO below deliberately has NO JsonPropertyName attributes of its
// own, so the naming policy applies uniformly.

internal sealed record TckToolCall(string Id, string? StepId, string ToolName, string? Status);

internal sealed record TckTrace(
    string ConversationId, IReadOnlyList<TckAgentStep> Steps, IReadOnlyList<TckToolCall> ToolCalls);

internal sealed record TckAgentStepResult(string Id, string ConversationId, string Reasoning, string ActionTaken, string? Result);

internal sealed record TckEntityFeedbackResult(string Id, bool Updated);

internal sealed record TckCypherResult(
    IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows, IReadOnlyDictionary<string, object?>? Stats);

// ---- Requests (snake_case on the wire via the same naming policy; matches the direct-backend
// AgentMemory.TckBridge's convention of keeping all wire DTOs, both response and request, in this file
// rather than split across Program.cs). ----

internal sealed record CreateConversationRequest(string? UserId, IReadOnlyDictionary<string, string>? Metadata);

internal sealed record ListConversationsRequest(int? Limit);

internal sealed record DeleteConversationRequest(string ConversationId);

internal sealed record BulkMessageInput(string Role, string Content);

internal sealed record BulkAddMessagesRequest(string ConversationId, IReadOnlyList<BulkMessageInput> Messages);

internal sealed record AddMessageRequest(string SessionId, string Role, string Content);

internal sealed record GetContextRequest(string ConversationId);

internal sealed record GetObservationsRequest(string ConversationId, int? Limit);

// Wire key is "entity_type" (matching the TCK client's own add_entity call), not "type".
internal sealed record AddEntityRequest(string Name, string EntityType, string? Description);

internal sealed record SetEntityFeedbackRequest(string EntityId, double? UserScore, bool? Confirmed);

internal sealed record RecordStepRequest(string ConversationId, string Reasoning, string ActionTaken, string? Result);

internal sealed record GetTraceRequest(string ConversationId);

internal sealed record CypherQueryRequest(string Cypher, IReadOnlyDictionary<string, object?>? Params);
