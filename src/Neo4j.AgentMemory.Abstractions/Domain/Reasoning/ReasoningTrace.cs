namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// Represents a reasoning trace for a task or agent run.
/// </summary>
public sealed record ReasoningTrace
{
    /// <summary>
    /// Unique identifier for the trace.
    /// </summary>
    public required string TraceId { get; init; }

    /// <summary>
    /// Session identifier for the trace.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Description of the task being performed.
    /// </summary>
    public required string Task { get; init; }

    /// <summary>
    /// Optional embedding vector for task similarity search.
    /// </summary>
    public float[]? TaskEmbedding { get; init; }

    /// <summary>
    /// Optional outcome description.
    /// </summary>
    public string? Outcome { get; init; }

    /// <summary>
    /// Whether the task was completed successfully.
    /// </summary>
    public bool? Success { get; init; }

    /// <summary>
    /// UTC timestamp when the trace started.
    /// </summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>
    /// UTC timestamp when the trace completed, if applicable.
    /// </summary>
    public DateTimeOffset? CompletedAtUtc { get; init; }

    /// <summary>
    /// Additional metadata.
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } =
        new Dictionary<string, object>();
}
