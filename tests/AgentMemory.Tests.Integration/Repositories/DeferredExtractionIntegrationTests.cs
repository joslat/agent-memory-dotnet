using AgentMemory.Abstractions.Domain;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// 37.4, on a real store: a turn that waits is marked on its stored message; the mark is per owner, read oldest
/// first, cleared once extracted, and a forgotten message never comes back.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class DeferredExtractionIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jMessageRepository _messages;
    private readonly Neo4jConversationRepository _conversations;

    public DeferredExtractionIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _messages = new Neo4jMessageRepository(fixture.TransactionRunner, NullLogger<Neo4jMessageRepository>.Instance);
        _conversations = new Neo4jConversationRepository(fixture.TransactionRunner, NullLogger<Neo4jConversationRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<Message> SaidAsync(string session, string content, int minute)
    {
        await _conversations.UpsertAsync(new Conversation
        {
            ConversationId = "c-" + session, SessionId = session, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow,
        });
        return await _messages.AddAsync(new Message
        {
            MessageId = Guid.NewGuid().ToString("N"), ConversationId = "c-" + session, SessionId = session, Role = "user",
            Content = content, TimestampUtc = DateTimeOffset.UnixEpoch.AddMinutes(minute),
        });
    }

    [Fact]
    public async Task What_waits_is_read_back_per_owner_oldest_first_and_cleared_once_extracted()
    {
        var first = await SaidAsync("s1", "What's a good hike?", 1);
        var second = await SaidAsync("s1", "Is it going to rain?", 2);
        var other = await SaidAsync("s2", "Where is Porto?", 3);
        await _messages.SetExtractionDeferredAsync([second.MessageId, first.MessageId], "owner:alice");
        await _messages.SetExtractionDeferredAsync([other.MessageId], "owner:bob");

        (await _messages.GetExtractionDeferredAsync("owner:alice", 10)).Select(m => m.Content)
            .Should().Equal("What's a good hike?", "Is it going to rain?");

        await _messages.SetExtractionDeferredAsync([first.MessageId, second.MessageId], heldFor: null);

        (await _messages.GetExtractionDeferredAsync("owner:alice", 10)).Should().BeEmpty();
        (await _messages.GetExtractionDeferredAsync("owner:bob", 10)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_forgotten_message_never_waits()
    {
        var question = await SaidAsync("s1", "What's a good hike?", 1);
        await _messages.SetExtractionDeferredAsync([question.MessageId], "owner:alice");

        await _messages.InvalidateAsync(question.MessageId);

        (await _messages.GetExtractionDeferredAsync("owner:alice", 10)).Should().BeEmpty();
    }
}
