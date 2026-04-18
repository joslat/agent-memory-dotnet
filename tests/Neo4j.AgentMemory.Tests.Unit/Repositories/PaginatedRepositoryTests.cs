using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Neo4j.Infrastructure;
using Neo4j.AgentMemory.Neo4j.Repositories;
using Neo4j.Driver;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.Repositories;

/// <summary>
/// Unit tests verifying that <see cref="Neo4jFactRepository.GetPageWithoutEmbeddingAsync"/>
/// and <see cref="Neo4jPreferenceRepository.GetPageWithoutEmbeddingAsync"/> apply the N+1
/// pagination pattern correctly.
/// </summary>
public sealed class PaginatedRepositoryTests
{
    // ── Helpers ──────────────────────────────────────────────────────────

    private static IResultCursor BuildEmptyCursor()
    {
        var cursor = Substitute.For<IResultCursor>();
        cursor.FetchAsync().Returns(Task.FromResult(false));
        return cursor;
    }

    private static (Neo4jFactRepository Repo, List<(string Cypher, object? Parameters)> Calls)
        CreateFactPagedReadCapture()
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        txRunner
            .ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<PagedResult<Fact>>>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task<PagedResult<Fact>>>>();
                var runner = Substitute.For<IAsyncQueryRunner>();
                runner
                    .RunAsync(Arg.Any<string>(), Arg.Any<object>())
                    .Returns(ci =>
                    {
                        calls.Add((ci.Arg<string>(), ci.ArgAt<object>(1)));
                        return Task.FromResult(BuildEmptyCursor());
                    });
                return await work(runner);
            });
        return (new Neo4jFactRepository(txRunner, NullLogger<Neo4jFactRepository>.Instance), calls);
    }

    private static (Neo4jPreferenceRepository Repo, List<(string Cypher, object? Parameters)> Calls)
        CreatePreferencePagedReadCapture()
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        txRunner
            .ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<PagedResult<Preference>>>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task<PagedResult<Preference>>>>();
                var runner = Substitute.For<IAsyncQueryRunner>();
                runner
                    .RunAsync(Arg.Any<string>(), Arg.Any<object>())
                    .Returns(ci =>
                    {
                        calls.Add((ci.Arg<string>(), ci.ArgAt<object>(1)));
                        return Task.FromResult(BuildEmptyCursor());
                    });
                return await work(runner);
            });
        return (new Neo4jPreferenceRepository(txRunner, NullLogger<Neo4jPreferenceRepository>.Instance), calls);
    }

    // ── Neo4jFactRepository ──────────────────────────────────────────────

    [Fact]
    public async Task FactRepository_GetPageWithoutEmbedding_SendsN1LimitToDatabase()
    {
        // The repo should request limit+1 items so it can detect a next page.
        var (repo, calls) = CreateFactPagedReadCapture();

        await repo.GetPageWithoutEmbeddingAsync(limit: 10);

        calls.Should().ContainSingle();
        var param = calls[0].Parameters!;
        param.GetType().GetProperty("limit")!.GetValue(param).Should().Be(11); // limit+1
    }

    [Fact]
    public async Task FactRepository_GetPageWithoutEmbedding_SendsCorrectCypher()
    {
        var (repo, calls) = CreateFactPagedReadCapture();

        await repo.GetPageWithoutEmbeddingAsync(limit: 5);

        calls[0].Cypher.Should().Contain("f.embedding IS NULL");
        calls[0].Cypher.Should().Contain("LIMIT $limit");
    }

    [Fact]
    public async Task FactRepository_GetPageWithoutEmbedding_ReturnsPagedResult()
    {
        var (repo, _) = CreateFactPagedReadCapture();

        var result = await repo.GetPageWithoutEmbeddingAsync(limit: 10);

        // Empty DB response → no items, no next page
        result.Should().NotBeNull();
        result.Items.Should().BeEmpty();
        result.HasNextPage.Should().BeFalse();
    }

    // ── Neo4jPreferenceRepository ────────────────────────────────────────

    [Fact]
    public async Task PreferenceRepository_GetPageWithoutEmbedding_SendsN1LimitToDatabase()
    {
        var (repo, calls) = CreatePreferencePagedReadCapture();

        await repo.GetPageWithoutEmbeddingAsync(limit: 20);

        calls.Should().ContainSingle();
        var param = calls[0].Parameters!;
        param.GetType().GetProperty("limit")!.GetValue(param).Should().Be(21); // limit+1
    }

    [Fact]
    public async Task PreferenceRepository_GetPageWithoutEmbedding_SendsCorrectCypher()
    {
        var (repo, calls) = CreatePreferencePagedReadCapture();

        await repo.GetPageWithoutEmbeddingAsync(limit: 5);

        calls[0].Cypher.Should().Contain("p.embedding IS NULL");
        calls[0].Cypher.Should().Contain("LIMIT $limit");
    }

    [Fact]
    public async Task PreferenceRepository_GetPageWithoutEmbedding_ReturnsPagedResult()
    {
        var (repo, _) = CreatePreferencePagedReadCapture();

        var result = await repo.GetPageWithoutEmbeddingAsync(limit: 10);

        result.Should().NotBeNull();
        result.Items.Should().BeEmpty();
        result.HasNextPage.Should().BeFalse();
    }
}
