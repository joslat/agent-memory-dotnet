using Neo4j.AgentMemory.Abstractions.Domain.Extraction.Streaming;
using Neo4j.AgentMemory.Abstractions.Options;

namespace Neo4j.AgentMemory.Abstractions.Services;

/// <summary>
/// Streaming extractor that processes long documents by splitting them into overlapping
/// chunks and running an <see cref="IEntityExtractor"/> on each one.
/// </summary>
public interface IStreamingExtractor
{
    /// <summary>
    /// Splits <paramref name="text"/> into chunks according to <paramref name="options"/>.
    /// </summary>
    IReadOnlyList<ChunkInfo> ChunkDocument(
        string text,
        StreamingExtractionOptions? options = null);

    /// <summary>
    /// Asynchronously streams one <see cref="StreamingChunkResult"/> per chunk as each chunk
    /// is extracted. Errors on individual chunks are captured inside the result rather than
    /// propagated.
    /// </summary>
    IAsyncEnumerable<StreamingChunkResult> ExtractStreamingAsync(
        string text,
        IEntityExtractor extractor,
        StreamingExtractionOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// Processes the entire document, collects all chunk results, optionally deduplicates,
    /// and returns a <see cref="StreamingExtractionResult"/> with aggregate statistics.
    /// </summary>
    Task<StreamingExtractionResult> ExtractAsync(
        string text,
        IEntityExtractor extractor,
        StreamingExtractionOptions? options = null,
        bool deduplicate = true,
        CancellationToken ct = default);
}
