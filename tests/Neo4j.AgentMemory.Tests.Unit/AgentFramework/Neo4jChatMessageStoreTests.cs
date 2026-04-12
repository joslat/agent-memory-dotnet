using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.AgentFramework;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Neo4j.AgentMemory.Tests.Unit.AgentFramework;

public sealed class Neo4jChatMessageStoreTests
{
    private readonly IMemoryService _memoryService = Substitute.For<IMemoryService>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IIdGenerator _idGen = Substitute.For<IIdGenerator>();
    private readonly Neo4jChatMessageStore _sut;

    private static readonly DateTimeOffset _now = new(2025, 1, 28, 12, 0, 0, TimeSpan.Zero);

    public Neo4jChatMessageStoreTests()
    {
        _clock.UtcNow.Returns(_now);
        _idGen.GenerateId().Returns("test-id");
        _sut = new Neo4jChatMessageStore(
            _memoryService,
            _clock,
            _idGen,
            NullLogger<Neo4jChatMessageStore>.Instance);
    }

    // ── AddMessageAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task AddMessageAsync_CallsMemoryServiceWithCorrectArgs()
    {
        var expected = new Message
        {
            MessageId = "test-id", SessionId = "s1", ConversationId = "c1",
            Role = "user", Content = "Hello", TimestampUtc = _now
        };
        _memoryService.AddMessageAsync("s1", "c1", "user", "Hello", Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(expected);

        var chatMsg = new ChatMessage(ChatRole.User, "Hello");
        var result = await _sut.AddMessageAsync(chatMsg, "s1", "c1");

        result.Role.Should().Be("user");
        result.Content.Should().Be("Hello");
        await _memoryService.Received(1).AddMessageAsync("s1", "c1", "user", "Hello",
            Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddMessageAsync_AssistantMessage_MapsRoleCorrectly()
    {
        var expected = new Message
        {
            MessageId = "id2", SessionId = "s1", ConversationId = "c1",
            Role = "assistant", Content = "Hi there", TimestampUtc = _now
        };
        _memoryService.AddMessageAsync(Arg.Any<string>(), Arg.Any<string>(), "assistant", Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(expected);

        var result = await _sut.AddMessageAsync(new ChatMessage(ChatRole.Assistant, "Hi there"), "s1", "c1");

        result.Role.Should().Be("assistant");
    }

    [Fact]
    public async Task AddMessageAsync_ServiceThrows_ReturnsFallbackMessage()
    {
        _memoryService.AddMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("DB error"));

        var result = await _sut.AddMessageAsync(new ChatMessage(ChatRole.User, "Hello"), "s1", "c1");

        result.Should().NotBeNull();
        result.Content.Should().Be("Hello");
        result.Role.Should().Be("user");
    }

    // ── GetMessagesAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task GetMessagesAsync_ReturnsRecentMessages()
    {
        var storedMsg = new Message
        {
            MessageId = "m1", SessionId = "s1", ConversationId = "c1",
            Role = "user", Content = "Hello", TimestampUtc = _now
        };
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RecallResult
            {
                Context = new MemoryContext
                {
                    SessionId = "s1",
                    AssembledAtUtc = _now,
                    RecentMessages = new MemoryContextSection<Message> { Items = [storedMsg] }
                }
            });

        var result = await _sut.GetMessagesAsync("s1");

        result.Should().HaveCount(1);
        result[0].Text.Should().Be("Hello");
    }

    [Fact]
    public async Task GetMessagesAsync_ServiceThrows_ReturnsEmpty()
    {
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("DB error"));

        var result = await _sut.GetMessagesAsync("s1");

        result.Should().BeEmpty();
    }

    // ── ClearSessionAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task ClearSessionAsync_CallsMemoryService()
    {
        await _sut.ClearSessionAsync("s1");

        await _memoryService.Received(1).ClearSessionAsync("s1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClearSessionAsync_ServiceThrows_DoesNotPropagate()
    {
        _memoryService.ClearSessionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("DB error"));

        var act = async () => await _sut.ClearSessionAsync("s1");

        await act.Should().NotThrowAsync();
    }
}
