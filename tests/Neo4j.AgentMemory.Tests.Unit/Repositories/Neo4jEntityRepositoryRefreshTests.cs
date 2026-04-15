using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.AgentMemory.Neo4j.Infrastructure;
using Neo4j.AgentMemory.Neo4j.Repositories;
using Neo4j.Driver;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.Repositories;

/// <summary>
/// Unit tests for RefreshEntitySearchFieldsAsync (G10) and the post-merge refresh hook.
/// </summary>
public sealed class Neo4jEntityRepositoryRefreshTests
{
    private static (Neo4jEntityRepository Repo, List<(string Cypher, object? Parameters)> Calls)
        CreateVoidWriteCapture()
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        txRunner
            .WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task>>();
                var runner = Substitute.For<IAsyncQueryRunner>();
                runner
                    .RunAsync(Arg.Any<string>(), Arg.Any<object>())
                    .Returns(ci =>
                    {
                        calls.Add((ci.Arg<string>(), ci.ArgAt<object>(1)));
                        return Task.FromResult(Substitute.For<IResultCursor>());
                    });
                return work(runner);
            });
        return (new Neo4jEntityRepository(txRunner, NullLogger<Neo4jEntityRepository>.Instance), calls);
    }

    // RefreshEntitySearchFieldsAsync

    [Fact]
    public async Task RefreshEntitySearchFieldsAsync_SendsCorrectCypher()
    {
        var (repo, calls) = CreateVoidWriteCapture();
        await repo.RefreshEntitySearchFieldsAsync("ent-1");
        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Contain("MATCH (e:Entity {id: $entityId})");
        calls[0].Cypher.Should().Contain("SET e.updated_at = datetime($updatedAt)");
        calls[0].Cypher.Should().Contain("e.aliases");
    }

    [Fact]
    public async Task RefreshEntitySearchFieldsAsync_PassesEntityIdParameter()
    {
        var (repo, calls) = CreateVoidWriteCapture();
        await repo.RefreshEntitySearchFieldsAsync("ent-abc");
        var param = calls[0].Parameters!;
        param.GetType().GetProperty("entityId")!.GetValue(param).Should().Be("ent-abc");
    }

    [Fact]
    public async Task RefreshEntitySearchFieldsAsync_SetsUpdatedAtTimestamp()
    {
        var (repo, calls) = CreateVoidWriteCapture();
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        await repo.RefreshEntitySearchFieldsAsync("ent-ts");
        var param = calls[0].Parameters!;
        var updatedAt = param.GetType().GetProperty("updatedAt")!.GetValue(param) as string;
        updatedAt.Should().NotBeNullOrEmpty();
        var parsed = DateTimeOffset.Parse(updatedAt!, null, System.Globalization.DateTimeStyles.RoundtripKind);
        parsed.Should().BeOnOrAfter(before);
    }

    [Fact]
    public async Task RefreshEntitySearchFieldsAsync_UsesWriteTransaction()
    {
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        txRunner
            .WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var repo = new Neo4jEntityRepository(txRunner, NullLogger<Neo4jEntityRepository>.Instance);

        await repo.RefreshEntitySearchFieldsAsync("ent-1");

        await txRunner.Received(1).WriteAsync(
            Arg.Any<Func<IAsyncQueryRunner, Task>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshEntitySearchFieldsAsync_DoesNotThrow_WhenEntityMissing()
    {
        var (repo, _) = CreateVoidWriteCapture();
        // The Cypher is a MATCH — if no entity found, SET is silently skipped by Neo4j.
        // The repository method should complete without throwing.
        var act = async () => await repo.RefreshEntitySearchFieldsAsync("entity-does-not-exist");
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RefreshEntitySearchFieldsAsync_DeduplicatesAliasesInCypher()
    {
        var (repo, calls) = CreateVoidWriteCapture();
        await repo.RefreshEntitySearchFieldsAsync("ent-dedup");
        calls[0].Cypher.Should().Contain("coalesce(e.aliases, [])");
    }

    // MergeEntitiesAsync — post-merge refresh hook

    [Fact]
    public async Task MergeEntitiesAsync_CallsRefreshAfterMerge()
    {
        var (repo, calls) = CreateVoidWriteCapture();
        await repo.MergeEntitiesAsync("src-id", "tgt-id");

        // First call is the merge Cypher; second call is the refresh Cypher.
        calls.Should().HaveCount(2);
        calls[0].Cypher.Should().Contain("MATCH (source:Entity {id: $sourceEntityId})");
        calls[1].Cypher.Should().Contain("MATCH (e:Entity {id: $entityId})");
        calls[1].Cypher.Should().Contain("SET e.updated_at = datetime($updatedAt)");
    }

    [Fact]
    public async Task MergeEntitiesAsync_RefreshUsesTargetEntityId()
    {
        var (repo, calls) = CreateVoidWriteCapture();
        await repo.MergeEntitiesAsync("src-x", "tgt-42");

        calls.Should().HaveCount(2);
        var refreshParam = calls[1].Parameters!;
        refreshParam.GetType().GetProperty("entityId")!.GetValue(refreshParam).Should().Be("tgt-42");
    }

    [Fact]
    public async Task MergeEntitiesAsync_CypherAbsorbsSourceAliases()
    {
        var (repo, calls) = CreateVoidWriteCapture();
        await repo.MergeEntitiesAsync("src", "tgt");
        calls[0].Cypher.Should().Contain("source.aliases");
    }

    [Fact]
    public async Task MergeEntitiesAsync_CypherMergesDescription()
    {
        var (repo, calls) = CreateVoidWriteCapture();
        await repo.MergeEntitiesAsync("src", "tgt");
        calls[0].Cypher.Should().Contain("target.description");
        calls[0].Cypher.Should().Contain("source.description");
    }

    [Fact]
    public async Task MergeEntitiesAsync_SetsUpdatedAtOnTarget()
    {
        var (repo, calls) = CreateVoidWriteCapture();
        await repo.MergeEntitiesAsync("src", "tgt");
        calls[0].Cypher.Should().Contain("target.updated_at");
    }
}
