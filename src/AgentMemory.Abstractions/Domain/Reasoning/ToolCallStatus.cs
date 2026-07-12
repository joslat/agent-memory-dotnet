namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// Status of a tool call. Explicit ordinals pin the wire/storage-adjacent values (the status is persisted
/// by enum <i>name</i>, so these are stable regardless). <see cref="Error"/>, <see cref="Failure"/>, and
/// <see cref="Timeout"/> all classify as "failed" in tool-usage stats.
/// </summary>
public enum ToolCallStatus
{
    /// <summary>Tool call is pending execution.</summary>
    Pending = 0,

    /// <summary>Tool call completed successfully.</summary>
    Success = 1,

    /// <summary>Tool call failed because it raised an error/exception during invocation.</summary>
    Error = 2,

    /// <summary>Tool call was cancelled before completing.</summary>
    Cancelled = 3,

    /// <summary>Tool call ran but returned an unsuccessful result (a handled failure, no exception).</summary>
    Failure = 4,

    /// <summary>Tool call exceeded its time limit.</summary>
    Timeout = 5
}
