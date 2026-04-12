using Neo4j.AgentMemory.Abstractions.Domain;

namespace Neo4j.AgentMemory.Abstractions.Services;

/// <summary>
/// Pipeline for extracting structured memory from messages.
/// </summary>
public interface IMemoryExtractionPipeline
{
    /// <summary>
    /// Extracts structured memory from messages.
    /// </summary>
    Task<ExtractionResult> ExtractAsync(
        ExtractionRequest request,
        CancellationToken cancellationToken = default);
}
