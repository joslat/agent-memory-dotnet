namespace AgentMemory.Abstractions.Options;

/// <summary>
/// Configuration for long-term memory.
/// </summary>
public sealed record LongTermMemoryOptions
{
    /// <summary>Whether to generate embeddings for entities automatically.</summary>
    public bool GenerateEntityEmbeddings { get; init; } = true;

    /// <summary>Whether to generate embeddings for facts automatically.</summary>
    public bool GenerateFactEmbeddings { get; init; } = true;

    /// <summary>Whether to generate embeddings for preferences automatically.</summary>
    public bool GeneratePreferenceEmbeddings { get; init; } = true;

    /// <summary>Minimum confidence threshold for persisting extracted items.</summary>
    public double MinConfidenceThreshold { get; init; } = 0.5;

    /// <summary>Whether to enable entity resolution and deduplication.</summary>
    public bool EnableEntityResolution { get; init; } = true;

    /// <summary>
    /// When true, <c>AddFactAsync</c>/<c>AddPreferenceAsync</c> reinforce an existing near-duplicate
    /// (same subject+predicate / same category, within the same owner, with cosine ≥
    /// <see cref="DeduplicationSimilarityThreshold"/>) instead of creating a new node — preventing node
    /// bloat from re-asserting the same knowledge across sessions. Requires embeddings to be enabled.
    /// </summary>
    public bool DeduplicateOnCreate { get; init; } = true;

    /// <summary>Cosine-similarity threshold above which a candidate is treated as a duplicate (default 0.95).</summary>
    public double DeduplicationSimilarityThreshold { get; init; } = 0.95;

    /// <summary>
    /// Confidence increment applied (capped at 1.0) when an existing item is reinforced by a duplicate
    /// add — a light reinforcement signal. Default 0.05.
    /// </summary>
    public double DeduplicationConfidenceBump { get; init; } = 0.05;

    /// <summary>
    /// Default magnitude by which entity feedback nudges confidence (positive reinforces, negative
    /// penalizes); clamped to [0,1]. Used by <c>RecordEntityFeedbackAsync</c> when no explicit delta is
    /// supplied. Default 0.1.
    /// </summary>
    public double FeedbackConfidenceDelta { get; init; } = 0.1;
}
