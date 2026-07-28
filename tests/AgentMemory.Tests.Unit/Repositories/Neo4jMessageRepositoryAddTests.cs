using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Queries;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Unit.TestHelpers;
using Neo4j.Driver;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Repositories;

public sealed class Neo4jMessageRepositoryAddTests
{
    [Fact]
    public async Task AddAsync_EmbeddedMessage_UsesOneCombinedQuery()
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var transactionRunner = Substitute.For<INeo4jTransactionRunner>();
        transactionRunner
            .WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task<Message>>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var runner = Substitute.For<IAsyncQueryRunner>();
                runner
                    .RunAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object?>>())
                    .Returns(query =>
                    {
                        calls.Add((query.Arg<string>(), query.ArgAt<object>(1)));
                        return Task.FromResult((IResultCursor)new FakeResultCursor(MessageRecord()));
                    });
                runner
                    .RunAsync(Arg.Any<string>(), Arg.Any<object>())
                    .Returns(query =>
                    {
                        calls.Add((query.Arg<string>(), query.ArgAt<object>(1)));
                        return Task.FromResult((IResultCursor)new FakeResultCursor(MessageRecord()));
                    });
                return await call.Arg<Func<IAsyncQueryRunner, Task<Message>>>()(runner);
            });

        var repository = new Neo4jMessageRepository(
            transactionRunner, NullLogger<Neo4jMessageRepository>.Instance);
        var message = new Message
        {
            MessageId = "message-1",
            ConversationId = "conversation-1",
            SessionId = "session-1",
            Role = "assistant",
            Content = "Stored once.",
            TimestampUtc = new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.Zero),
            Embedding = [0.1f, 0.2f, 0.3f, 0.4f],
        };

        var result = await repository.AddAsync(message);

        result.MessageId.Should().Be(message.MessageId);
        calls.Should().ContainSingle(
            "message create, embedding, HAS_MESSAGE, FIRST_MESSAGE, and NEXT_MESSAGE must share one query");
        calls[0].Cypher.Should().Be(MessageQueries.Add);
        calls[0].Cypher.Should().Contain("FIRST_MESSAGE");
        calls[0].Cypher.Should().Contain("NEXT_MESSAGE");
        calls[0].Cypher.Should().Contain("RETURN persisted AS m",
            "the just-written embedding must not be echoed back in the result payload");

        var parameters = calls[0].Parameters.Should()
            .BeAssignableTo<IDictionary<string, object?>>().Subject;
        parameters["embedding"].Should().BeEquivalentTo(message.Embedding);
    }

    private static IRecord MessageRecord()
    {
        var timestamp = new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.Zero).ToString("O");
        var properties = new Dictionary<string, object>
        {
            ["id"] = "message-1",
            ["conversation_id"] = "conversation-1",
            ["session_id"] = "session-1",
            ["role"] = "assistant",
            ["content"] = "Stored once.",
            ["timestamp"] = timestamp,
            ["metadata"] = "{}",
        };
        var node = Substitute.For<INode>();
        foreach (var (key, value) in properties)
            node[key].Returns(value);
        node.Properties.Returns(properties);

        var record = Substitute.For<IRecord>();
        record["m"].Returns(node);
        return record;
    }
}
