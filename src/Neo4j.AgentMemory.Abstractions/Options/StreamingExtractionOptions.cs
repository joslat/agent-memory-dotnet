namespace Neo4j.AgentMemory.Abstractions.Options;

/// <summary>
/// Options that control how documents are chunked and processed in streaming extraction.
/// </summary>
public sealed class StreamingExtractionOptions
{
    /// <summary>
    /// Target chunk size. Interpreted as characters when <see cref="ChunkByTokens"/> is false
    /// (default 4000) or as approximate tokens when true (default 1000).
    /// </summary>
    public int ChunkSize { get; set; } = DefaultChunkSize;

    /// <summary>
    /// Overlap between consecutive chunks. Interpreted as characters or tokens depending on
    /// <see cref="ChunkByTokens"/>. Defaults to 200 chars or 50 tokens.
    /// </summary>
    public int Overlap { get; set; } = DefaultOverlap;

    /// <summary>
    /// When true, chunk by approximate token count instead of character count.
    /// Changing this also changes the effective defaults for <see cref="ChunkSize"/> and
    /// <see cref="Overlap"/>.
    /// </summary>
    public bool ChunkByTokens { get; set; }

    /// <summary>
    /// When true (default), attempt to split chunks on sentence boundaries so that
    /// sentences are not cut in the middle.
    /// </summary>
    public bool SplitOnSentences { get; set; } = true;

    // ── char-based defaults ──────────────────────────────────────────────────
    /// <summary>Default character-based chunk size (4 000 characters).</summary>
    public const int DefaultChunkSize = 4000;

    /// <summary>Default character-based overlap (200 characters).</summary>
    public const int DefaultOverlap = 200;

    // ── token-based defaults ─────────────────────────────────────────────────
    /// <summary>Default token-based chunk size (1 000 tokens).</summary>
    public const int DefaultTokenChunkSize = 1000;

    /// <summary>Default token-based overlap (50 tokens).</summary>
    public const int DefaultTokenOverlap = 50;

    /// <summary>
    /// Returns a new options instance configured for token-based chunking with token defaults.
    /// </summary>
    public static StreamingExtractionOptions ForTokens() =>
        new()
        {
            ChunkByTokens = true,
            ChunkSize = DefaultTokenChunkSize,
            Overlap = DefaultTokenOverlap
        };
}
