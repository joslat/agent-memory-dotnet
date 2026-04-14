using System.Text.RegularExpressions;
using Neo4j.AgentMemory.Abstractions.Domain.Extraction.Streaming;

namespace Neo4j.AgentMemory.Core.Extraction.Streaming;

/// <summary>
/// Static helpers that split raw text into overlapping <see cref="ChunkInfo"/> slices,
/// porting the logic from the Python <c>streaming.py</c> module.
/// </summary>
internal static class TextChunker
{
    private static readonly Regex SentenceEndPattern =
        new(@"[.!?]\s+", RegexOptions.Compiled);

    private static readonly Regex TokenPattern =
        new(@"\S+", RegexOptions.Compiled);

    /// <summary>
    /// Splits <paramref name="text"/> into chunks by character count.
    /// </summary>
    internal static IReadOnlyList<ChunkInfo> ChunkByChars(
        string text,
        int chunkSize,
        int overlap,
        bool splitOnSentences)
    {
        if (string.IsNullOrEmpty(text))
            return Array.Empty<ChunkInfo>();

        if (text.Length <= chunkSize)
        {
            return new[]
            {
                new ChunkInfo
                {
                    Index = 0, StartChar = 0, EndChar = text.Length,
                    Text = text, IsFirst = true, IsLast = true
                }
            };
        }

        var chunks = new List<ChunkInfo>();
        int start = 0;
        int chunkIndex = 0;

        while (start < text.Length)
        {
            int end = Math.Min(start + chunkSize, text.Length);

            if (end < text.Length && splitOnSentences)
            {
                int searchStart = Math.Max(end - 100, start);
                string searchRegion = text[searchStart..end];
                var sentenceEnds = SentenceEndPattern.Matches(searchRegion);
                if (sentenceEnds.Count > 0)
                {
                    int boundary = sentenceEnds[^1].Index + sentenceEnds[^1].Length;
                    end = searchStart + boundary;
                }
            }

            chunks.Add(new ChunkInfo
            {
                Index = chunkIndex,
                StartChar = start,
                EndChar = end,
                Text = text[start..end],
                IsFirst = chunkIndex == 0,
                IsLast = end >= text.Length
            });

            start = end < text.Length ? end - overlap : end;
            chunkIndex++;
        }

        if (chunks.Count > 0)
            chunks[^1] = chunks[^1] with { IsLast = true };

        return chunks;
    }

    /// <summary>
    /// Splits <paramref name="text"/> into chunks by approximate token count,
    /// using a simple whitespace-based tokeniser (matching Python's <c>\S+</c> pattern).
    /// </summary>
    internal static IReadOnlyList<ChunkInfo> ChunkByTokens(
        string text,
        int chunkSize,
        int overlap)
    {
        if (string.IsNullOrEmpty(text))
            return Array.Empty<ChunkInfo>();

        var tokens = TokenPattern.Matches(text);

        if (tokens.Count <= chunkSize)
        {
            return new[]
            {
                new ChunkInfo
                {
                    Index = 0, StartChar = 0, EndChar = text.Length,
                    Text = text, IsFirst = true, IsLast = true
                }
            };
        }

        var chunks = new List<ChunkInfo>();
        int tokenIdx = 0;
        int chunkIndex = 0;

        while (tokenIdx < tokens.Count)
        {
            int startChar = tokens[tokenIdx].Index;
            int endTokenIdx = Math.Min(tokenIdx + chunkSize, tokens.Count);
            int endChar = tokens[endTokenIdx - 1].Index + tokens[endTokenIdx - 1].Length;

            if (endTokenIdx < tokens.Count)
            {
                int lookAheadEnd = Math.Min(endChar + 100, text.Length);
                string next100 = text[endChar..lookAheadEnd];
                var sentenceMatch = SentenceEndPattern.Match(next100);
                if (sentenceMatch.Success)
                    endChar = endChar + sentenceMatch.Index + sentenceMatch.Length;
            }

            chunks.Add(new ChunkInfo
            {
                Index = chunkIndex,
                StartChar = startChar,
                EndChar = endChar,
                Text = text[startChar..endChar],
                IsFirst = chunkIndex == 0,
                IsLast = endTokenIdx >= tokens.Count
            });

            tokenIdx = endTokenIdx < tokens.Count ? endTokenIdx - overlap : endTokenIdx;
            chunkIndex++;
        }

        if (chunks.Count > 0)
            chunks[^1] = chunks[^1] with { IsLast = true };

        return chunks;
    }
}
