namespace AgentMemory.Extraction.Llm;

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
    /// Whether extraction requests should ask the chat provider for a JSON response.
    /// Disable only for providers that do not support the portable response-format hint.
    /// </summary>
    public bool UseJsonResponseFormat { get; set; } = true;

    /// <summary>
    /// Uses one typed model response for entities, facts, preferences, and relationships.
    /// Disabled by default until the unified path passes live extraction-quality acceptance;
    /// the existing four-category extraction path remains the compatibility control.
    /// </summary>
    public bool UseUnifiedExtraction { get; set; }

    /// <summary>
    /// Enables token-bounded multi-session unified extraction through
    /// <c>IMemoryExtractionPipeline.ExtractBatchAsync</c>. Disabled by default; single-session
    /// extraction behavior is unchanged.
    /// </summary>
    public bool UseMultiSessionBatchExtraction { get; set; }

    /// <summary>
    /// Maximum number of planned multi-session batches that one extraction operation may send
    /// concurrently. The default of one preserves the historical sequential provider-call order.
    /// </summary>
    public int MaxConcurrentBatchesPerExtraction { get; set; } = 1;

    /// <summary>
    /// Optional process-local cap shared by all multi-session extraction operations registered in
    /// the same service provider. Zero disables the shared cap.
    /// </summary>
    public int MaxConcurrentExtractionBatches { get; set; }

    /// <summary>
    /// Model identifier to use. <c>null</c> (the default) means use the <c>IChatClient</c> default.
    /// </summary>
    public string? ModelId { get; set; }

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
