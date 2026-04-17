using FluentAssertions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Options;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.Core.Resolution;
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
        var orchestrator = Substitute.For<IEmbeddingOrchestrator>();
        var candidateEmbedding = UnitVector(4, 0);
        orchestrator
            .EmbedEntityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(candidateEmbedding));

        // Same vector → similarity = 1.0
        var existing = new[] { MakeEntityWithEmbedding("Alice", UnitVector(4, 0)) };

        var sut = new SemanticMatchEntityMatcher(orchestrator,
            new EntityResolutionOptions { SemanticMatchThreshold = 0.8 });

        var result = await sut.TryMatchAsync(MakeCandidate("Alice"), existing);

        result.Should().NotBeNull();
        result!.MatchType.Should().Be("semantic");
        result.Confidence.Should().BeApproximately(1.0, 0.001);
    }

    [Fact]
    public async Task TryMatchAsync_LowSimilarityEmbeddings_ReturnsNull()
    {
        var orchestrator = Substitute.For<IEmbeddingOrchestrator>();
        var candidateEmbedding = UnitVector(4, 0); // e1 = [1,0,0,0]
        orchestrator
            .EmbedEntityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(candidateEmbedding));

        // Orthogonal vector → similarity = 0.0
        var existing = new[] { MakeEntityWithEmbedding("Bob", UnitVector(4, 1)) };

        var sut = new SemanticMatchEntityMatcher(orchestrator,
            new EntityResolutionOptions { SemanticMatchThreshold = 0.8 });

        var result = await sut.TryMatchAsync(MakeCandidate("Alice"), existing);

        result.Should().BeNull();
    }

    [Fact]
    public async Task TryMatchAsync_EntitiesWithoutEmbeddingsAreSkipped()
    {
        var orchestrator = Substitute.For<IEmbeddingOrchestrator>();
        orchestrator
            .EmbedEntityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(UnitVector(4, 0)));

        // Entity has no embedding — should be skipped
        var existing = new[] { MakeEntityWithoutEmbedding("Alice") };

        var sut = new SemanticMatchEntityMatcher(orchestrator,
            new EntityResolutionOptions { SemanticMatchThreshold = 0.0 });

        var result = await sut.TryMatchAsync(MakeCandidate("Alice"), existing);

        result.Should().BeNull();
    }

    [Fact]
    public async Task TryMatchAsync_embeddingGeneratorCalledOnce()
    {
        var orchestrator = Substitute.For<IEmbeddingOrchestrator>();
        orchestrator
            .EmbedEntityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(UnitVector(4, 0)));

        var existing = new[] { MakeEntityWithEmbedding("Alice", UnitVector(4, 0)) };

        var sut = new SemanticMatchEntityMatcher(orchestrator,
            new EntityResolutionOptions { SemanticMatchThreshold = 0.5 });

        await sut.TryMatchAsync(MakeCandidate("Alice"), existing);

        await orchestrator.Received(1)
            .EmbedEntityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
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
        var orchestrator = Substitute.For<IEmbeddingOrchestrator>();
        new SemanticMatchEntityMatcher(orchestrator, new EntityResolutionOptions())
            .MatchType.Should().Be("semantic");
    }
}
