using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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

    [Fact]
    public async Task AddBatchAsync_UsesOneQueryForMessagesEmbeddingsLinksAndReadBack()
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var transactionRunner = Substitute.For<INeo4jTransactionRunner>();
        var messages = Enumerable.Range(0, 3)
            .Select(index => new Message
            {
                MessageId = $"message-{index}",
                ConversationId = "conversation-1",
                SessionId = "session-1",
                Role = index % 2 == 0 ? "user" : "assistant",
                Content = $"Stored {index}.",
                TimestampUtc = new DateTimeOffset(2026, 7, 28, 12, 0, index, TimeSpan.Zero),
                Embedding = [index + 0.1f, index + 0.2f],
            })
            .ToArray();
        var records = messages.Select(BatchMessageRecord).ToArray();

        transactionRunner
            .WriteAsync(
                Arg.Any<Func<IAsyncQueryRunner, Task<List<Message>>>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var runner = Substitute.For<IAsyncQueryRunner>();
                runner
                    .RunAsync(Arg.Any<string>(), Arg.Any<object>())
                    .Returns(query =>
                    {
                        calls.Add((query.Arg<string>(), query.ArgAt<object>(1)));
                        return Task.FromResult((IResultCursor)new FakeResultCursor(records));
                    });
                return call.Arg<Func<IAsyncQueryRunner, Task<List<Message>>>>()(runner);
            });

        var repository = new Neo4jMessageRepository(
            transactionRunner, NullLogger<Neo4jMessageRepository>.Instance);

        var result = await repository.AddBatchAsync(messages);

        result.Select(message => message.MessageId)
            .Should().Equal(messages.Select(message => message.MessageId));
        result.Select(message => message.Embedding)
            .Should().BeEquivalentTo(messages.Select(message => message.Embedding));
        calls.Should().ContainSingle(
            "one UNWIND query must persist messages and embeddings, link their order, and return them");
        calls[0].Cypher.Should().Contain("msg.embedding");
        calls[0].Cypher.Should().Contain("NEXT_MESSAGE");
        calls[0].Cypher.Should().Contain("RETURN m");
        calls[0].Cypher.Should().Contain("WITH DISTINCT msg.id AS id");
    }

    private static IRecord BatchMessageRecord(Message message)
    {
        var properties = new Dictionary<string, object>
        {
            ["id"] = message.MessageId,
            ["conversation_id"] = message.ConversationId,
            ["session_id"] = message.SessionId,
            ["role"] = message.Role,
            ["content"] = message.Content,
            ["timestamp"] = message.TimestampUtc.ToString("O"),
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
