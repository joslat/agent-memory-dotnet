namespace Neo4j.AgentMemory.Extraction.AzureLanguage;

/// <summary>
/// Configuration options for the Azure AI Language extraction services.
/// </summary>
public sealed class AzureLanguageOptions
{
    /// <summary>
    /// Azure Cognitive Services endpoint URL.
    /// </summary>
    public required string Endpoint { get; set; }

    /// <summary>
    /// API key for authenticating with Azure Cognitive Services.
    /// </summary>
    public required string ApiKey { get; set; }

    /// <summary>
    /// Default language hint for text analysis. Defaults to "en".
    /// </summary>
    public string DefaultLanguage { get; set; } = "en";

    /// <summary>
    /// Maximum number of documents per batch request. Defaults to 25.
    /// </summary>
    public int MaxDocumentBatchSize { get; set; } = 25;

    /// <summary>
    /// Minimum sentiment confidence score required to emit a preference. Defaults to 0.7.
    /// </summary>
    public double PreferenceSentimentThreshold { get; set; } = 0.7;

    /// <summary>
    /// Confidence score assigned to facts extracted from key phrases. Defaults to 0.7.
    /// </summary>
    public double KeyPhraseFactConfidence { get; set; } = 0.7;

    /// <summary>
    /// Confidence score assigned to facts extracted from linked entities. Defaults to 0.8.
    /// </summary>
    public double LinkedEntityFactConfidence { get; set; } = 0.8;
}
