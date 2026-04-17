namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// Status of a tool call.
/// </summary>
public enum ToolCallStatus
{
    /// <summary>
    /// Tool call is pending execution.
    /// </summary>
    Pending,

    /// <summary>
    /// Tool call completed successfully.
    /// </summary>
    Success,

    /// <summary>
    /// Tool call failed with an error.
    /// </summary>
    Error,

    /// <summary>
    /// Tool call was cancelled.
    /// </summary>
    Cancelled,

    /// <summary>
    /// Tool call failed during execution.
    /// </summary>
    Failure,

    /// <summary>
    /// Tool call timed out.
    /// </summary>
    Timeout
}
