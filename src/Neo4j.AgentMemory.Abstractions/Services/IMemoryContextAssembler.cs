using Neo4j.AgentMemory.Abstractions.Domain;

namespace Neo4j.AgentMemory.Abstractions.Services;

/// <summary>
/// Service for assembling memory context from multiple sources.
/// </summary>
public interface IMemoryContextAssembler
{
    /// <summary>
    /// Assembles memory context for a recall request.
    /// </summary>
    Task<MemoryContext> AssembleContextAsync(
        RecallRequest request,
        CancellationToken cancellationToken = default);
}
