using Neo4j.AgentMemory.Abstractions.Options;

namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// Request for recalling memory context.
/// </summary>
public sealed record RecallRequest
{
    /// <summary>
    /// Session identifier.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Optional user identifier.
    /// </summary>
    public string? UserId { get; init; }

    /// <summary>
    /// Current user query or message.
    /// </summary>
    public required string Query { get; init; }

    /// <summary>
    /// Optional query embedding for semantic search.
    /// </summary>
    public float[]? QueryEmbedding { get; init; }

    /// <summary>
    /// Recall options.
    /// </summary>
    public RecallOptions Options { get; init; } = RecallOptions.Default;
}
