using System.Collections.Concurrent;

namespace Neo4j.AgentMemory.Extraction.AzureLanguage.Internal;

/// <summary>
/// Scoped cache that deduplicates Azure entity recognition API calls within a single extraction operation.
/// Both <see cref="AzureLanguageEntityExtractor"/> and <see cref="AzureLanguageRelationshipExtractor"/>
/// call <c>RecognizeEntitiesAsync</c> for the same messages; this context ensures the API is only
/// called once per unique content string per scope.
/// </summary>
internal sealed class AzureExtractionContext
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<AzureRecognizedEntity>> _entityCache = new();

    public async Task<IReadOnlyList<AzureRecognizedEntity>> GetOrRecognizeEntitiesAsync(
        string content, string? language, ITextAnalyticsClientWrapper client, CancellationToken ct)
    {
        if (_entityCache.TryGetValue(content, out var cached))
            return cached;

        var result = await client.RecognizeEntitiesAsync(content, language, ct);
        var list = result.ToList();
        _entityCache.TryAdd(content, list);
        return list;
    }
}
