namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// Represents session-level information for memory scoping.
/// </summary>
public sealed record SessionInfo
{
    /// <summary>
    /// Session identifier.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Optional user identifier associated with this session.
    /// </summary>
    public string? UserId { get; init; }

    /// <summary>
    /// UTC timestamp when the session started.
    /// </summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>
    /// UTC timestamp when the session ended, if applicable.
    /// </summary>
    public DateTimeOffset? EndedAtUtc { get; init; }

    /// <summary>
    /// Session-level metadata.
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } =
        new Dictionary<string, object>();
}
