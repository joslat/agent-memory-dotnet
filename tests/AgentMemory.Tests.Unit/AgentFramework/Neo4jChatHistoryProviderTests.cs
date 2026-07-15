using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.AI;
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

    // Minimal AIAgent/AgentSession doubles just to satisfy ChatHistoryProvider.InvokedContext's non-null
    // constructor requirements -- these tests never actually invoke Run/session (de)serialization.
    private sealed class NoopAgent : AIAgent
    {
        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class TestAgentSession : AgentSession;

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

    // ── Fix A (#89): don't re-persist another provider's injected request messages ──

    [Fact]
    public async Task InvokedAsync_ExcludesAIContextProviderSourcedRequestMessages()
    {
        // The base ChatHistoryProvider's default filter only excludes ChatHistory-sourced messages;
        // Neo4jChatHistoryProvider narrows this to External-only (see its constructor) so it never
        // re-persists e.g. Neo4jMemoryContextProvider's injected recalled-memory content every turn.
        var sut = CreateSut();
        StubAddMessage("user", "hello", "m-external");
        StubAddMessage("assistant", "hi", "m-res");

        var externalMessage = new ChatMessage(ChatRole.User, "hello");
        var injectedMessage = new ChatMessage(ChatRole.System, "<recalled_memory>stuff</recalled_memory>")
            .WithAgentRequestMessageSource(AgentRequestMessageSourceType.AIContextProvider, "Neo4jMemoryContextProvider");

#pragma warning disable MAAI001
        var context = new ChatHistoryProvider.InvokedContext(
            new NoopAgent(),
            new TestAgentSession(),
            new[] { externalMessage, injectedMessage },
            new[] { new ChatMessage(ChatRole.Assistant, "hi") });
#pragma warning restore MAAI001

        await sut.InvokedAsync(context, CancellationToken.None);

        await _memoryService.Received(1).AddMessageAsync(
            Arg.Any<string>(), Arg.Any<string>(), "user", "hello",
            Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>());
        await _memoryService.DidNotReceive().AddMessageAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Is<string>(c => c.Contains("recalled_memory")),
            Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>());
    }

    // ── Fix B.4 (#89): dedupe response messages via a provider-native MessageId when present ──

    private void StubAddMessageWithId(string role, string content, string messageId) =>
        _memoryService
            .AddMessageWithIdAsync(Arg.Any<string>(), Arg.Any<string>(), role, content, messageId,
                Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(new Message
            {
                MessageId = messageId, SessionId = "s1", ConversationId = "c1",
                Role = role, Content = content, TimestampUtc = _now
            });

    [Fact]
    public async Task PerformStoreAsync_ResponseMessageWithProviderMessageId_UsesAddMessageWithIdAsync()
    {
        var sut = CreateSut();
        StubAddMessage("user", "hi", "m-req");
        StubAddMessageWithId("assistant", "Got it.", "maf:resp-42");

        var responseMessage = new ChatMessage(ChatRole.Assistant, "Got it.") { MessageId = "resp-42" };

        await sut.PerformStoreAsync(
            new List<ChatMessage> { new(ChatRole.User, "hi") },
            new List<ChatMessage> { responseMessage },
            "s1", "c1", CancellationToken.None);

        await _memoryService.Received(1).AddMessageWithIdAsync(
            "s1", "c1", "assistant", "Got it.", "maf:resp-42",
            Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>());
        await _memoryService.DidNotReceive().AddMessageAsync(
            Arg.Any<string>(), Arg.Any<string>(), "assistant", "Got it.",
            Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PerformStoreAsync_ResponseMessageWithoutProviderMessageId_FallsBackToAddMessageAsync()
    {
        var sut = CreateSut();
        StubAddMessage("user", "hi", "m-req");
        StubAddMessage("assistant", "Got it.", "m-res");

        await sut.PerformStoreAsync(
            new List<ChatMessage> { new(ChatRole.User, "hi") },
            new List<ChatMessage> { new(ChatRole.Assistant, "Got it.") },
            "s1", "c1", CancellationToken.None);

        await _memoryService.Received(1).AddMessageAsync(
            "s1", "c1", "assistant", "Got it.",
            Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>());
        await _memoryService.DidNotReceive().AddMessageWithIdAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>());
    }

    // ── Fix C (#89): non-text content (function calls/results, reasoning) is excluded, explicitly ──

    [Fact]
    public async Task PerformStoreAsync_FunctionCallOnlyResponseMessage_IsExcludedFromPersistenceAndExtraction()
    {
        var sut = CreateSut(new AgentFrameworkOptions { AutoExtractOnPersist = true });
        StubAddMessage("user", "hi", "m-req");
        _memoryService.ExtractAndPersistAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ExtractionResult
            {
                Entities = Array.Empty<ExtractedEntity>(),
                Facts = Array.Empty<ExtractedFact>(),
                Preferences = Array.Empty<ExtractedPreference>(),
                Relationships = Array.Empty<ExtractedRelationship>(),
                SourceMessageIds = Array.Empty<string>()
            });

        var functionCallMessage = new ChatMessage(
            ChatRole.Assistant, new List<AIContent> { new FunctionCallContent("call-1", "search_memory") });

        await sut.PerformStoreAsync(
            new List<ChatMessage> { new(ChatRole.User, "hi") },
            new List<ChatMessage> { functionCallMessage },
            "s1", "c1", CancellationToken.None);

        await _memoryService.DidNotReceive().AddMessageAsync(
            Arg.Any<string>(), Arg.Any<string>(), "assistant", Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>());
        await _memoryService.Received(1).ExtractAndPersistAsync(
            Arg.Is<ExtractionRequest>(r => r.Messages.Count == 1 && r.Messages[0].Role == "user"),
            Arg.Any<CancellationToken>());
    }
}
