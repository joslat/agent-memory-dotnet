using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Domain.Extraction.Streaming;
using Neo4j.AgentMemory.Abstractions.Options;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.Core.Extraction.Streaming;

/// <summary>
/// Default implementation of <see cref="IStreamingExtractor"/>.
/// Splits long documents into overlapping chunks and extracts entities from each chunk,
/// streaming results as they become available.
/// </summary>
public sealed class StreamingExtractor : IStreamingExtractor
{
    private static readonly Regex TokenPattern = new(@"\S+", RegexOptions.Compiled);

    private readonly ILogger<StreamingExtractor> _logger;

    /// <summary>Initialises a new <see cref="StreamingExtractor"/>.</summary>
    public StreamingExtractor(ILogger<StreamingExtractor> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public IReadOnlyList<ChunkInfo> ChunkDocument(
        string text,
        StreamingExtractionOptions? options = null)
    {
        var opts = options ?? new StreamingExtractionOptions();
        return opts.ChunkByTokens
            ? TextChunker.ChunkByTokens(text, opts.ChunkSize, opts.Overlap)
            : TextChunker.ChunkByChars(text, opts.ChunkSize, opts.Overlap, opts.SplitOnSentences);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<StreamingChunkResult> ExtractStreamingAsync(
        string text,
        IEntityExtractor extractor,
        StreamingExtractionOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var chunks = ChunkDocument(text, options);
        _logger.LogInformation(
            "Streaming extraction: {ChunkCount} chunks from {CharCount} characters",
            chunks.Count, text.Length);

        foreach (var chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            StreamingChunkResult chunkResult;

            try
            {
                var message = BuildMessage(chunk.Text);
                var entities = await extractor.ExtractAsync(new[] { message }, ct)
                    .ConfigureAwait(false);

                sw.Stop();

                var result = new ExtractionResult { Entities = entities };
                chunkResult = new StreamingChunkResult
                {
                    Chunk = chunk,
                    Result = result,
                    Success = true,
                    DurationMs = sw.Elapsed.TotalMilliseconds
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                sw.Stop();
                _logger.LogWarning(ex,
                    "Chunk {ChunkIndex} extraction failed: {Message}", chunk.Index, ex.Message);

                chunkResult = new StreamingChunkResult
                {
                    Chunk = chunk,
                    Result = new ExtractionResult(),
                    Success = false,
                    Error = ex.Message,
                    DurationMs = sw.Elapsed.TotalMilliseconds
                };
            }

            yield return chunkResult;
        }
    }

    /// <inheritdoc/>
    public async Task<StreamingExtractionResult> ExtractAsync(
        string text,
        IEntityExtractor extractor,
        StreamingExtractionOptions? options = null,
        bool deduplicate = true,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var chunks = ChunkDocument(text, options);

        var allEntities = new List<ExtractedEntity>();
        var allRelationships = new List<ExtractedRelationship>();
        var chunkResults = new List<StreamingChunkResult>();
        int successfulChunks = 0;
        int failedChunks = 0;

        await foreach (var chunkResult in ExtractStreamingAsync(text, extractor, options, ct)
            .ConfigureAwait(false))
        {
            chunkResults.Add(chunkResult);
            if (chunkResult.Success)
            {
                successfulChunks++;
                allEntities.AddRange(chunkResult.Result.Entities);
                allRelationships.AddRange(chunkResult.Result.Relationships);
            }
            else
            {
                failedChunks++;
            }
        }

        int rawEntities = allEntities.Count;
        int rawRelationships = allRelationships.Count;

        IReadOnlyList<ExtractedEntity> finalEntities = allEntities;
        IReadOnlyList<ExtractedRelationship> finalRelationships = allRelationships;

        if (deduplicate)
        {
            finalEntities = EntityDeduplicator.DeduplicateEntities(allEntities);
            finalRelationships = EntityDeduplicator.DeduplicateRelationships(allRelationships);
        }

        sw.Stop();

        var stats = new StreamingExtractionStats
        {
            TotalChunks = chunks.Count,
            SuccessfulChunks = successfulChunks,
            FailedChunks = failedChunks,
            TotalEntities = rawEntities,
            TotalRelations = rawRelationships,
            DeduplicatedEntities = finalEntities.Count,
            TotalDurationMs = sw.Elapsed.TotalMilliseconds,
            TotalCharacters = text.Length,
            TotalTokensApprox = TokenPattern.Matches(text).Count
        };

        _logger.LogInformation(
            "Streaming extraction complete: {Successful}/{Total} chunks, " +
            "{Deduped} entities (from {Raw} raw), {DurationMs:F1}ms",
            successfulChunks, chunks.Count, finalEntities.Count, rawEntities,
            sw.Elapsed.TotalMilliseconds);

        return new StreamingExtractionResult
        {
            Entities = finalEntities,
            Relationships = finalRelationships,
            ChunkResults = chunkResults,
            Stats = stats
        };
    }

    private static Message BuildMessage(string content) => new()
    {
        MessageId = Guid.NewGuid().ToString(),
        ConversationId = "streaming",
        SessionId = "streaming",
        Role = "user",
        Content = content,
        TimestampUtc = DateTimeOffset.UtcNow
    };
}
