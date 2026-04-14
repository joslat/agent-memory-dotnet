using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Neo4j.Repositories;
using Neo4j.AgentMemory.Tests.Integration.Fixtures;

namespace Neo4j.AgentMemory.Tests.Integration.Repositories;

[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class ConversationRepositoryIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jConversationRepository _repo;

    public ConversationRepositoryIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _repo = new Neo4jConversationRepository(
            fixture.TransactionRunner,
            NullLogger<Neo4jConversationRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task UpsertAsync_CreatesConversation_WithRequiredProperties()
    {
        var conv = new Conversation
        {
            ConversationId = $"conv-{Guid.NewGuid():N}",
            SessionId = $"session-{Guid.NewGuid():N}",
            UserId = "user-42",
            CreatedAtUtc = new DateTimeOffset(2025, 6, 1, 10, 0, 0, TimeSpan.Zero),
            UpdatedAtUtc = new DateTimeOffset(2025, 6, 1, 10, 0, 0, TimeSpan.Zero)
        };

        var result = await _repo.UpsertAsync(conv);

        result.ConversationId.Should().Be(conv.ConversationId);
        result.SessionId.Should().Be(conv.SessionId);
        result.UserId.Should().Be("user-42");
    }

    [Fact]
    public async Task UpsertAsync_WithTitle_PersistsTitle()
    {
        var conv = new Conversation
        {
            ConversationId = $"conv-{Guid.NewGuid():N}",
            SessionId = $"session-{Guid.NewGuid():N}",
            Title = "My Integration Test Conversation",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        var result = await _repo.UpsertAsync(conv);

        result.Title.Should().Be("My Integration Test Conversation");
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsConversation_WhenExists()
    {
        var conv = new Conversation
        {
            ConversationId = $"conv-{Guid.NewGuid():N}",
            SessionId = $"session-{Guid.NewGuid():N}",
            Title = "Round-Trip Test",
            UserId = "user-1",
            CreatedAtUtc = new DateTimeOffset(2025, 1, 15, 9, 30, 0, TimeSpan.Zero),
            UpdatedAtUtc = new DateTimeOffset(2025, 1, 15, 9, 30, 0, TimeSpan.Zero)
        };
        await _repo.UpsertAsync(conv);

        var result = await _repo.GetByIdAsync(conv.ConversationId);

        result.Should().NotBeNull();
        result!.ConversationId.Should().Be(conv.ConversationId);
        result.SessionId.Should().Be(conv.SessionId);
        result.Title.Should().Be("Round-Trip Test");
        result.UserId.Should().Be("user-1");
        result.CreatedAtUtc.Should().BeCloseTo(conv.CreatedAtUtc, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsNull_WhenNotFound()
    {
        var result = await _repo.GetByIdAsync("definitely-does-not-exist");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetBySessionAsync_ReturnsOnlyConversationsForSession()
    {
        var sessionId = $"session-{Guid.NewGuid():N}";
        var conv1 = new Conversation
        {
            ConversationId = $"conv-{Guid.NewGuid():N}",
            SessionId = sessionId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        var conv2 = new Conversation
        {
            ConversationId = $"conv-{Guid.NewGuid():N}",
            SessionId = sessionId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(1)
        };
        var other = new Conversation
        {
            ConversationId = $"conv-{Guid.NewGuid():N}",
            SessionId = $"session-other-{Guid.NewGuid():N}",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        await _repo.UpsertAsync(conv1);
        await _repo.UpsertAsync(conv2);
        await _repo.UpsertAsync(other);

        var results = await _repo.GetBySessionAsync(sessionId);

        results.Should().HaveCount(2);
        results.Select(c => c.ConversationId).Should().BeEquivalentTo(
            new[] { conv1.ConversationId, conv2.ConversationId });
    }

    [Fact]
    public async Task GetBySessionAsync_ReturnsOrderedByUpdatedAtDescending()
    {
        var sessionId = $"session-{Guid.NewGuid():N}";
        var older = new Conversation
        {
            ConversationId = $"conv-older-{Guid.NewGuid():N}",
            SessionId = sessionId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = new DateTimeOffset(2025, 1, 1, 8, 0, 0, TimeSpan.Zero)
        };
        var newer = new Conversation
        {
            ConversationId = $"conv-newer-{Guid.NewGuid():N}",
            SessionId = sessionId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = new DateTimeOffset(2025, 1, 2, 8, 0, 0, TimeSpan.Zero)
        };

        await _repo.UpsertAsync(older);
        await _repo.UpsertAsync(newer);

        var results = await _repo.GetBySessionAsync(sessionId);

        results.Should().HaveCount(2);
        results[0].ConversationId.Should().Be(newer.ConversationId);
        results[1].ConversationId.Should().Be(older.ConversationId);
    }

    [Fact]
    public async Task DeleteAsync_RemovesConversation()
    {
        var conv = new Conversation
        {
            ConversationId = $"conv-{Guid.NewGuid():N}",
            SessionId = $"session-{Guid.NewGuid():N}",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        await _repo.UpsertAsync(conv);

        await _repo.DeleteAsync(conv.ConversationId);

        var result = await _repo.GetByIdAsync(conv.ConversationId);
        result.Should().BeNull();
    }

    [Fact]
    public async Task UpsertAsync_UpdatesTitle_OnSecondCall()
    {
        var convId = $"conv-{Guid.NewGuid():N}";
        var original = new Conversation
        {
            ConversationId = convId,
            SessionId = $"session-{Guid.NewGuid():N}",
            Title = "Original Title",
            CreatedAtUtc = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            UpdatedAtUtc = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };
        await _repo.UpsertAsync(original);

        var updated = original with
        {
            Title = "Updated Title",
            UpdatedAtUtc = new DateTimeOffset(2025, 1, 2, 0, 0, 0, TimeSpan.Zero)
        };
        await _repo.UpsertAsync(updated);

        var fetched = await _repo.GetByIdAsync(convId);
        fetched!.Title.Should().Be("Updated Title");
    }
}
