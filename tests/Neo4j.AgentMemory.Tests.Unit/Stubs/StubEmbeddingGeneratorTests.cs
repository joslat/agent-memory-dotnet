using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.AgentMemory.Core.Stubs;

namespace Neo4j.AgentMemory.Tests.Unit.Stubs;

public class StubEmbeddingGeneratorTests
{
    private readonly StubEmbeddingGenerator _generator =
        new(NullLogger<StubEmbeddingGenerator>.Instance);

    [Fact]
    public async Task GenerateAsync_SingleValue_ReturnsOneEmbedding()
    {
        var result = await _generator.GenerateAsync(["hello world"]);
        result.Should().ContainSingle();
    }

    [Fact]
    public async Task GenerateAsync_SingleValue_ReturnsDimensionMatchingDefault()
    {
        var result = await _generator.GenerateAsync(["hello world"]);
        result[0].Vector.Length.Should().Be(1536);
    }

    [Fact]
    public async Task GenerateAsync_IsDeterministic()
    {
        const string text = "determinism test";
        var r1 = await _generator.GenerateAsync([text]);
        var r2 = await _generator.GenerateAsync([text]);
        r1[0].Vector.ToArray().Should().BeEquivalentTo(r2[0].Vector.ToArray());
    }

    [Fact]
    public async Task GenerateAsync_DifferentInputsProduceDifferentVectors()
    {
        var r1 = await _generator.GenerateAsync(["foo"]);
        var r2 = await _generator.GenerateAsync(["bar"]);
        r1[0].Vector.ToArray().SequenceEqual(r2[0].Vector.ToArray())
            .Should().BeFalse("different inputs must yield different vectors");
    }

    [Fact]
    public async Task GenerateAsync_BatchReturnsSameCountAsInput()
    {
        var result = await _generator.GenerateAsync(["alpha", "beta", "gamma"]);
        result.Should().HaveCount(3);
    }

    [Fact]
    public async Task GenerateAsync_EachVectorHasCorrectDimension()
    {
        var result = await _generator.GenerateAsync(["x", "y"]);
        foreach (var e in result)
            e.Vector.Length.Should().Be(1536);
    }

    [Fact]
    public async Task GenerateAsync_ConfigurableDimension()
    {
        var generator = new StubEmbeddingGenerator(NullLogger<StubEmbeddingGenerator>.Instance, 256);
        var result = await generator.GenerateAsync(["test"]);
        result[0].Vector.Length.Should().Be(256);
    }

    [Fact]
    public void Metadata_ProviderIdIsStub()
    {
        _generator.Metadata.ProviderName.Should().Be("stub");
    }
}
