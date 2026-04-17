using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.Core.Services;

public sealed class EmbeddingOrchestrator : IEmbeddingOrchestrator
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;
    private readonly ILogger<EmbeddingOrchestrator> _logger;

    public EmbeddingOrchestrator(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        ILogger<EmbeddingOrchestrator> logger)
    {
        _generator = generator;
        _logger = logger;
    }

    public Task<float[]> EmbedEntityAsync(string entityName, CancellationToken ct = default)
        => EmbedTextAsync(entityName, ct);

    public Task<float[]> EmbedFactAsync(string subject, string predicate, string obj, CancellationToken ct = default)
        => EmbedTextAsync($"{subject} {predicate} {obj}", ct);

    public Task<float[]> EmbedPreferenceAsync(string preferenceText, CancellationToken ct = default)
        => EmbedTextAsync(preferenceText, ct);

    public Task<float[]> EmbedMessageAsync(string content, CancellationToken ct = default)
        => EmbedTextAsync(content, ct);

    public Task<float[]> EmbedQueryAsync(string query, CancellationToken ct = default)
        => EmbedTextAsync(query, ct);

    public async Task<float[]> EmbedTextAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<float>();

        try
        {
            var result = await _generator.GenerateAsync([text], cancellationToken: ct).ConfigureAwait(false);
            return result[0].Vector.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Embedding generation failed for text (length={Len}); returning empty vector.", text.Length);
            return Array.Empty<float>();
        }
    }
}
