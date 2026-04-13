namespace Neo4j.AgentMemory.Extraction.AzureLanguage.Internal;

/// <summary>
/// A recognized named entity returned from Azure Language services.
/// </summary>
internal sealed record AzureRecognizedEntity(
    string Text,
    string Category,
    double ConfidenceScore,
    string? SubCategory);

/// <summary>
/// A linked entity (Wikipedia reference) returned from Azure Language services.
/// </summary>
internal sealed record AzureLinkedEntity(
    string Name,
    string? Url);
