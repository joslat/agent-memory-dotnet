using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Neo4j.Infrastructure;
using Neo4j.AgentMemory.Neo4j.Repositories;
using Neo4j.AgentMemory.Tests.Unit.TestHelpers;
using Neo4j.Driver;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.Repositories;

public sealed class Neo4jConversationRepositoryTitleTests
{
    private static IRecord CreateConversationRecord(string? title)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        var props = new Dictionary<string, object>
        {
            ["id"] = "conv-1",
            ["session_id"] = "sess-1",
            ["created_at"] = now,
            ["updated_at"] = now
        };
        if (title != null)
            props["title"] = title;

        var node = Substitute.For<INode>();
        node["id"].Returns((object)"conv-1");
        node["session_id"].Returns((object)"sess-1");
        node["created_at"].Returns((object)now);
        node["updated_at"].Returns((object)now);
        node.Properties.Returns(props);

        var record = Substitute.For<IRecord>();
        record["c"].Returns(node);
        return record;
    }

    private static (Neo4jConversationRepository Repo, List<(string Cypher, object? Parameters)> Calls)
        CreateUpsertCapture(string? titleInNode)
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        txRunner
            .WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task<Conversation>>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task<Conversation>>>();
                var runner = Substitute.For<IAsyncQueryRunner>();
                var fakeRecord = CreateConversationRecord(titleInNode);
                runner
                    .RunAsync(Arg.Any<string>(), Arg.Any<object>())
                    .Returns(ci =>
                    {
                        calls.Add((ci.Arg<string>(), ci.ArgAt<object>(1)));
                        return Task.FromResult((IResultCursor)new FakeResultCursor(fakeRecord));
                    });
                return await work(runner);
            });
        return (new Neo4jConversationRepository(txRunner, NullLogger<Neo4jConversationRepository>.Instance), calls);
    }

    private static (Neo4jConversationRepository Repo, List<(string Cypher, object? Parameters)> Calls)
        CreateGetByIdCapture(string? titleInNode)
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        txRunner
            .ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<Conversation?>>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task<Conversation?>>>();
                var runner = Substitute.For<IAsyncQueryRunner>();
                var fakeRecord = CreateConversationRecord(titleInNode);
                runner
                    .RunAsync(Arg.Any<string>(), Arg.Any<object>())
                    .Returns(ci =>
                    {
                        calls.Add((ci.Arg<string>(), ci.ArgAt<object>(1)));
                        return Task.FromResult((IResultCursor)new FakeResultCursor(fakeRecord));
                    });
                return await work(runner);
            });
        return (new Neo4jConversationRepository(txRunner, NullLogger<Neo4jConversationRepository>.Instance), calls);
    }

    // ── Upsert includes title ──

    [Fact]
    public async Task UpsertAsync_CypherIncludesTitleProperty()
    {
        var (repo, calls) = CreateUpsertCapture("My Chat");
        var conv = new Conversation
        {
            ConversationId = "conv-1", SessionId = "sess-1", Title = "My Chat",
            CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        await repo.UpsertAsync(conv);
        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Contain("c.title");
    }

    [Fact]
    public async Task UpsertAsync_PassesTitleParameter()
    {
        var (repo, calls) = CreateUpsertCapture("My Chat");
        var conv = new Conversation
        {
            ConversationId = "conv-1", SessionId = "sess-1", Title = "My Chat",
            CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        await repo.UpsertAsync(conv);
        var param = calls[0].Parameters!;
        param.GetType().GetProperty("title")!.GetValue(param).Should().Be("My Chat");
    }

    [Fact]
    public async Task UpsertAsync_TitleIncludedInOnCreateSet()
    {
        var (repo, calls) = CreateUpsertCapture("My Chat");
        var conv = new Conversation
        {
            ConversationId = "conv-1", SessionId = "sess-1", Title = "My Chat",
            CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        await repo.UpsertAsync(conv);
        calls[0].Cypher.Should().Contain("ON CREATE SET");
        calls[0].Cypher.Should().Contain("c.title       = $title");
    }

    [Fact]
    public async Task UpsertAsync_TitleIncludedInOnMatchSet()
    {
        var (repo, calls) = CreateUpsertCapture("My Chat");
        var conv = new Conversation
        {
            ConversationId = "conv-1", SessionId = "sess-1", Title = "My Chat",
            CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        await repo.UpsertAsync(conv);
        calls[0].Cypher.Should().Contain("ON MATCH SET");
    }

    [Fact]
    public async Task UpsertAsync_NullTitle_PassesNullParameter()
    {
        var (repo, calls) = CreateUpsertCapture(null);
        var conv = new Conversation
        {
            ConversationId = "conv-1", SessionId = "sess-1", Title = null,
            CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        await repo.UpsertAsync(conv);
        var param = calls[0].Parameters!;
        param.GetType().GetProperty("title")!.GetValue(param).Should().BeNull();
    }

    // ── GetByIdAsync returns title ──

    [Fact]
    public async Task GetByIdAsync_ReturnsTitleFromNode()
    {
        var (repo, _) = CreateGetByIdCapture("Saved Title");
        var result = await repo.GetByIdAsync("conv-1");
        result.Should().NotBeNull();
        result!.Title.Should().Be("Saved Title");
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsNullTitle_WhenNotSet()
    {
        var (repo, _) = CreateGetByIdCapture(null);
        var result = await repo.GetByIdAsync("conv-1");
        result.Should().NotBeNull();
        result!.Title.Should().BeNull();
    }

    // ── Domain model ──

    [Fact]
    public void Conversation_TitleProperty_CanBeSet()
    {
        var conv = new Conversation
        {
            ConversationId = "c1", SessionId = "s1", Title = "Test Title",
            CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        conv.Title.Should().Be("Test Title");
    }

    [Fact]
    public void Conversation_TitleProperty_DefaultsToNull()
    {
        var conv = new Conversation
        {
            ConversationId = "c1", SessionId = "s1",
            CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        conv.Title.Should().BeNull();
    }
}
