using FluentAssertions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Resolution;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Resolution;

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
            .EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(candidateEmbedding));

        // Same vector → similarity = 1.0
        var existing = new[] { MakeEntityWithEmbedding("Alice", UnitVector(4, 0)) };

        var sut = new SemanticMatchEntityMatcher(orchestrator,
            new EntityResolutionOptions { SemanticMatchThreshold = 0.8 });

        var result = await sut.TryMatchAsync(MakeCandidate("Alice"), existing);

        result.Should().NotBeNull();
        result!.MatchType.Should().Be(EntityMatchType.Semantic);
        result.Confidence.Should().BeApproximately(1.0, 0.001);
    }

    [Fact]
    public async Task TryMatchAsync_LowSimilarityEmbeddings_ReturnsNull()
    {
        var orchestrator = Substitute.For<IEmbeddingOrchestrator>();
        var candidateEmbedding = UnitVector(4, 0); // e1 = [1,0,0,0]
        orchestrator
            .EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
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
            .EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
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
            .EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(UnitVector(4, 0)));

        var existing = new[] { MakeEntityWithEmbedding("Alice", UnitVector(4, 0)) };

        var sut = new SemanticMatchEntityMatcher(orchestrator,
            new EntityResolutionOptions { SemanticMatchThreshold = 0.5 });

        await sut.TryMatchAsync(MakeCandidate("Alice"), existing);

        await orchestrator.Received(1)
            .EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryMatchAsync_EmptyCandidateEmbedding_ReturnsNull_WithoutThrowing()
    {
        // cycle-3: the orchestrator degrades to an empty vector on a generation failure. The matcher must
        // treat that as "no signal" and return null (so resolution falls through to CreateNew) rather than
        // throwing a dimension-mismatch that silently drops the entity and its relationships.
        var orchestrator = Substitute.For<IEmbeddingOrchestrator>();
        orchestrator
            .EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Array.Empty<float>()));

        var existing = new[] { MakeEntityWithEmbedding("Alice", UnitVector(4, 0)) };

        var sut = new SemanticMatchEntityMatcher(orchestrator,
            new EntityResolutionOptions { SemanticMatchThreshold = 0.0 });

        var act = async () => await sut.TryMatchAsync(MakeCandidate("Alice"), existing);

        (await act.Should().NotThrowAsync()).Subject.Should().BeNull();
    }

    [Fact]
    public async Task TryMatchAsync_ExistingWithMismatchedDimension_IsSkipped_WithoutThrowing()
    {
        var orchestrator = Substitute.For<IEmbeddingOrchestrator>();
        orchestrator
            .EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(UnitVector(4, 0)));

        // Existing entity embedded with a different dimensionality (3 vs 4) — must be skipped, not compared.
        var existing = new[] { MakeEntityWithEmbedding("Alice", new float[] { 1f, 0f, 0f }) };

        var sut = new SemanticMatchEntityMatcher(orchestrator,
            new EntityResolutionOptions { SemanticMatchThreshold = 0.0 });

        var act = async () => await sut.TryMatchAsync(MakeCandidate("Alice"), existing);

        (await act.Should().NotThrowAsync()).Subject.Should().BeNull();
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
            .MatchType.Should().Be(EntityMatchType.Semantic);
    }
}
