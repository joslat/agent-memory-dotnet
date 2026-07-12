using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AgentMemory.Tests.Unit.AgentFramework;

public sealed class Neo4jMicrosoftMemoryFacadeTests
{
    private readonly IMemoryService _memoryService = Substitute.For<IMemoryService>();
    private readonly Neo4jChatMessageStore _messageStore;
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IIdGenerator _idGen = Substitute.For<IIdGenerator>();
    private readonly Neo4jMicrosoftMemoryFacade _sut;

    private static readonly DateTimeOffset _now = new(2025, 1, 28, 12, 0, 0, TimeSpan.Zero);

    public Neo4jMicrosoftMemoryFacadeTests()
    {
        _clock.UtcNow.Returns(_now);
        _idGen.GenerateId().Returns("id-001");

        _messageStore = new Neo4jChatMessageStore(
            _memoryService, _clock, _idGen, NullLogger<Neo4jChatMessageStore>.Instance);

        _sut = new Neo4jMicrosoftMemoryFacade(
            _memoryService,
            _messageStore,
            Options.Create(new AgentFrameworkOptions()),
            NullLogger<Neo4jMicrosoftMemoryFacade>.Instance);
    }

    // ── GetContextForRunAsync ──────────────────────────────────────────────

    [Fact]
    public async Task GetContextForRunAsync_ReturnsMessagesFromStore()
    {
        var storedMsg = new Message
        {
            MessageId = "m1", SessionId = "s1", ConversationId = "c1",
            Role = "user", Content = "Hi", TimestampUtc = _now
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

        var result = await _sut.GetContextForRunAsync([], "s1", "c1");

        result.Should().HaveCount(1);
        result[0].Text.Should().Be("Hi");
    }

    [Fact]
    public async Task GetContextForRunAsync_StoreThrows_ReturnsEmpty()
    {
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("DB error"));

        var result = await _sut.GetContextForRunAsync([], "s1", "c1");

        result.Should().BeEmpty();
    }

    // ── PersistAfterRunAsync ───────────────────────────────────────────────

    [Fact]
    public async Task PersistAfterRunAsync_EmptyMessages_DoesNothing()
    {
        await _sut.PersistAfterRunAsync([], "s1", "c1");

        await _memoryService.DidNotReceive()
            .AddMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistAfterRunAsync_StoresMessages()
    {
        var stored = new Message
        {
            MessageId = "id-001", SessionId = "s1", ConversationId = "c1",
            Role = "user", Content = "Hello", TimestampUtc = _now
        };
        _memoryService.AddMessageAsync("s1", "c1", "user", "Hello",
            Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(stored);
        _memoryService.ExtractAndPersistAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ExtractionResult());

        var messages = new List<ChatMessage> { new(ChatRole.User, "Hello") };
        await _sut.PersistAfterRunAsync(messages, "s1", "c1");

        await _memoryService.Received(1).AddMessageAsync("s1", "c1", "user", "Hello",
            Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistAfterRunAsync_WithAutoExtract_CallsExtractAndPersist()
    {
        var stored = new Message
        {
            MessageId = "id-001", SessionId = "s1", ConversationId = "c1",
            Role = "user", Content = "Hello", TimestampUtc = _now
        };
        _memoryService.AddMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(stored);
        _memoryService.ExtractAndPersistAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ExtractionResult());

        var messages = new List<ChatMessage> { new(ChatRole.User, "Hello") };
        await _sut.PersistAfterRunAsync(messages, "s1", "c1", userId: "alice");

        await _memoryService.Received(1).ExtractAndPersistAsync(
            Arg.Is<ExtractionRequest>(r => r.SessionId == "s1" && r.UserId == "alice"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistAfterRunAsync_ExtractionFails_DoesNotPropagate()
    {
        var stored = new Message
        {
            MessageId = "id-001", SessionId = "s1", ConversationId = "c1",
            Role = "user", Content = "Hello", TimestampUtc = _now
        };
        _memoryService.AddMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(stored);
        _memoryService.ExtractAndPersistAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("LLM unavailable"));

        var messages = new List<ChatMessage> { new(ChatRole.User, "Hello") };
        var act = async () => await _sut.PersistAfterRunAsync(messages, "s1", "c1");

        await act.Should().NotThrowAsync();
    }

    // ── StoreMessageAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task StoreMessageAsync_DelegatesToMessageStore()
    {
        var stored = new Message
        {
            MessageId = "id-001", SessionId = "s1", ConversationId = "c1",
            Role = "assistant", Content = "Done!", TimestampUtc = _now
        };
        _memoryService.AddMessageAsync(Arg.Any<string>(), Arg.Any<string>(), "assistant", "Done!",
            Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(stored);

        var result = await _sut.StoreMessageAsync(new ChatMessage(ChatRole.Assistant, "Done!"), "s1", "c1");

        result.Role.Should().Be("assistant");
        result.Content.Should().Be("Done!");
    }

    // ── ClearSessionAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task ClearSessionAsync_CallsMemoryService()
    {
        await _sut.ClearSessionAsync("s1");

        await _memoryService.Received(1).ClearSessionAsync("s1", Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ── R6-A: caller cancellation must propagate, not be swallowed as empty/success ──

    [Fact]
    public async Task GetContextForRunAsync_CallerCancels_PropagatesOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        var messages = new List<ChatMessage> { new(ChatRole.User, "find relevant history") };
        var act = async () => await _sut.GetContextForRunAsync(messages, "s1", "c1", cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetContextForRunAsync_PassesUserId_ToRecallForOwnerScoping()
    {
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RecallResult
            {
                Context = new MemoryContext
                {
                    SessionId = "s1",
                    AssembledAtUtc = _now,
                    RecentMessages = new MemoryContextSection<Message> { Items = [] },
                    RelevantMessages = new MemoryContextSection<Message> { Items = [] }
                }
            });

        var messages = new List<ChatMessage> { new(ChatRole.User, "find relevant history") };
        await _sut.GetContextForRunAsync(messages, "s1", "c1", userId: "alice");

        await _memoryService.Received(1).RecallAsync(
            Arg.Is<RecallRequest>(r => r.UserId == "alice" && r.SessionId == "s1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistAfterRunAsync_CallerCancels_PropagatesOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _memoryService.AddMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        var messages = new List<ChatMessage> { new(ChatRole.User, "Hello") };
        var act = async () => await _sut.PersistAfterRunAsync(messages, "s1", "c1", cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
