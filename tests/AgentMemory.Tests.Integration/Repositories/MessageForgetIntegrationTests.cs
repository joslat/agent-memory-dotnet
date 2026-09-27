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
    private string _conversationId = "";

    public MessageForgetIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _conversations = new Neo4jConversationRepository(fixture.TransactionRunner, NullLogger<Neo4jConversationRepository>.Instance);
        _messages = new Neo4jMessageRepository(fixture.TransactionRunner, NullLogger<Neo4jMessageRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<(string Session, string Forgotten, string Kept)> SeedAsync(int createdMinutesAgo = 0)
    {
        var session = $"session-{Guid.NewGuid():N}";
        var conversation = await _conversations.UpsertAsync(new Conversation
        {
            ConversationId = $"conv-{Guid.NewGuid():N}", SessionId = session,
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-createdMinutesAgo), UpdatedAtUtc = DateTimeOffset.UtcNow,
        });
        Message M(string id, string text, int minutesAgo) => new()
        {
            MessageId = id, ConversationId = conversation.ConversationId, SessionId = session, Role = "user",
            Content = text, TimestampUtc = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo), Embedding = Embedding,
        };
        _conversationId = conversation.ConversationId;
        var (forgotten, kept) = ($"m-forgotten-{session[^8..]}", $"m-kept-{session[^8..]}");
        await _messages.AddAsync(M(forgotten, "My brother Pablo lives in Seville.", 10));
        await _messages.AddAsync(M(kept, "My sister Lena lives in Berlin.", 5));
        return (session, forgotten, kept);
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

    // ── G-30 review ─────────────────────────────────────────────────────

    [Fact]
    public async Task A_forgotten_message_is_not_in_the_conversation_read_and_says_when_it_was_forgotten()
    {
        // Retroactive conversation extraction reads the conversation: a forgotten message there is re-learned.
        var (_, forgotten, kept) = await SeedAsync();

        await _messages.InvalidateAsync(forgotten);

        (await _messages.GetByConversationAsync(_conversationId)).Select(m => m.MessageId).Should().Equal(kept);
        (await _messages.GetByIdAsync(forgotten))!.InvalidatedAtUtc.Should().NotBeNull();
        (await _messages.GetByIdAsync(kept))!.InvalidatedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task A_forgotten_last_message_is_not_the_session_preview()
    {
        var (session, _, kept) = await SeedAsync();

        await _messages.InvalidateAsync(kept);   // the newest

        var summary = (await _conversations.ListSessionsAsync()).Single(s => s.SessionId == session);
        summary.LastMessagePreview.Should().Contain("Pablo", "the newest live message is the preview");
    }

    [Fact]
    public async Task The_search_across_sessions_is_not_left_short_by_forgotten_messages()
    {
        // The forgotten message is the best match, so an index asked for exactly `limit` returned it and one more,
        // and the filter afterwards left one.
        var session = $"session-{Guid.NewGuid():N}";
        var conversation = await _conversations.UpsertAsync(new Conversation
        {
            ConversationId = $"conv-{Guid.NewGuid():N}", SessionId = session,
            CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow,
        });
        Message M(string id, float[] embedding) => new()
        {
            MessageId = id, ConversationId = conversation.ConversationId, SessionId = session, Role = "user",
            Content = id, TimestampUtc = DateTimeOffset.UtcNow, Embedding = embedding,
        };
        await _messages.AddAsync(M("best-forgotten", Embedding));
        await _messages.AddAsync(M("second", [0.1f, 0.2f, 0.3f, 0.35f]));
        await _messages.AddAsync(M("third", [0.1f, 0.2f, 0.25f, 0.4f]));
        await _messages.InvalidateAsync("best-forgotten");

        var results = await _messages.SearchByVectorAsync(Embedding, sessionId: null, limit: 2, minScore: 0.0);

        results.Select(r => r.Message.MessageId).Should().BeEquivalentTo(["second", "third"]);
    }

    // ── G-30 review round 2 ─────────────────────────────────────────────

    [Fact]
    public async Task The_search_across_sessions_still_returns_at_most_the_limit()
    {
        // The index is over-fetched so forgotten messages cannot leave the result short; the caller still gets
        // `limit`, not the whole candidate pool.
        var session = $"session-{Guid.NewGuid():N}";
        var conversation = await _conversations.UpsertAsync(new Conversation
        {
            ConversationId = $"conv-{Guid.NewGuid():N}", SessionId = session,
            CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow,
        });
        for (var i = 0; i < 6; i++)
            await _messages.AddAsync(new Message
            {
                MessageId = $"live-{i}", ConversationId = conversation.ConversationId, SessionId = session, Role = "user",
                Content = $"message {i}", TimestampUtc = DateTimeOffset.UtcNow, Embedding = Embedding,
            });

        var results = await _messages.SearchByVectorAsync(Embedding, sessionId: null, limit: 2, minScore: 0.0);

        results.Should().HaveCount(2);
    }

    [Fact]
    public async Task An_old_session_with_everything_forgotten_does_not_jump_to_the_top()
    {
        // Nothing live leaves no last activity; a null sorted first under DESC and pushed live sessions out.
        // As in life, a session is created before its messages: the older one an hour ago, the newer 30 minutes ago.
        var (older, a, b) = await SeedAsync(createdMinutesAgo: 60);
        var (newer, _, _) = await SeedAsync(createdMinutesAgo: 30);
        await _messages.InvalidateAsync(a);
        await _messages.InvalidateAsync(b);

        var sessions = await _conversations.ListSessionsAsync();

        sessions.Select(s => s.SessionId).Should().ContainInOrder(newer, older);
    }
}
