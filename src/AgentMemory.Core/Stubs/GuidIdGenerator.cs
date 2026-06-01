using AgentMemory.Abstractions.Services;

namespace AgentMemory.Core.Stubs;

/// <summary>
/// Default IIdGenerator implementation using GUIDs.
/// </summary>
public sealed class GuidIdGenerator : IIdGenerator
{
    /// <inheritdoc/>
    public string GenerateId() => Guid.NewGuid().ToString("N");
}
