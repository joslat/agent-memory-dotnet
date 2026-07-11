using System.Text.Json;

namespace AgentMemory.TckBridge;

// Wire contract for the upstream neo4j-labs/agent-memory-tck Bronze/Silver bridge protocol.
// Property names are C# PascalCase; ConfigureHttpJsonOptions (Program.cs) maps them to
// snake_case on the wire via JsonNamingPolicy.SnakeCaseLower.

// ---- Responses ----

public sealed record TckMessage(
    string Id,
    string Role,
    string Content,
    DateTimeOffset Timestamp,
    float[]? Embedding,
    IReadOnlyDictionary<string, object> Metadata);

public sealed record TckConversation(
    string Id,
    string SessionId,
    IReadOnlyList<TckMessage> Messages,
    string? Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

// TCK SessionInfo contract (tck/adapters/base_adapter.py TCKSessionInfo): the runner reads exactly
// session_id, message_count, created_at, updated_at — created_at is a required key (the adapter's
// _session_info_from_dict does d["created_at"]) and message_count defaults to 0. Extra fields would be
// ignored by Pydantic, but matching the contract exactly keeps the wire shape honest.
public sealed record TckSessionInfo(
    string SessionId,
    int MessageCount,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt);

// ---- Long-term responses (Bronze schema tier: add_entity / add_preference / add_fact) ----

public sealed record TckEntity(
    string Id,
    string Name,
    string Type,
    string? Subtype,
    string? Description,
    float[]? Embedding,
    string? CanonicalName,
    DateTimeOffset CreatedAt);

public sealed record TckPreference(
    string Id,
    string Category,
    string Preference,
    string? Context,
    float[]? Embedding);

public sealed record TckFact(
    string Id,
    string Subject,
    string Predicate,
    string Object,
    float[]? Embedding);

// TCKRelationship (tck/adapters/base_adapter.py): { id, source_id, target_id, relationship_type, properties }.
// add_relationship is classified Gold in bridge-protocol.adoc, but the Silver get_related_entities tests use
// it to set up their data, so the bridge serves it here.
public sealed record TckRelationship(
    string Id,
    string SourceId,
    string TargetId,
    string RelationshipType,
    IReadOnlyDictionary<string, object> Properties);

// ---- Reasoning responses (Silver tier: start_trace / add_step / record_tool_call / complete_trace /
// get_trace_with_steps / list_traces / get_tool_stats). Field names match the TCK Pydantic models exactly
// (tck/adapters/base_adapter.py TCKReasoningTrace / TCKReasoningStep / TCKToolCall / TCKToolStats).

public sealed record TckReasoningTrace(
    string Id,
    string SessionId,
    string Task,
    IReadOnlyList<TckReasoningStep> Steps,
    string? Outcome,
    bool? Success,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt);

public sealed record TckReasoningStep(
    string Id,
    string TraceId,
    int StepNumber,
    string? Thought,
    string? Action,
    string? Observation,
    IReadOnlyList<TckToolCall> ToolCalls);

// Arguments/Result are wire-level JSON (an object / any JSON value respectively), not JSON-escaped strings —
// the TCK asserts `tc.arguments == {...}` (a dict), so ArgumentsJson/ResultJson (stored as JSON strings on
// the .NET ToolCall domain record) must be re-parsed into a JsonElement before going on the wire (see
// Program.cs ToToolCallDto(ToolCall)).
public sealed record TckToolCall(
    string Id,
    string ToolName,
    JsonElement Arguments,
    JsonElement? Result,
    string Status,
    long? DurationMs,
    string? Error);

public sealed record TckToolStats(
    string Name,
    int TotalCalls,
    int SuccessfulCalls,
    int FailedCalls,
    double SuccessRate,
    double? AvgDurationMs);

// ---- Requests (optional fields nullable; omitted-when-null is a request-side concern only) ----

public sealed record AddMessageRequest(
    string SessionId,
    string Role,
    string Content,
    IReadOnlyDictionary<string, object>? Metadata);

public sealed record GetConversationRequest(string SessionId, int? Limit);

public sealed record SearchMessagesRequest(string Query, string? SessionId, int? Limit, double? Threshold);

public sealed record ListSessionsRequest(int? Limit);

public sealed record DeleteMessageRequest(string MessageId);

public sealed record ClearSessionRequest(string SessionId);

public sealed record AddEntityRequest(string Name, string EntityType, string? Description);

public sealed record AddPreferenceRequest(string Category, string Preference, string? Context);

// The bridge protocol sends the fact object as "obj" (not "object"), so the request property is Obj.
public sealed record AddFactRequest(string Subject, string Predicate, string Obj);

// ---- Long-term memory (Silver tier: search_entities / search_preferences / get_entity_by_name /
// get_related_entities) ----

public sealed record SearchEntitiesRequest(string Query, int? Limit);

public sealed record SearchPreferencesRequest(string Query, string? Category, int? Limit);

public sealed record GetEntityByNameRequest(string Name);

public sealed record GetRelatedEntitiesRequest(string EntityId, string? RelationshipType, int? Depth);

public sealed record AddRelationshipRequest(
    string SourceId,
    string TargetId,
    string RelationshipType,
    IReadOnlyDictionary<string, object>? Properties);

// ---- Reasoning memory (Silver tier) ----

public sealed record StartTraceRequest(string SessionId, string Task);

public sealed record AddStepRequest(string TraceId, string? Thought, string? Action, string? Observation);

// Arguments is always present on the wire (BaseAdapter.record_tool_call requires it, defaulting to {} when
// the caller passes nothing); Result is `Any = None` so it may be any JSON value, including absent.
public sealed record RecordToolCallRequest(
    string StepId,
    string ToolName,
    JsonElement Arguments,
    JsonElement? Result,
    string? Status,
    long? DurationMs,
    string? Error);

public sealed record CompleteTraceRequest(string TraceId, string? Outcome, bool? Success);

public sealed record GetTraceWithStepsRequest(string TraceId);

public sealed record ListTracesRequest(string? SessionId, int? Limit);

public sealed record GetToolStatsRequest(string? ToolName);
