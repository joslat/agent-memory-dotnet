using Neo4j.AgentMemory.Abstractions.Domain;

namespace Neo4j.AgentMemory.Abstractions.Services;

/// <summary>
/// Source for GraphRAG-derived context.
/// </summary>
public interface IGraphRagContextSource
{
    /// <summary>
    /// Retrieves context from GraphRAG.
    /// </summary>
    Task<GraphRagContextResult> GetContextAsync(
        GraphRagContextRequest request,
        CancellationToken cancellationToken = default);
}
