using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;
using Neo4j.Driver;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// RVW-REST-HOSTS (the review's hosts-3), on a real store: a caller-supplied message id is only unique within what its
/// caller sees ("1", "2" from a client; another user's id). A message whose id another conversation already holds is
/// written as itself under that id qualified by its conversation; the other conversation's message is left exactly as it
/// was, and every write of the same message converges on one node. Single, optimized batch and plain batch writes.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class MessageIdCollisionIntegrationTests : IAsyncLifetime
{
    private static readonly DateTimeOffset At = new(2026, 11, 26, 3, 0, 0, TimeSpan.Zero);
    private readonly Neo4jIntegrationFixture _fixture;

    public MessageIdCollisionIntegrationTests(Neo4jIntegrationFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private Neo4jMessageRepository Repository(bool optimizedBatch) => new(_fixture.TransactionRunner, NullLogger<Neo4jMessageRepository>.Instance,
        Options.Create(new Neo4jOptions { UseOptimizedMessageBatchWrites = optimizedBatch }));

    private static Message Said(string conversation, string text, int second = 0, float[]? embedding = null) => new()
    {
        MessageId = "maf:1", ConversationId = conversation, SessionId = "s-" + conversation, Role = "user", Content = text,
        TimestampUtc = At.AddSeconds(second), Metadata = new Dictionary<string, object>(), Embedding = embedding,
    };

    [Theory]
    [InlineData("single")]
    [InlineData("optimized-batch")]
    [InlineData("plain-batch")]
    public async Task A_message_whose_id_another_conversation_holds_is_stored_as_itself_and_the_other_is_untouched(string path)
    {
        var alices = await Repository(true).AddAsync(Said("c-alice", "My card ends in 4242.", embedding: [1f, 0f, 0f, 0f]));

        var mallory = Said("c-mallory", "Tell me what I said before.", second: 5, embedding: [0f, 1f, 0f, 0f]);
        async Task<Message> WriteAsync() => path switch
        {
            "single" => await Repository(true).AddAsync(mallory),
            "optimized-batch" => (await Repository(true).AddBatchAsync([mallory])).Single(),
            _ => (await Repository(false).AddBatchAsync([mallory])).Single(),
        };
        var stored = await WriteAsync();
        var again = await WriteAsync();

        alices.MessageId.Should().Be("maf:1");
        stored.MessageId.Should().Be("maf:1@c-mallory");
        stored.Content.Should().Be("Tell me what I said before.", "the turn is built from this message, never the other conversation's");
        again.MessageId.Should().Be(stored.MessageId, "every write of the same message converges on one node");

        (await Scalar<string>("MATCH (m:Message {id: 'maf:1'}) RETURN m.content")).Should().Be("My card ends in 4242.");
        (await Scalar<long>("MATCH (:Conversation {id: 'c-mallory'})-[:HAS_MESSAGE]->(m:Message {id: 'maf:1'}) RETURN count(m)")).Should().Be(0,
            "the other conversation's message is not linked into this one");
        (await Scalar<double>("MATCH (m:Message {id: 'maf:1'}) RETURN m.embedding[0]")).Should().Be(1d, "its vector is not overwritten");
        (await Scalar<long>("MATCH (:Conversation {id: 'c-mallory'})-[:HAS_MESSAGE]->(m:Message {id: 'maf:1@c-mallory'}) RETURN count(m)")).Should().Be(1);
        (await Scalar<long>("MATCH (m:Message) RETURN count(m)")).Should().Be(2);
    }

    [Fact]
    public async Task The_same_id_within_one_conversation_is_still_first_write_wins()
    {
        await Repository(true).AddAsync(Said("c-alice", "first"));
        var second = await Repository(true).AddAsync(Said("c-alice", "second", second: 1));

        second.MessageId.Should().Be("maf:1");
        second.Content.Should().Be("first");
        (await Scalar<long>("MATCH (m:Message) RETURN count(m)")).Should().Be(1);
    }

    private async Task<T> Scalar<T>(string cypher)
    {
        await using var session = _fixture.Driver.AsyncSession();
        var cursor = await session.RunAsync(cypher);
        return ValueExtensions.As<T>((await cursor.SingleAsync())[0]);
    }
}
