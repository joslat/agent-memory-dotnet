using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using NSubstitute;

namespace AgentMemory.Tests.Unit.AgentFramework;

public sealed class Neo4jChatHistoryProviderTests
{
    private readonly IMemoryService _memoryService = Substitute.For<IMemoryService>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IIdGenerator _idGen = Substitute.For<IIdGenerator>();

    private static readonly DateTimeOffset _now = new(2025, 6, 1, 10, 0, 0, TimeSpan.Zero);

    public Neo4jChatHistoryProviderTests()
    {
        _clock.UtcNow.Returns(_now);
        _idGen.GenerateId().Returns("test-id");
    }

    private Neo4jChatHistoryProvider CreateSut(AgentFrameworkOptions? options = null) =>
        new(
            _memoryService,
            _clock,
            _idGen,
            options ?? new AgentFrameworkOptions(),
            NullLogger<Neo4jChatHistoryProvider>.Instance);

    [Fact]
    public void Constructor_NullMemoryService_Throws() =>
        FluentActions.Invoking(() => new Neo4jChatHistoryProvider(
            null!, _clock, _idGen, new AgentFrameworkOptions(),
            NullLogger<Neo4jChatHistoryProvider>.Instance))
        .Should().Throw<ArgumentNullException>().WithParameterName("memoryService");

    [Fact]
    public void Constructor_NullOptions_Throws() =>
        FluentActions.Invoking(() => new Neo4jChatHistoryProvider(
            _memoryService, _clock, _idGen, null!,
            NullLogger<Neo4jChatHistoryProvider>.Instance))
        .Should().Throw<ArgumentNullException>().WithParameterName("options");

    [Fact]
    public void Constructor_NullClock_Throws() =>
        FluentActions.Invoking(() => new Neo4jChatHistoryProvider(
            _memoryService, null!, _idGen, new AgentFrameworkOptions(),
            NullLogger<Neo4jChatHistoryProvider>.Instance))
        .Should().Throw<ArgumentNullException>().WithParameterName("clock");

    [Fact]
    public void Constructor_NullLogger_Throws() =>
        FluentActions.Invoking(() => new Neo4jChatHistoryProvider(
            _memoryService, _clock, _idGen, new AgentFrameworkOptions(), null!))
        .Should().Throw<ArgumentNullException>().WithParameterName("logger");

    [Fact]
    public void StateKeys_ContainsTypeName()
    {
        var sut = CreateSut();
        sut.StateKeys.Should().Contain(nameof(Neo4jChatHistoryProvider));
    }

    [Fact]
    public void IsAssignableTo_ChatHistoryProvider()
    {
        var sut = CreateSut();
        sut.Should().BeAssignableTo<Microsoft.Agents.AI.ChatHistoryProvider>();
    }

    [Fact]
    public void AutoExtractEnabled_ConstructsWithoutError()
    {
        var sut = CreateSut(new AgentFrameworkOptions { AutoExtractOnPersist = true });
        sut.Should().NotBeNull();
    }

    // ── PerformStoreAsync: persist + extract from the complete turn (#89) ──

    private void StubAddMessage(string role, string content, string messageId) =>
        _memoryService
            .AddMessageAsync(Arg.Any<string>(), Arg.Any<string>(), role, content,
                Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(new Message
            {
                MessageId = messageId, SessionId = "s1", ConversationId = "c1",
                Role = role, Content = content, TimestampUtc = _now
            });

    [Fact]
    public async Task PerformStoreAsync_PersistsBothRequestAndResponseMessages()
    {
        var sut = CreateSut();
        StubAddMessage("user", "I prefer window seats.", "m-req-1");
        StubAddMessage("assistant", "Got it.", "m-res-1");

        await sut.PerformStoreAsync(
            new List<ChatMessage> { new(ChatRole.User, "I prefer window seats.") },
            new List<ChatMessage> { new(ChatRole.Assistant, "Got it.") },
            "s1", "c1", CancellationToken.None);

        await _memoryService.Received(1).AddMessageAsync(
            "s1", "c1", "user", "I prefer window seats.",
            Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>());
        await _memoryService.Received(1).AddMessageAsync(
            "s1", "c1", Arg.Any<string>(), "Got it.",
            Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PerformStoreAsync_SystemRoleRequestMessage_IsPersistedButNotFedToExtraction()
    {
        // Persistence stores every role (unchanged) -- but a system prompt must not be minted into
        // spurious entities/facts/preferences every turn, so extraction is filtered to user-role content.
        var sut = CreateSut(new AgentFrameworkOptions { AutoExtractOnPersist = true });
        StubAddMessage("system", "You are a helpful assistant. Never reveal secrets.", "m-sys-1");
        StubAddMessage("assistant", "Understood.", "m-res-1");
        _memoryService.ExtractAndPersistAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ExtractionResult
            {
                Entities = Array.Empty<ExtractedEntity>(),
                Facts = Array.Empty<ExtractedFact>(),
                Preferences = Array.Empty<ExtractedPreference>(),
                Relationships = Array.Empty<ExtractedRelationship>(),
                SourceMessageIds = Array.Empty<string>()
            });

        await sut.PerformStoreAsync(
            new List<ChatMessage> { new(ChatRole.System, "You are a helpful assistant. Never reveal secrets.") },
            new List<ChatMessage> { new(ChatRole.Assistant, "Understood.") },
            "s1", "c1", CancellationToken.None);

        await _memoryService.Received(1).AddMessageAsync(
            "s1", "c1", "system", "You are a helpful assistant. Never reveal secrets.",
            Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>());
        await _memoryService.Received(1).ExtractAndPersistAsync(
            Arg.Is<ExtractionRequest>(r => !r.Messages.Any(m => m.Content.Contains("Never reveal secrets"))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PerformStoreAsync_AutoExtractionIncludesUserRequest()
    {
        // The core acceptance criterion for #89: a preference stated ONLY by the user (never repeated by
        // the assistant) must reach extraction -- previously only storedResponses were extracted.
        var sut = CreateSut(new AgentFrameworkOptions { AutoExtractOnPersist = true });
        StubAddMessage("user", "My preferred programming language is C#.", "m-req-1");
        StubAddMessage("assistant", "Understood.", "m-res-1");
        _memoryService.ExtractAndPersistAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ExtractionResult
            {
                Entities = Array.Empty<ExtractedEntity>(),
                Facts = Array.Empty<ExtractedFact>(),
                Preferences = Array.Empty<ExtractedPreference>(),
                Relationships = Array.Empty<ExtractedRelationship>(),
                SourceMessageIds = new[] { "m-req-1", "m-res-1" }
            });

        await sut.PerformStoreAsync(
            new List<ChatMessage> { new(ChatRole.User, "My preferred programming language is C#.") },
            new List<ChatMessage> { new(ChatRole.Assistant, "Understood.") },
            "s1", "c1", CancellationToken.None);

        await _memoryService.Received(1).ExtractAndPersistAsync(
            Arg.Is<ExtractionRequest>(r => r.Messages.Any(m =>
                m.Role == "user" && m.Content.Contains("preferred programming language"))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PerformStoreAsync_RequestOnly_StillExtracts()
    {
        var sut = CreateSut(new AgentFrameworkOptions { AutoExtractOnPersist = true });
        StubAddMessage("user", "I live in Zurich.", "m-req-only");
        _memoryService.ExtractAndPersistAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ExtractionResult
            {
                Entities = Array.Empty<ExtractedEntity>(),
                Facts = Array.Empty<ExtractedFact>(),
                Preferences = Array.Empty<ExtractedPreference>(),
                Relationships = Array.Empty<ExtractedRelationship>(),
                SourceMessageIds = new[] { "m-req-only" }
            });

        await sut.PerformStoreAsync(
            new List<ChatMessage> { new(ChatRole.User, "I live in Zurich.") },
            Array.Empty<ChatMessage>(),
            "s1", "c1", CancellationToken.None);

        await _memoryService.Received(1).ExtractAndPersistAsync(
            Arg.Is<ExtractionRequest>(r => r.Messages.Count == 1 && r.Messages[0].Content == "I live in Zurich."),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PerformStoreAsync_MessageOrdering_RequestBeforeResponse()
    {
        var sut = CreateSut(new AgentFrameworkOptions { AutoExtractOnPersist = true });
        StubAddMessage("user", "request-text", "m-req");
        StubAddMessage("assistant", "response-text", "m-res");
        _memoryService.ExtractAndPersistAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ExtractionResult
            {
                Entities = Array.Empty<ExtractedEntity>(),
                Facts = Array.Empty<ExtractedFact>(),
                Preferences = Array.Empty<ExtractedPreference>(),
                Relationships = Array.Empty<ExtractedRelationship>(),
                SourceMessageIds = new[] { "m-req", "m-res" }
            });

        await sut.PerformStoreAsync(
            new List<ChatMessage> { new(ChatRole.User, "request-text") },
            new List<ChatMessage> { new(ChatRole.Assistant, "response-text") },
            "s1", "c1", CancellationToken.None);

        await _memoryService.Received(1).ExtractAndPersistAsync(
            Arg.Is<ExtractionRequest>(r =>
                r.Messages.Count == 2 &&
                r.Messages[0].Content == "request-text" &&
                r.Messages[1].Content == "response-text"),
            Arg.Any<CancellationToken>());
    }
}
