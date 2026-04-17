using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Neo4j.Infrastructure;
using Neo4j.AgentMemory.Neo4j.Queries;
using Neo4j.AgentMemory.Neo4j.Repositories;
using Neo4j.AgentMemory.Tests.Unit.TestHelpers;
using Neo4j.Driver;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.Repositories;

/// <summary>
/// Unit tests for <see cref="Neo4jConversationRepository.ListSessionsAsync"/> (G3).
/// </summary>
public sealed class Neo4jConversationRepositoryListSessionsTests
{
    private static (Neo4jConversationRepository Repo, List<(string Cypher, object? Parameters)> Calls)
        CreateReadCapture(params IRecord[] records)
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var txRunner = Substitute.For<INeo4jTransactionRunner>();

        txRunner
            .ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<List<SessionSummary>>>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task<List<SessionSummary>>>>();
                var runner = Substitute.For<IAsyncQueryRunner>();
                runner
                    .RunAsync(Arg.Any<string>(), Arg.Any<object>())
                    .Returns(ci =>
                    {
                        calls.Add((ci.Arg<string>(), ci.ArgAt<object>(1)));
                        return Task.FromResult((IResultCursor)new FakeResultCursor(records));
                    });
                return await work(runner);
            });

        return (new Neo4jConversationRepository(txRunner, NullLogger<Neo4jConversationRepository>.Instance), calls);
    }

    [Fact]
    public async Task ListSessionsAsync_SendsCorrectCypher()
    {
        var (repo, calls) = CreateReadCapture();

        await repo.ListSessionsAsync(25);

        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Be(ConversationQueries.ListSessions);
    }

    [Fact]
    public async Task ListSessionsAsync_PassesLimitParameter()
    {
        var (repo, calls) = CreateReadCapture();

        await repo.ListSessionsAsync(25);

        var param = calls[0].Parameters!;
        param.GetType().GetProperty("limit")!.GetValue(param).Should().Be(25);
    }

    [Fact]
    public async Task ListSessionsAsync_DefaultLimitIs50()
    {
        var (repo, calls) = CreateReadCapture();

        await repo.ListSessionsAsync();

        var param = calls[0].Parameters!;
        param.GetType().GetProperty("limit")!.GetValue(param).Should().Be(50);
    }

    [Fact]
    public async Task ListSessionsAsync_ReturnsEmptyWhenNoSessions()
    {
        var (repo, _) = CreateReadCapture();

        var result = await repo.ListSessionsAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ListSessionsAsync_CypherContainsSessionAggregation()
    {
        var (repo, calls) = CreateReadCapture();

        await repo.ListSessionsAsync();

        calls[0].Cypher.Should().Contain("MATCH (c:Conversation)");
        calls[0].Cypher.Should().Contain("c.session_id AS sessionId");
        calls[0].Cypher.Should().Contain("LIMIT $limit");
        calls[0].Cypher.Should().Contain("ORDER BY lastActivity DESC");
    }

    // ── Edge cases ──

    [Fact]
    public async Task ListSessionsAsync_CustomLimit_PassedCorrectly()
    {
        var (repo, calls) = CreateReadCapture();

        await repo.ListSessionsAsync(100);

        var param = calls[0].Parameters!;
        param.GetType().GetProperty("limit")!.GetValue(param).Should().Be(100);
    }

    [Fact]
    public async Task ListSessionsAsync_LimitOfOne_PassedCorrectly()
    {
        var (repo, calls) = CreateReadCapture();

        await repo.ListSessionsAsync(1);

        var param = calls[0].Parameters!;
        param.GetType().GetProperty("limit")!.GetValue(param).Should().Be(1);
    }

    [Fact]
    public async Task ListSessionsAsync_SessionWithNullPreviewAndActivity_ReturnsNulls()
    {
        var record = Substitute.For<IRecord>();
        record["sessionId"].Returns("session-1");
        record["convCount"].Returns(1);
        record["msgCount"].Returns(0);
        record["lastPreview"].Returns((object?)null);
        record["lastActivity"].Returns((object?)null);

        var (repo, _) = CreateReadCapture(record);

        var result = await repo.ListSessionsAsync();

        result.Should().ContainSingle();
        result[0].SessionId.Should().Be("session-1");
        result[0].LastMessagePreview.Should().BeNull();
        result[0].LastActivity.Should().BeNull();
    }

    [Fact]
    public async Task ListSessionsAsync_UsesReadTransaction()
    {
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        txRunner
            .ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<List<SessionSummary>>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<SessionSummary>()));
        var repo = new Neo4jConversationRepository(txRunner, NullLogger<Neo4jConversationRepository>.Instance);

        await repo.ListSessionsAsync();

        await txRunner.Received(1).ReadAsync(
            Arg.Any<Func<IAsyncQueryRunner, Task<List<SessionSummary>>>>(),
            Arg.Any<CancellationToken>());
    }
}
