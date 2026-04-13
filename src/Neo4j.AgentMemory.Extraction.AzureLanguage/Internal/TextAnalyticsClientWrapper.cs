using Azure.AI.TextAnalytics;

namespace Neo4j.AgentMemory.Extraction.AzureLanguage.Internal;

/// <summary>
/// Default implementation that delegates to the real Azure TextAnalyticsClient.
/// </summary>
internal sealed class TextAnalyticsClientWrapper : ITextAnalyticsClientWrapper
{
    private readonly TextAnalyticsClient _client;

    public TextAnalyticsClientWrapper(TextAnalyticsClient client) => _client = client;

    public async Task<IReadOnlyList<AzureRecognizedEntity>> RecognizeEntitiesAsync(
        string document, string? language, CancellationToken ct)
    {
        var response = await _client.RecognizeEntitiesAsync(document, language, ct);
        return response.Value
            .Select(e => new AzureRecognizedEntity(e.Text, e.Category.ToString(), e.ConfidenceScore, e.SubCategory))
            .ToList();
    }

    public async Task<IReadOnlyList<string>> ExtractKeyPhrasesAsync(
        string document, string? language, CancellationToken ct)
    {
        var response = await _client.ExtractKeyPhrasesAsync(document, language, ct);
        return response.Value.ToList();
    }

    public async Task<IReadOnlyList<AzureLinkedEntity>> RecognizeLinkedEntitiesAsync(
        string document, string? language, CancellationToken ct)
    {
        var response = await _client.RecognizeLinkedEntitiesAsync(document, language, ct);
        return response.Value
            .Select(e => new AzureLinkedEntity(e.Name, e.Url?.ToString()))
            .ToList();
    }
}
