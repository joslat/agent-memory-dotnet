namespace Neo4j.AgentMemory.Extraction.Llm;

/// <summary>
/// Configuration options for LLM-backed extractors.
/// </summary>
public sealed class LlmExtractionOptions
{
    /// <summary>
    /// Sampling temperature for the LLM call (0.0 = deterministic).
    /// </summary>
    public float Temperature { get; set; } = 0.0f;

    /// <summary>
    /// Number of retry attempts on transient failures.
    /// </summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>
    /// Model identifier to use. Empty string means use the IChatClient default.
    /// </summary>
    public string ModelId { get; set; } = "";

    /// <summary>
    /// POLE+O entity types recognised by the entity extractor.
    /// </summary>
    public IReadOnlyList<string> EntityTypes { get; set; } =
        new[] { "PERSON", "ORGANIZATION", "LOCATION", "EVENT", "OBJECT" };

    /// <summary>
    /// Optional override for the entity extractor system prompt.
    /// When null the extractor's built-in default prompt is used.
    /// </summary>
    public string? EntityExtractionPrompt { get; set; }

    /// <summary>
    /// Optional override for the fact extractor system prompt.
    /// When null the extractor's built-in default prompt is used.
    /// </summary>
    public string? FactExtractionPrompt { get; set; }

    /// <summary>
    /// Optional override for the relationship extractor system prompt.
    /// When null the extractor's built-in default prompt is used.
    /// </summary>
    public string? RelationshipExtractionPrompt { get; set; }

    /// <summary>
    /// Optional override for the preference extractor system prompt.
    /// When null the extractor's built-in default prompt is used.
    /// </summary>
    public string? PreferenceExtractionPrompt { get; set; }
}
