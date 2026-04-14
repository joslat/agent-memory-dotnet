using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Neo4j.Repositories;
using Neo4j.AgentMemory.Tests.Integration.Fixtures;

namespace Neo4j.AgentMemory.Tests.Integration.Repositories;

[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class MessageRepositoryIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jConversationRepository _convRepo;
    private readonly Neo4jMessageRepository _repo;

    // Small embedding matching the fixture's index dimension of 4
    private static readonly float[] TestEmbedding = [0.1f, 0.2f, 0.3f, 0.4f];
    private static readonly float[] QueryEmbedding = [0.1f, 0.2f, 0.3f, 0.4f];

    public MessageRepositoryIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _convRepo = new Neo4jConversationRepository(
            fixture.TransactionRunner,
            NullLogger<Neo4jConversationRepository>.Instance);
        _repo = new Neo4jMessageRepository(
            fixture.TransactionRunner,
            NullLogger<Neo4jMessageRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<Conversation> SeedConversationAsync(string? sessionId = null)
    {
        var conv = new Conversation
        {
            ConversationId = $"conv-{Guid.NewGuid():N}",
            SessionId = sessionId ?? $"session-{Guid.NewGuid():N}",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        return await _convRepo.UpsertAsync(conv);
    }

    [Fact]
    public async Task AddAsync_PersistsSingleMessage_WithRequiredProperties()
    {
        var conv = await SeedConversationAsync();
        var msg = new Message
        {
            MessageId = $"msg-{Guid.NewGuid():N}",
            ConversationId = conv.ConversationId,
            SessionId = conv.SessionId,
            Role = "user",
            Content = "Hello world",
            TimestampUtc = new DateTimeOffset(2025, 6, 1, 10, 0, 0, TimeSpan.Zero)
        };

        var result = await _repo.AddAsync(msg);

        result.MessageId.Should().Be(msg.MessageId);
        result.ConversationId.Should().Be(conv.ConversationId);
        result.Role.Should().Be("user");
        result.Content.Should().Be("Hello world");
    }

    [Fact]
    public async Task AddAsync_WithEmbedding_PersistsEmbedding()
    {
        var conv = await SeedConversationAsync();
        var msg = new Message
        {
            MessageId = $"msg-{Guid.NewGuid():N}",
            ConversationId = conv.ConversationId,
            SessionId = conv.SessionId,
            Role = "assistant",
            Content = "An embedded response",
            TimestampUtc = DateTimeOffset.UtcNow,
            Embedding = TestEmbedding
        };

        await _repo.AddAsync(msg);

        var fetched = await _repo.GetByIdAsync(msg.MessageId);
        fetched.Should().NotBeNull();
        fetched!.Embedding.Should().NotBeNull();
        fetched.Embedding!.Length.Should().Be(TestEmbedding.Length);
    }

    [Fact]
    public async Task AddBatchAsync_PersistsAllMessages()
    {
        var conv = await SeedConversationAsync();
        var now = DateTimeOffset.UtcNow;
        var messages = new[]
        {
            new Message
            {
                MessageId = $"msg-{Guid.NewGuid():N}",
                ConversationId = conv.ConversationId,
                SessionId = conv.SessionId,
                Role = "user",
                Content = "Batch message 1",
                TimestampUtc = now
            },
            new Message
            {
                MessageId = $"msg-{Guid.NewGuid():N}",
                ConversationId = conv.ConversationId,
                SessionId = conv.SessionId,
                Role = "assistant",
                Content = "Batch message 2",
                TimestampUtc = now.AddSeconds(1)
            },
            new Message
            {
                MessageId = $"msg-{Guid.NewGuid():N}",
                ConversationId = conv.ConversationId,
                SessionId = conv.SessionId,
                Role = "user",
                Content = "Batch message 3",
                TimestampUtc = now.AddSeconds(2)
            }
        };

        var results = await _repo.AddBatchAsync(messages);

        results.Should().HaveCount(3);
        results.Select(m => m.Content).Should().BeEquivalentTo(
            ["Batch message 1", "Batch message 2", "Batch message 3"]);
    }

    [Fact]
    public async Task GetRecentBySessionAsync_ReturnsLimitedMessages_OrderedNewestFirst()
    {
        var sessionId = $"session-{Guid.NewGuid():N}";
        var conv = await SeedConversationAsync(sessionId);
        var baseTime = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

        for (int i = 0; i < 5; i++)
        {
            await _repo.AddAsync(new Message
            {
                MessageId = $"msg-{i}-{Guid.NewGuid():N}",
                ConversationId = conv.ConversationId,
                SessionId = sessionId,
                Role = "user",
                Content = $"Message {i}",
                TimestampUtc = baseTime.AddMinutes(i)
            });
        }

        var results = await _repo.GetRecentBySessionAsync(sessionId, limit: 3);

        results.Should().HaveCount(3);
        // Should be ordered newest first
        results[0].Content.Should().Be("Message 4");
        results[1].Content.Should().Be("Message 3");
        results[2].Content.Should().Be("Message 2");
    }

    [Fact]
    public async Task GetByConversationAsync_ReturnsAllMessages_LinkedToConversation()
    {
        var conv = await SeedConversationAsync();
        var now = DateTimeOffset.UtcNow;

        await _repo.AddAsync(new Message
        {
            MessageId = $"msg-{Guid.NewGuid():N}",
            ConversationId = conv.ConversationId,
            SessionId = conv.SessionId,
            Role = "user",
            Content = "First",
            TimestampUtc = now
        });
        await _repo.AddAsync(new Message
        {
            MessageId = $"msg-{Guid.NewGuid():N}",
            ConversationId = conv.ConversationId,
            SessionId = conv.SessionId,
            Role = "assistant",
            Content = "Second",
            TimestampUtc = now.AddSeconds(1)
        });

        var results = await _repo.GetByConversationAsync(conv.ConversationId);

        results.Should().HaveCount(2);
        results.Select(m => m.ConversationId).Should().AllBe(conv.ConversationId);
    }

    [Fact]
    public async Task SearchByVectorAsync_ReturnsResults_WhenEmbeddingMatches()
    {
        var conv = await SeedConversationAsync();
        var msg = new Message
        {
            MessageId = $"msg-{Guid.NewGuid():N}",
            ConversationId = conv.ConversationId,
            SessionId = conv.SessionId,
            Role = "user",
            Content = "Semantic search candidate",
            TimestampUtc = DateTimeOffset.UtcNow,
            Embedding = TestEmbedding
        };
        await _repo.AddAsync(msg);

        var results = await _repo.SearchByVectorAsync(QueryEmbedding, limit: 5);

        results.Should().NotBeEmpty();
        results[0].Message.MessageId.Should().Be(msg.MessageId);
        results[0].Score.Should().BeGreaterThan(0.0);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsNull_WhenMessageDoesNotExist()
    {
        var result = await _repo.GetByIdAsync("nonexistent-message-id");

        result.Should().BeNull();
    }
}
