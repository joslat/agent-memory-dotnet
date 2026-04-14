using FluentAssertions;
using Neo4j.AgentMemory.Abstractions.Domain.Extraction.Streaming;
using Neo4j.AgentMemory.Core.Extraction.Streaming;

namespace Neo4j.AgentMemory.Tests.Unit.Extraction.Streaming;

public sealed class TextChunkerTests
{
    // ── ChunkByChars ─────────────────────────────────────────────────────────

    [Fact]
    public void ChunkByChars_EmptyText_ReturnsEmpty()
    {
        var result = TextChunker.ChunkByChars("", 4000, 200, true);
        result.Should().BeEmpty();
    }

    [Fact]
    public void ChunkByChars_NullText_ReturnsEmpty()
    {
        var result = TextChunker.ChunkByChars(null!, 4000, 200, true);
        result.Should().BeEmpty();
    }

    [Fact]
    public void ChunkByChars_ShortText_ReturnsSingleChunk()
    {
        const string text = "Hello world";
        var result = TextChunker.ChunkByChars(text, 4000, 200, true);

        result.Should().HaveCount(1);
        result[0].Index.Should().Be(0);
        result[0].StartChar.Should().Be(0);
        result[0].EndChar.Should().Be(text.Length);
        result[0].Text.Should().Be(text);
        result[0].IsFirst.Should().BeTrue();
        result[0].IsLast.Should().BeTrue();
    }

    [Fact]
    public void ChunkByChars_LongText_ReturnsMultipleChunks()
    {
        var text = new string('a', 10000);
        var result = TextChunker.ChunkByChars(text, 4000, 200, false);

        result.Should().HaveCountGreaterThan(1);
        result[0].IsFirst.Should().BeTrue();
        result[^1].IsLast.Should().BeTrue();
    }

    [Fact]
    public void ChunkByChars_Overlap_IsRespected()
    {
        // Create text long enough to get 2+ chunks
        var text = new string('x', 8000);
        var result = TextChunker.ChunkByChars(text, 4000, 200, false);

        result.Should().HaveCountGreaterThanOrEqualTo(2);
        // Second chunk should start at chunkSize - overlap from first chunk start
        int expectedStart = result[0].EndChar - 200;
        result[1].StartChar.Should().Be(expectedStart);
    }

    [Fact]
    public void ChunkByChars_SentenceSplitting_PrefersSentenceBoundary()
    {
        // Build text with a sentence ending near the chunk boundary
        string prefix = new string('a', 3950);
        const string sentence = "End. Next sentence continues here and beyond.";
        string text = prefix + sentence + new string('b', 1000);

        var result = TextChunker.ChunkByChars(text, 4000, 200, splitOnSentences: true);

        // The first chunk should end after "End. " (at a sentence boundary)
        result[0].Text.Should().EndWith(". ");
    }

    [Fact]
    public void ChunkByChars_SplitOnSentencesFalse_CutsAtChunkSize()
    {
        // Text is clearly longer than chunkSize so it will be split
        string text = new string('a', 3950) + "End. " + new string('b', 2000);
        var result = TextChunker.ChunkByChars(text, 4000, 200, splitOnSentences: false);

        // Without sentence splitting the first chunk ends exactly at chunkSize
        result[0].EndChar.Should().Be(4000);
    }

    [Fact]
    public void ChunkByChars_ChunkIndices_AreSequential()
    {
        var text = new string('z', 20000);
        var result = TextChunker.ChunkByChars(text, 4000, 200, false);

        for (int i = 0; i < result.Count; i++)
            result[i].Index.Should().Be(i);
    }

    [Fact]
    public void ChunkByChars_UnicodeText_HandledCorrectly()
    {
        // emoji = 2 chars in .NET (UTF-16 surrogate pairs)
        var text = string.Concat(Enumerable.Repeat("🎉 Hello! ", 600));
        var result = TextChunker.ChunkByChars(text, 4000, 200, false);

        result.Should().NotBeEmpty();
        result[0].IsFirst.Should().BeTrue();
        result[^1].IsLast.Should().BeTrue();
    }

    [Fact]
    public void ChunkByChars_VeryLongText_DoesNotThrow()
    {
        var text = new string('x', 1_000_000);
        var act = () => TextChunker.ChunkByChars(text, 4000, 200, true);
        act.Should().NotThrow();
    }

    [Fact]
    public void ChunkByChars_TextWithNoSpaces_StillChunks()
    {
        var text = new string('a', 10000);
        var result = TextChunker.ChunkByChars(text, 4000, 0, splitOnSentences: true);
        result.Should().HaveCountGreaterThan(1);
    }

    // ── ChunkByTokens ────────────────────────────────────────────────────────

    [Fact]
    public void ChunkByTokens_EmptyText_ReturnsEmpty()
    {
        var result = TextChunker.ChunkByTokens("", 1000, 50);
        result.Should().BeEmpty();
    }

    [Fact]
    public void ChunkByTokens_ShortText_ReturnsSingleChunk()
    {
        const string text = "The quick brown fox jumps.";
        var result = TextChunker.ChunkByTokens(text, 1000, 50);

        result.Should().HaveCount(1);
        result[0].IsFirst.Should().BeTrue();
        result[0].IsLast.Should().BeTrue();
    }

    [Fact]
    public void ChunkByTokens_ManyTokens_ReturnsMultipleChunks()
    {
        // 3000 tokens: word word word …
        string text = string.Join(" ", Enumerable.Range(1, 3000).Select(i => $"word{i}"));
        var result = TextChunker.ChunkByTokens(text, 1000, 50);

        result.Should().HaveCountGreaterThan(1);
        result[0].IsFirst.Should().BeTrue();
        result[^1].IsLast.Should().BeTrue();
    }

    [Fact]
    public void ChunkByTokens_TokenOverlap_IsRespected()
    {
        string text = string.Join(" ", Enumerable.Range(1, 3000).Select(i => $"w{i}"));
        var result = TextChunker.ChunkByTokens(text, 1000, 50);

        result.Should().HaveCountGreaterThanOrEqualTo(2);
        // Chunks should overlap — end of chunk 0 > start of chunk 1
        result[1].StartChar.Should().BeLessThan(result[0].EndChar);
    }
}
