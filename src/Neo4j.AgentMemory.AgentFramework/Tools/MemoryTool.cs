namespace Neo4j.AgentMemory.AgentFramework.Tools;

/// <summary>
/// Represents a callable memory tool that agents can invoke.
/// </summary>
public sealed class MemoryTool
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required Func<MemoryToolRequest, CancellationToken, Task<MemoryToolResponse>> ExecuteAsync { get; init; }
}

/// <summary>
/// Input to a memory tool invocation.
/// </summary>
public sealed class MemoryToolRequest
{
    public required string Query { get; init; }
    public IReadOnlyDictionary<string, object> Parameters { get; init; } = new Dictionary<string, object>();
}

/// <summary>
/// Output from a memory tool invocation.
/// </summary>
public sealed class MemoryToolResponse
{
    public required bool Success { get; init; }
    public string? Result { get; init; }
    public string? Error { get; init; }
}
