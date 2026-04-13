namespace Neo4j.AgentMemory.Extraction.AzureLanguage.Internal;

/// <summary>
/// Thin wrapper over the Azure TextAnalyticsClient to enable unit testing.
/// </summary>
internal interface ITextAnalyticsClientWrapper
{
    Task<IReadOnlyList<AzureRecognizedEntity>> RecognizeEntitiesAsync(
        string document, string? language, CancellationToken ct);

    Task<IReadOnlyList<string>> ExtractKeyPhrasesAsync(
        string document, string? language, CancellationToken ct);

    Task<IReadOnlyList<AzureLinkedEntity>> RecognizeLinkedEntitiesAsync(
        string document, string? language, CancellationToken ct);
}
