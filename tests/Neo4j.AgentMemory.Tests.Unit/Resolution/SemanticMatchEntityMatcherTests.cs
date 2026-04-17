using FluentAssertions;
using Microsoft.Extensions.AI;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Options;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.Core.Resolution;
using Neo4j.AgentMemory.Tests.Unit.TestHelpers;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.Resolution;

public sealed class SemanticMatchEntityMatcherTests
{
    private static readonly DateTimeOffset FixedTime = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static Entity MakeEntityWithEmbedding(string name, float[] embedding) =>
        new()
        {
            EntityId = Guid.NewGuid().ToString("N"),
            Name = name,
            Type = "Person",
            Confidence = 1.0,
            Embedding = embedding,
            CreatedAtUtc = FixedTime
        };

    private static Entity MakeEntityWithoutEmbedding(string name) =>
        new()
        {
            EntityId = Guid.NewGuid().ToString("N"),
            Name = name,
            Type = "Person",
            Confidence = 1.0,
            CreatedAtUtc = FixedTime
        };

    private static ExtractedEntity MakeCandidate(string name) =>
        new() { Name = name, Type = "Person" };

    private static float[] UnitVector(int dim, int nonZeroIdx = 0)
    {
        var v = new float[dim];
        v[nonZeroIdx] = 1.0f;
        return v;
    }

    [Fact]
    public async Task TryMatchAsync_HighSimilarityEmbeddings_ReturnsResult()
    {
        var embeddingProvider = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var candidateEmbedding = UnitVector(4, 0);
        embeddingProvider
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(call => MockFactory.EmbeddingResult(call, candidateEmbedding));

        // Same vector → similarity = 1.0
        var existing = new[] { MakeEntityWithEmbedding("Alice", UnitVector(4, 0)) };

        var sut = new SemanticMatchEntityMatcher(embeddingProvider,
            new EntityResolutionOptions { SemanticMatchThreshold = 0.8 });

        var result = await sut.TryMatchAsync(MakeCandidate("Alice"), existing);

        result.Should().NotBeNull();
        result!.MatchType.Should().Be("semantic");
        result.Confidence.Should().BeApproximately(1.0, 0.001);
    }

    [Fact]
    public async Task TryMatchAsync_LowSimilarityEmbeddings_ReturnsNull()
    {
        var embeddingProvider = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var candidateEmbedding = UnitVector(4, 0); // e1 = [1,0,0,0]
        embeddingProvider
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(call => MockFactory.EmbeddingResult(call, candidateEmbedding));

        // Orthogonal vector → similarity = 0.0
        var existing = new[] { MakeEntityWithEmbedding("Bob", UnitVector(4, 1)) };

        var sut = new SemanticMatchEntityMatcher(embeddingProvider,
            new EntityResolutionOptions { SemanticMatchThreshold = 0.8 });

        var result = await sut.TryMatchAsync(MakeCandidate("Alice"), existing);

        result.Should().BeNull();
    }

    [Fact]
    public async Task TryMatchAsync_EntitiesWithoutEmbeddingsAreSkipped()
    {
        var embeddingProvider = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embeddingProvider
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(call => MockFactory.EmbeddingResult(call, UnitVector(4, 0)));

        // Entity has no embedding — should be skipped
        var existing = new[] { MakeEntityWithoutEmbedding("Alice") };

        var sut = new SemanticMatchEntityMatcher(embeddingProvider,
            new EntityResolutionOptions { SemanticMatchThreshold = 0.0 });

        var result = await sut.TryMatchAsync(MakeCandidate("Alice"), existing);

        result.Should().BeNull();
    }

    [Fact]
    public async Task TryMatchAsync_embeddingGeneratorCalledOnce()
    {
        var embeddingProvider = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embeddingProvider
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(call => MockFactory.EmbeddingResult(call, UnitVector(4, 0)));

        var existing = new[] { MakeEntityWithEmbedding("Alice", UnitVector(4, 0)) };

        var sut = new SemanticMatchEntityMatcher(embeddingProvider,
            new EntityResolutionOptions { SemanticMatchThreshold = 0.5 });

        await sut.TryMatchAsync(MakeCandidate("Alice"), existing);

        await embeddingProvider.Received(1)
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void CosineSimilarity_IdenticalVectors_ReturnsOne()
    {
        var v = new float[] { 1.0f, 2.0f, 3.0f };
        SemanticMatchEntityMatcher.CosineSimilarity(v, v).Should().BeApproximately(1.0, 0.0001);
    }

    [Fact]
    public void CosineSimilarity_OrthogonalVectors_ReturnsZero()
    {
        var a = new float[] { 1.0f, 0.0f };
        var b = new float[] { 0.0f, 1.0f };
        SemanticMatchEntityMatcher.CosineSimilarity(a, b).Should().BeApproximately(0.0, 0.0001);
    }

    [Fact]
    public void MatchType_IsSemantic()
    {
        var provider = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        new SemanticMatchEntityMatcher(provider, new EntityResolutionOptions())
            .MatchType.Should().Be("semantic");
    }
}
