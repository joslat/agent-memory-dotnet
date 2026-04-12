using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.AgentMemory.Core.Stubs;

namespace Neo4j.AgentMemory.Tests.Unit.Stubs;

public class StubEmbeddingProviderTests
{
    private readonly StubEmbeddingProvider _provider =
        new(NullLogger<StubEmbeddingProvider>.Instance);

    [Fact]
    public async Task GenerateEmbeddingAsync_ReturnsDimensionMatchingProperty()
    {
        var vector = await _provider.GenerateEmbeddingAsync("hello world");
        vector.Should().HaveCount(_provider.EmbeddingDimensions);
    }

    [Fact]
    public void EmbeddingDimensions_DefaultsTo1536()
    {
        _provider.EmbeddingDimensions.Should().Be(1536);
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_IsDeterministic()
    {
        const string text = "determinism test";
        var v1 = await _provider.GenerateEmbeddingAsync(text);
        var v2 = await _provider.GenerateEmbeddingAsync(text);
        v1.Should().BeEquivalentTo(v2);
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_DifferentInputsProduceDifferentVectors()
    {
        var v1 = await _provider.GenerateEmbeddingAsync("foo");
        var v2 = await _provider.GenerateEmbeddingAsync("bar");
        // Avoid NotBeEquivalentTo on large collections — use SequenceEqual instead.
        v1.SequenceEqual(v2).Should().BeFalse("different inputs must yield different vectors");
    }

    [Fact]
    public async Task GenerateEmbeddingsAsync_ReturnsSameCountAsInput()
    {
        var texts = new[] { "alpha", "beta", "gamma" };
        var results = await _provider.GenerateEmbeddingsAsync(texts);
        results.Should().HaveCount(3);
    }

    [Fact]
    public async Task GenerateEmbeddingsAsync_EachVectorHasCorrectDimension()
    {
        var texts = new[] { "x", "y" };
        var results = await _provider.GenerateEmbeddingsAsync(texts);
        foreach (var v in results)
            v.Should().HaveCount(_provider.EmbeddingDimensions);
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_ConfigurableDimension()
    {
        var provider = new StubEmbeddingProvider(NullLogger<StubEmbeddingProvider>.Instance, 256);
        var vector = await provider.GenerateEmbeddingAsync("test");
        vector.Should().HaveCount(256);
        provider.EmbeddingDimensions.Should().Be(256);
    }
}
