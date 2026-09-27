using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// A forgotten message is never recalled again (recent messages, message search, whole-session reads) but is
/// kept for history: as-of reads of times before it was forgotten still return it. Found live: after forgetting a
/// person and every fact about them, the agent still answered from the message that named them.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class MessageForgetIntegrationTests : IAsyncLifetime
{
    private static readonly float[] Embedding = [0.1f, 0.2f, 0.3f, 0.4f];

    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jConversationRepository _conversations;
    private readonly Neo4jMessageRepository _messages;

    public MessageForgetIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _conversations = new Neo4jConversationRepository(fixture.TransactionRunner, NullLogger<Neo4jConversationRepository>.Instance);
        _messages = new Neo4jMessageRepository(fixture.TransactionRunner, NullLogger<Neo4jMessageRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<(string Session, string Forgotten, string Kept)> SeedAsync()
    {
        var session = $"session-{Guid.NewGuid():N}";
        var conversation = await _conversations.UpsertAsync(new Conversation
        {
            ConversationId = $"conv-{Guid.NewGuid():N}", SessionId = session,
            CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow,
        });
        Message M(string id, string text, int minutesAgo) => new()
        {
            MessageId = id, ConversationId = conversation.ConversationId, SessionId = session, Role = "user",
            Content = text, TimestampUtc = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo), Embedding = Embedding,
        };
        await _messages.AddAsync(M("m-forgotten", "My brother Pablo lives in Seville.", 10));
        await _messages.AddAsync(M("m-kept", "My sister Lena lives in Berlin.", 5));
        return (session, "m-forgotten", "m-kept");
    }

    [Fact]
    public async Task A_forgotten_message_leaves_every_recall_read()
    {
        var (session, forgotten, kept) = await SeedAsync();

        (await _messages.InvalidateAsync(forgotten)).Should().BeTrue();

        (await _messages.GetRecentBySessionAsync(session, 10)).Select(m => m.MessageId).Should().Equal(kept);
        (await _messages.GetAllBySessionAsync(session)).Select(m => m.MessageId).Should().Equal(kept);
        (await _messages.SearchByVectorAsync(Embedding, session, 10, 0.0)).Select(r => r.Message.MessageId).Should().Equal(kept);
        (await _messages.SearchByVectorAsync(Embedding, null, 10, 0.0)).Select(r => r.Message.MessageId).Should().Equal(kept);
    }

    [Fact]
    public async Task History_before_the_forget_still_has_it()
    {
        var (session, forgotten, _) = await SeedAsync();
        var before = DateTimeOffset.UtcNow;
        await Task.Delay(50);

        await _messages.InvalidateAsync(forgotten);

        (await _messages.GetRecentBySessionAsOfAsync(session, before, 10)).Select(m => m.MessageId).Should().Contain(forgotten);
        (await _messages.GetRecentBySessionAsOfAsync(session, DateTimeOffset.UtcNow.AddSeconds(1), 10))
            .Select(m => m.MessageId).Should().NotContain(forgotten);
        (await _messages.GetByIdAsync(forgotten)).Should().NotBeNull("forgotten, not deleted");
    }

    [Fact]
    public async Task Forgetting_is_idempotent_and_an_unknown_message_is_reported()
    {
        var (_, forgotten, _) = await SeedAsync();

        (await _messages.InvalidateAsync(forgotten)).Should().BeTrue();
        (await _messages.InvalidateAsync(forgotten)).Should().BeTrue();
        (await _messages.InvalidateAsync("no-such-message")).Should().BeFalse();
    }
}
