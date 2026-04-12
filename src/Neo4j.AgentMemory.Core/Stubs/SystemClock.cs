using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.Core.Stubs;

/// <summary>
/// Default IClock implementation using system time.
/// </summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
