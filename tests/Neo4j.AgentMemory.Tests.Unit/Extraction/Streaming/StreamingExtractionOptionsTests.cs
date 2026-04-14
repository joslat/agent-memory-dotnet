using FluentAssertions;
using Neo4j.AgentMemory.Abstractions.Options;

namespace Neo4j.AgentMemory.Tests.Unit.Extraction.Streaming;

public sealed class StreamingExtractionOptionsTests
{
    [Fact]
    public void DefaultInstance_HasCharBasedDefaults()
    {
        var opts = new StreamingExtractionOptions();

        opts.ChunkSize.Should().Be(StreamingExtractionOptions.DefaultChunkSize);
        opts.Overlap.Should().Be(StreamingExtractionOptions.DefaultOverlap);
        opts.ChunkByTokens.Should().BeFalse();
        opts.SplitOnSentences.Should().BeTrue();
    }

    [Fact]
    public void DefaultChunkSize_Is4000()
    {
        StreamingExtractionOptions.DefaultChunkSize.Should().Be(4000);
    }

    [Fact]
    public void DefaultOverlap_Is200()
    {
        StreamingExtractionOptions.DefaultOverlap.Should().Be(200);
    }

    [Fact]
    public void DefaultTokenChunkSize_Is1000()
    {
        StreamingExtractionOptions.DefaultTokenChunkSize.Should().Be(1000);
    }

    [Fact]
    public void DefaultTokenOverlap_Is50()
    {
        StreamingExtractionOptions.DefaultTokenOverlap.Should().Be(50);
    }

    [Fact]
    public void ForTokens_ReturnsTokenBasedDefaults()
    {
        var opts = StreamingExtractionOptions.ForTokens();

        opts.ChunkByTokens.Should().BeTrue();
        opts.ChunkSize.Should().Be(StreamingExtractionOptions.DefaultTokenChunkSize);
        opts.Overlap.Should().Be(StreamingExtractionOptions.DefaultTokenOverlap);
    }
}
