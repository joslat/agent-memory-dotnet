using System.Text.RegularExpressions;

namespace Neo4j.AgentMemory.Abstractions.Domain.Extraction.Streaming;

/// <summary>Information about a single document chunk.</summary>
public sealed record ChunkInfo
{
    private static readonly Regex TokenPattern = new(@"\S+", RegexOptions.Compiled);

    /// <summary>Zero-based index of this chunk within the document.</summary>
    public required int Index { get; init; }

    /// <summary>Start character offset in the original document.</summary>
    public required int StartChar { get; init; }

    /// <summary>Exclusive end character offset in the original document.</summary>
    public required int EndChar { get; init; }

    /// <summary>Text content of this chunk.</summary>
    public required string Text { get; init; }

    /// <summary>Whether this is the first chunk.</summary>
    public bool IsFirst { get; init; }

    /// <summary>Whether this is the last chunk.</summary>
    public bool IsLast { get; init; }

    /// <summary>Number of characters in this chunk.</summary>
    public int CharCount => Text.Length;

    /// <summary>Approximate token count (word-based, matching Python \S+ pattern).</summary>
    public int ApproxTokenCount => TokenPattern.Matches(Text).Count;
}
