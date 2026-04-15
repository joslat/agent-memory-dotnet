namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// Represents a tool invocation within a reasoning step.
/// </summary>
public sealed record ToolCall
{
    /// <summary>
    /// Unique identifier for the tool call.
    /// </summary>
    public required string ToolCallId { get; init; }

    /// <summary>
    /// Identifier of the step this tool call belongs to.
    /// </summary>
    public required string StepId { get; init; }

    /// <summary>
    /// Name of the tool invoked.
    /// </summary>
    public required string ToolName { get; init; }

    /// <summary>
    /// JSON-serialized arguments passed to the tool.
    /// </summary>
    public required string ArgumentsJson { get; init; }

    /// <summary>
    /// JSON-serialized result from the tool, if available.
    /// </summary>
    public string? ResultJson { get; init; }

    /// <summary>
    /// Status of the tool call.
    /// </summary>
    public required ToolCallStatus Status { get; init; }

    /// <summary>
    /// Duration of the tool call in milliseconds.
    /// </summary>
    public long? DurationMs { get; init; }

    /// <summary>
    /// Error message if the tool call failed.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Optional description of the tool (propagated to the Tool node).
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Additional metadata.
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } =
        new Dictionary<string, object>();
}
