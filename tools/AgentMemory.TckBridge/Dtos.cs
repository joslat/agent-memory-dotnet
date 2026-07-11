namespace AgentMemory.TckBridge;

// Wire contract for the upstream neo4j-labs/agent-memory-tck Bronze bridge protocol.
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
