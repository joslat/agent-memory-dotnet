namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// Summary of a session including conversation and message counts.
/// </summary>
public sealed record SessionSummary(
    string SessionId,
    int ConversationCount,
    int MessageCount,
    string? LastMessagePreview,
    DateTimeOffset? LastActivity);
