using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.Core.Stubs;

/// <summary>
/// Default IIdGenerator implementation using GUIDs.
/// </summary>
public sealed class GuidIdGenerator : IIdGenerator
{
    public string GenerateId() => Guid.NewGuid().ToString("N");
}
