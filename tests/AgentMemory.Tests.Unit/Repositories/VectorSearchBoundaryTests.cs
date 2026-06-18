using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Repositories;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Repositories;

/// <summary>
/// R5 boundary invariant: an empty/degraded (zero-dimension) embedding must NOT reach
/// db.index.vector.queryNodes (which throws a dimension mismatch). Every repository vector-search /
/// dedup entry point short-circuits to an empty result BEFORE issuing a transaction — so a bare
/// transaction-runner substitute (whose ReadAsync is unconfigured) returning a usable result is itself
/// the proof that no query was attempted (otherwise awaiting the unconfigured runner would fault).
/// </summary>
public sealed class VectorSearchBoundaryTests
{
    private static readonly DateTimeOffset AsOf = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static INeo4jTransactionRunner BareRunner() => Substitute.For<INeo4jTransactionRunner>();

    private static readonly float[][] Degraded = [System.Array.Empty<float>(), null!];

    public static IEnumerable<object[]> EmptyEmbeddings() => Degraded.Select(e => new object[] { e });

    [Theory]
    [MemberData(nameof(EmptyEmbeddings))]
    public async Task FactSearch_EmptyEmbedding_ReturnsEmpty_WithoutQuerying(float[] embedding)
    {
        var repo = new Neo4jFactRepository(BareRunner(), NullLogger<Neo4jFactRepository>.Instance);
        (await repo.SearchByVectorAsync(embedding)).Should().BeEmpty();
        (await repo.SearchByVectorAsOfAsync(embedding, AsOf)).Should().BeEmpty();
        (await repo.FindDuplicateAsync("Alice", "works_at", embedding, ownerId: null, threshold: 0.95)).Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(EmptyEmbeddings))]
    public async Task EntitySearch_EmptyEmbedding_ReturnsEmpty_WithoutQuerying(float[] embedding)
    {
        var repo = new Neo4jEntityRepository(BareRunner(), NullLogger<Neo4jEntityRepository>.Instance);
        (await repo.SearchByVectorAsync(embedding)).Should().BeEmpty();
        (await repo.SearchByVectorAsOfAsync(embedding, AsOf)).Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(EmptyEmbeddings))]
    public async Task PreferenceSearch_EmptyEmbedding_ReturnsEmpty_WithoutQuerying(float[] embedding)
    {
        var repo = new Neo4jPreferenceRepository(BareRunner(), NullLogger<Neo4jPreferenceRepository>.Instance);
        (await repo.SearchByVectorAsync(embedding)).Should().BeEmpty();
        (await repo.SearchByVectorAsOfAsync(embedding, AsOf)).Should().BeEmpty();
        (await repo.FindDuplicateAsync("style", embedding, ownerId: null, threshold: 0.95)).Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(EmptyEmbeddings))]
    public async Task ReasoningTraceSearch_EmptyEmbedding_ReturnsEmpty_WithoutQuerying(float[] embedding)
    {
        var repo = new Neo4jReasoningTraceRepository(BareRunner(), NullLogger<Neo4jReasoningTraceRepository>.Instance);
        (await repo.SearchByTaskVectorAsync(embedding)).Should().BeEmpty();
        (await repo.SearchByTaskVectorAsOfAsync(embedding, AsOf)).Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(EmptyEmbeddings))]
    public async Task MessageSearch_EmptyEmbedding_ReturnsEmpty_WithoutQuerying(float[] embedding)
    {
        var repo = new Neo4jMessageRepository(BareRunner(), NullLogger<Neo4jMessageRepository>.Instance);
        (await repo.SearchByVectorAsync(embedding)).Should().BeEmpty();
    }
}
