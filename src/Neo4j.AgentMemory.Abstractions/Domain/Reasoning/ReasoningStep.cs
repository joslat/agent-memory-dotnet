namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// Represents a single step in a reasoning trace.
/// </summary>
public sealed record ReasoningStep
{
    /// <summary>
    /// Unique identifier for the step.
    /// </summary>
    public required string StepId { get; init; }

    /// <summary>
    /// Identifier of the trace this step belongs to.
    /// </summary>
    public required string TraceId { get; init; }

    /// <summary>
    /// Sequential step number within the trace.
    /// </summary>
    public required int StepNumber { get; init; }

    /// <summary>
    /// Optional thought or reasoning for this step.
    /// </summary>
    public string? Thought { get; init; }

    /// <summary>
    /// Optional action taken in this step.
    /// </summary>
    public string? Action { get; init; }

    /// <summary>
    /// Optional observation or result of the action.
    /// </summary>
    public string? Observation { get; init; }

    /// <summary>
    /// Optional embedding vector for step similarity search.
    /// </summary>
    public float[]? Embedding { get; init; }

    /// <summary>
    /// Additional metadata.
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } =
        new Dictionary<string, object>();
}
