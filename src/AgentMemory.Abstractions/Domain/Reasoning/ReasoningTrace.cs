namespace AgentMemory.Abstractions.Domain;

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
    /// Optional owner/user id that scopes this trace. Null means shared/global. See
    /// <c>MemoryScope</c> and docs/archive/Memory_Review_and_Implementation_Plan.md (R1/R2).
    /// </summary>
    public string? OwnerId { get; init; }

    /// <summary>
    /// Whether this trace is an ordinary episode or a promoted, reusable procedure.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="TraceKind.Episode"/>, which is what every existing trace is, so nothing
    /// changes for a store written before this existed.
    /// </remarks>
    public TraceKind Kind { get; init; } = TraceKind.Episode;

    /// <summary>
    /// Additional metadata.
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } =
        new Dictionary<string, object>();
}
