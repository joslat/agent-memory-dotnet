using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Options;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.Core.Extraction.Streaming;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Neo4j.AgentMemory.Tests.Unit.Extraction.Streaming;

public sealed class StreamingExtractorTests
{
    private static StreamingExtractor CreateSut() =>
        new(NullLogger<StreamingExtractor>.Instance);

    private static IEntityExtractor MockExtractor(params ExtractedEntity[] entities)
    {
        var extractor = Substitute.For<IEntityExtractor>();
        extractor.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ExtractedEntity>>(entities));
        return extractor;
    }

    // ── ChunkDocument ────────────────────────────────────────────────────────

    [Fact]
    public void ChunkDocument_SingleChunk_WhenShortText()
    {
        var sut = CreateSut();
        var chunks = sut.ChunkDocument("Short text.", new StreamingExtractionOptions());

        chunks.Should().HaveCount(1);
        chunks[0].IsFirst.Should().BeTrue();
        chunks[0].IsLast.Should().BeTrue();
    }

    [Fact]
    public void ChunkDocument_UsesTokenChunker_WhenChunkByTokensTrue()
    {
        var sut = CreateSut();
        string text = string.Join(" ", Enumerable.Range(1, 3000).Select(i => $"word{i}"));
        var opts = StreamingExtractionOptions.ForTokens();

        var chunks = sut.ChunkDocument(text, opts);
        chunks.Should().HaveCountGreaterThan(1);
    }

    // ── ExtractStreamingAsync ────────────────────────────────────────────────

    [Fact]
    public async Task ExtractStreamingAsync_SingleChunk_YieldsOneResult()
    {
        var sut = CreateSut();
        var extractor = MockExtractor(
            new ExtractedEntity { Name = "Alice", Type = "PERSON" });

        var results = new List<Abstractions.Domain.Extraction.Streaming.StreamingChunkResult>();
        await foreach (var r in sut.ExtractStreamingAsync("Hello Alice.", extractor))
            results.Add(r);

        results.Should().HaveCount(1);
        results[0].Success.Should().BeTrue();
        results[0].EntityCount.Should().Be(1);
    }

    [Fact]
    public async Task ExtractStreamingAsync_MultipleChunks_YieldsPerChunk()
    {
        var sut = CreateSut();
        var extractor = MockExtractor(
            new ExtractedEntity { Name = "Bob", Type = "PERSON" });

        string text = new string('x', 20000);
        var opts = new StreamingExtractionOptions { ChunkSize = 4000, Overlap = 0 };

        var results = new List<Abstractions.Domain.Extraction.Streaming.StreamingChunkResult>();
        await foreach (var r in sut.ExtractStreamingAsync(text, extractor, opts))
            results.Add(r);

        results.Should().HaveCountGreaterThan(1);
        results.All(r => r.Success).Should().BeTrue();
    }

    [Fact]
    public async Task ExtractStreamingAsync_ExtractorThrows_YieldsFailedChunk()
    {
        var sut = CreateSut();
        var extractor = Substitute.For<IEntityExtractor>();
        extractor
            .ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        var results = new List<Abstractions.Domain.Extraction.Streaming.StreamingChunkResult>();
        await foreach (var r in sut.ExtractStreamingAsync("Some text.", extractor))
            results.Add(r);

        results.Should().HaveCount(1);
        results[0].Success.Should().BeFalse();
        results[0].Error.Should().Be("boom");
    }

    [Fact]
    public async Task ExtractStreamingAsync_IndexesAreSequential()
    {
        var sut = CreateSut();
        var extractor = MockExtractor();
        string text = new string('a', 20000);
        var opts = new StreamingExtractionOptions { ChunkSize = 4000, Overlap = 0 };

        var results = new List<Abstractions.Domain.Extraction.Streaming.StreamingChunkResult>();
        await foreach (var r in sut.ExtractStreamingAsync(text, extractor, opts))
            results.Add(r);

        for (int i = 0; i < results.Count; i++)
            results[i].Chunk.Index.Should().Be(i);
    }

    // ── ExtractAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ExtractAsync_SingleChunk_ReturnsEntities()
    {
        var sut = CreateSut();
        var extractor = MockExtractor(
            new ExtractedEntity { Name = "Alice", Type = "PERSON", Confidence = 0.9 });

        var result = await sut.ExtractAsync("Hello Alice.", extractor);

        result.Entities.Should().HaveCount(1);
        result.Stats.TotalChunks.Should().Be(1);
        result.Stats.SuccessfulChunks.Should().Be(1);
        result.Stats.FailedChunks.Should().Be(0);
    }

    [Fact]
    public async Task ExtractAsync_MultipleChunks_DeduplicatesEntities()
    {
        // Same entity returned from every chunk → dedup to 1
        var sut = CreateSut();
        var extractor = MockExtractor(
            new ExtractedEntity { Name = "Alice", Type = "PERSON", Confidence = 0.9 });

        string text = new string('a', 20000);
        var opts = new StreamingExtractionOptions { ChunkSize = 4000, Overlap = 0 };

        var result = await sut.ExtractAsync(text, extractor, opts, deduplicate: true);

        result.Entities.Should().HaveCount(1);
        result.Stats.TotalEntities.Should().BeGreaterThan(1);
        result.Stats.DeduplicatedEntities.Should().Be(1);
    }

    [Fact]
    public async Task ExtractAsync_DeduplicateFalse_KeepsAllEntities()
    {
        var sut = CreateSut();
        var extractor = MockExtractor(
            new ExtractedEntity { Name = "Alice", Type = "PERSON", Confidence = 0.9 });

        string text = new string('a', 20000);
        var opts = new StreamingExtractionOptions { ChunkSize = 4000, Overlap = 0 };

        var result = await sut.ExtractAsync(text, extractor, opts, deduplicate: false);

        result.Entities.Should().HaveCountGreaterThan(1);
    }

    [Fact]
    public async Task ExtractAsync_FailedChunk_StatsReflectFailure()
    {
        var sut = CreateSut();
        var extractor = Substitute.For<IEntityExtractor>();
        extractor
            .ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await sut.ExtractAsync("text", extractor);

        result.Stats.FailedChunks.Should().Be(1);
        result.Stats.SuccessfulChunks.Should().Be(0);
    }

    [Fact]
    public async Task ExtractAsync_Stats_TotalCharactersCorrect()
    {
        var sut = CreateSut();
        const string text = "Hello world!";
        var extractor = MockExtractor();

        var result = await sut.ExtractAsync(text, extractor);

        result.Stats.TotalCharacters.Should().Be(text.Length);
    }

    [Fact]
    public async Task ExtractAsync_Stats_TotalTokensApproxCorrect()
    {
        var sut = CreateSut();
        const string text = "word1 word2 word3";
        var extractor = MockExtractor();

        var result = await sut.ExtractAsync(text, extractor);

        result.Stats.TotalTokensApprox.Should().Be(3);
    }

    [Fact]
    public async Task ExtractAsync_ToExtractionResult_ReturnsValidResult()
    {
        var sut = CreateSut();
        var extractor = MockExtractor(
            new ExtractedEntity { Name = "Alice", Type = "PERSON" });

        var streamResult = await sut.ExtractAsync("Alice lives here.", extractor);
        var extractionResult = streamResult.ToExtractionResult();

        extractionResult.Should().NotBeNull();
        extractionResult.Entities.Should().HaveCount(1);
    }
}
