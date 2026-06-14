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

public sealed class Neo4jMemoryContextProviderTests
{
    private readonly IMemoryService _memoryService = Substitute.For<IMemoryService>();
    private readonly IEmbeddingOrchestrator _embeddingOrchestrator = Substitute.For<IEmbeddingOrchestrator>();
    private readonly Neo4jMemoryContextProvider _sut;

    public Neo4jMemoryContextProviderTests()
    {
        _sut = new Neo4jMemoryContextProvider(
            _memoryService,
            _embeddingOrchestrator,
            Options.Create(new ContextFormatOptions()),
            Options.Create(new AgentFrameworkOptions()),
            NullLogger<Neo4jMemoryContextProvider>.Instance);
    }

    private static RecallResult EmptyRecall(string sessionId) => new()
    {
        Context = new MemoryContext { SessionId = sessionId, AssembledAtUtc = DateTimeOffset.UtcNow }
    };

    // ── BuildContextAsync (internal, tested via InternalsVisibleTo) ────────

    [Fact]
    public async Task BuildContextAsync_NoUserMessages_ReturnsEmptyContext()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are helpful.")
        };

        var result = await _sut.BuildContextAsync(messages, "s1", "c1", CancellationToken.None);

        result.Messages.Should().BeNullOrEmpty();
        await _memoryService.DidNotReceive().RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BuildContextAsync_UsesEmbeddingOrchestrator_EmbedQueryAsync()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "Tell me about Neo4j.") };
        _embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.5f });
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(EmptyRecall("s1"));

        await _sut.BuildContextAsync(messages, "s1", "c1", CancellationToken.None);

        await _embeddingOrchestrator.Received(1)
            .EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _memoryService.Received(1).RecallAsync(
            Arg.Is<RecallRequest>(r => r.QueryEmbedding != null && r.QueryEmbedding.Length == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BuildContextAsync_WithUserMessage_CallsRecallAsync()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "What is Neo4j?") };
        _embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.1f, 0.2f });
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(EmptyRecall("s1"));

        await _sut.BuildContextAsync(messages, "s1", "c1", CancellationToken.None);

        await _memoryService.Received(1).RecallAsync(
            Arg.Is<RecallRequest>(r => r.SessionId == "s1" && r.Query.Contains("Neo4j")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BuildContextAsync_WithUserId_SetsRecallRequestUserId()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "What do I prefer?") };
        _embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.1f });
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(EmptyRecall("s1"));

        await _sut.BuildContextAsync(messages, "s1", "c1", CancellationToken.None, userId: "alice");

        await _memoryService.Received(1).RecallAsync(
            Arg.Is<RecallRequest>(r => r.UserId == "alice"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BuildContextAsync_WithoutUserId_RecallRequestUserIdIsNull()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "What do I prefer?") };
        _embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.1f });
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(EmptyRecall("s1"));

        await _sut.BuildContextAsync(messages, "s1", "c1", CancellationToken.None);

        await _memoryService.Received(1).RecallAsync(
            Arg.Is<RecallRequest>(r => r.UserId == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BuildContextAsync_WithRecallResults_ReturnsContextMessages()
    {
        var storedMsg = new Message
        {
            MessageId = "m1", SessionId = "s1", ConversationId = "c1",
            Role = "assistant", Content = "Neo4j is a graph database.",
            TimestampUtc = DateTimeOffset.UtcNow
        };
        var recallResult = new RecallResult
        {
            Context = new MemoryContext
            {
                SessionId = "s1",
                AssembledAtUtc = DateTimeOffset.UtcNow,
                RecentMessages = new MemoryContextSection<Message> { Items = [storedMsg] }
            }
        };
        _embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.1f });
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(recallResult);

        var messages = new List<ChatMessage> { new(ChatRole.User, "Tell me about graph databases.") };
        var result = await _sut.BuildContextAsync(messages, "s1", "c1", CancellationToken.None);

        result.Messages.Should().NotBeNullOrEmpty();
        result.Messages!.Any(m => m.Text != null && m.Text.Contains("graph database")).Should().BeTrue();
    }

    [Fact]
    public async Task BuildContextAsync_EmbeddingFails_StillCallsRecall()
    {
        _embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Embedding service unavailable"));
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(EmptyRecall("s1"));

        var messages = new List<ChatMessage> { new(ChatRole.User, "Hello") };
        var result = await _sut.BuildContextAsync(messages, "s1", "c1", CancellationToken.None);

        await _memoryService.Received(1).RecallAsync(
            Arg.Is<RecallRequest>(r => r.QueryEmbedding == null),
            Arg.Any<CancellationToken>());
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task BuildContextAsync_RecallFails_ReturnsEmptyContext()
    {
        _embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.1f });
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("DB down"));

        var messages = new List<ChatMessage> { new(ChatRole.User, "Hello") };
        var result = await _sut.BuildContextAsync(messages, "s1", "c1", CancellationToken.None);

        result.Messages.Should().BeNullOrEmpty();
    }

    // ── Ambient owner context (IC8) — cross-owner isolation of LLM-invokable tools ────────
    //
    // The agent's mid-turn facade tools (search_memory / remember_*) scope only via the ambient
    // IMemoryOwnerContext. The provider must push the turn's userId into it, or those tools run unscoped
    // (cross-owner read leak) and writes are stored shared/global. We assert the WIRING via a substitute:
    // DefaultMemoryOwnerContext is AsyncLocal-backed, and a value set inside an awaited async method is not
    // observable by the caller afterwards — so the host must own the enclosing scope for the value to reach
    // the tools (documented in docs/reviews/review-2026-06-13-cycle3.md). Here we verify the provider sets it.

    [Fact]
    public async Task BuildContextAsync_WithUserId_PushesOwnerIntoAmbientContext()
    {
        var owner = Substitute.For<IWritableMemoryOwnerContext>();
        var sut = new Neo4jMemoryContextProvider(
            _memoryService, _embeddingOrchestrator,
            Options.Create(new ContextFormatOptions()), Options.Create(new AgentFrameworkOptions()),
            NullLogger<Neo4jMemoryContextProvider>.Instance, ownerContext: owner);
        _embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.1f });
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(EmptyRecall("s1"));

        var messages = new List<ChatMessage> { new(ChatRole.User, "What do I prefer?") };
        await sut.BuildContextAsync(messages, "s1", "c1", CancellationToken.None, userId: "alice");

        owner.Received().UserId = "alice";
    }

    [Fact]
    public async Task PerformStoreAsync_WithUserId_PushesOwnerIntoAmbientContext()
    {
        var owner = Substitute.For<IWritableMemoryOwnerContext>();
        var sut = new Neo4jMemoryContextProvider(
            _memoryService, _embeddingOrchestrator,
            Options.Create(new ContextFormatOptions()), Options.Create(new AgentFrameworkOptions()),
            NullLogger<Neo4jMemoryContextProvider>.Instance, ownerContext: owner);

        await sut.PerformStoreAsync(
            new List<ChatMessage> { new(ChatRole.Assistant, "noted") }, "s1", "c1",
            CancellationToken.None, userId: "bob");

        owner.Received().UserId = "bob";
    }

    [Fact]
    public async Task BuildContextAsync_WithoutUserId_PushesNullOwner_NoStaleBleed()
    {
        // An unowned turn must reset the ambient owner to null (shared), never inherit a previous turn's.
        var owner = Substitute.For<IWritableMemoryOwnerContext>();
        var sut = new Neo4jMemoryContextProvider(
            _memoryService, _embeddingOrchestrator,
            Options.Create(new ContextFormatOptions()), Options.Create(new AgentFrameworkOptions()),
            NullLogger<Neo4jMemoryContextProvider>.Instance, ownerContext: owner);
        _embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.1f });
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(EmptyRecall("s1"));

        await sut.BuildContextAsync(
            new List<ChatMessage> { new(ChatRole.User, "hi") }, "s1", "c1", CancellationToken.None);

        owner.Received().UserId = null;
    }

    // ── PerformStoreAsync (internal, tested via InternalsVisibleTo) ────────

    private static readonly DateTimeOffset FixedTime = new(2025, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private Neo4jMemoryContextProvider CreateSut(AgentFrameworkOptions? agentOptions = null) =>
        new(
            _memoryService,
            _embeddingOrchestrator,
            Options.Create(new ContextFormatOptions()),
            Options.Create(agentOptions ?? new AgentFrameworkOptions()),
            NullLogger<Neo4jMemoryContextProvider>.Instance);

    [Fact]
    public async Task PerformStoreAsync_PersistsResponseMessages()
    {
        var sut = CreateSut();
        var responseMessages = new List<ChatMessage>
        {
            new(ChatRole.Assistant, "I remember you like dark mode.")
        };

        var storedMessage = new Message
        {
            MessageId = "m-store-1", SessionId = "s1", ConversationId = "c1",
            Role = "assistant", Content = "I remember you like dark mode.",
            TimestampUtc = FixedTime
        };
        _memoryService
            .AddMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(storedMessage);

        await sut.PerformStoreAsync(responseMessages, "s1", "c1", CancellationToken.None);

        await _memoryService.Received(1).AddMessageAsync(
            "s1", "c1", Arg.Any<string>(), "I remember you like dark mode.",
            Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PerformStoreAsync_SkipsEmptyTextMessages()
    {
        var sut = CreateSut();
        var responseMessages = new List<ChatMessage>
        {
            new(ChatRole.Assistant, (string?)null),
            new(ChatRole.Assistant, "   ")
        };

        await sut.PerformStoreAsync(responseMessages, "s1", "c1", CancellationToken.None);

        await _memoryService.DidNotReceive().AddMessageAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PerformStoreAsync_AutoExtractEnabled_CallsExtractAndPersistAsync()
    {
        var sut = CreateSut(new AgentFrameworkOptions { AutoExtractOnPersist = true });
        var messages = new List<ChatMessage> { new(ChatRole.Assistant, "Paris is the capital of France.") };
        var storedMessage = new Message
        {
            MessageId = "m-ae-1", SessionId = "s1", ConversationId = "c1",
            Role = "assistant", Content = "Paris is the capital of France.",
            TimestampUtc = FixedTime
        };
        _memoryService
            .AddMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(storedMessage);
        _memoryService
            .ExtractAndPersistAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ExtractionResult
            {
                Entities = Array.Empty<ExtractedEntity>(),
                Facts = Array.Empty<ExtractedFact>(),
                Preferences = Array.Empty<ExtractedPreference>(),
                Relationships = Array.Empty<ExtractedRelationship>(),
                SourceMessageIds = new[] { "m-ae-1" }
            });

        await sut.PerformStoreAsync(messages, "s1", "c1", CancellationToken.None);

        await _memoryService.Received(1).ExtractAndPersistAsync(
            Arg.Is<ExtractionRequest>(r => r.SessionId == "s1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PerformStoreAsync_AutoExtractEnabled_WithUserId_StampsExtractionOwner()
    {
        var sut = CreateSut(new AgentFrameworkOptions { AutoExtractOnPersist = true });
        var messages = new List<ChatMessage> { new(ChatRole.Assistant, "Paris is the capital of France.") };
        var storedMessage = new Message
        {
            MessageId = "m-owner-1", SessionId = "s1", ConversationId = "c1",
            Role = "assistant", Content = "Paris is the capital of France.",
            TimestampUtc = FixedTime
        };
        _memoryService
            .AddMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(storedMessage);
        _memoryService
            .ExtractAndPersistAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ExtractionResult
            {
                Entities = Array.Empty<ExtractedEntity>(),
                Facts = Array.Empty<ExtractedFact>(),
                Preferences = Array.Empty<ExtractedPreference>(),
                Relationships = Array.Empty<ExtractedRelationship>(),
                SourceMessageIds = new[] { "m-owner-1" }
            });

        await sut.PerformStoreAsync(messages, "s1", "c1", CancellationToken.None, userId: "bob");

        await _memoryService.Received(1).ExtractAndPersistAsync(
            Arg.Is<ExtractionRequest>(r => r.SessionId == "s1" && r.UserId == "bob"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PerformStoreAsync_AutoExtractDisabled_DoesNotCallExtractAndPersistAsync()
    {
        var sut = CreateSut(new AgentFrameworkOptions { AutoExtractOnPersist = false });
        var messages = new List<ChatMessage> { new(ChatRole.Assistant, "Some content.") };
        var storedMessage = new Message
        {
            MessageId = "m-no-ae", SessionId = "s1", ConversationId = "c1",
            Role = "assistant", Content = "Some content.",
            TimestampUtc = FixedTime
        };
        _memoryService
            .AddMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(storedMessage);

        await sut.PerformStoreAsync(messages, "s1", "c1", CancellationToken.None);

        await _memoryService.DidNotReceive().ExtractAndPersistAsync(
            Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PerformStoreAsync_ExceptionInAddMessage_IsCaughtGracefully()
    {
        var sut = CreateSut();
        var messages = new List<ChatMessage> { new(ChatRole.Assistant, "Boom!") };
        _memoryService
            .AddMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB down"));

        var act = () => sut.PerformStoreAsync(messages, "s1", "c1", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task PerformStoreAsync_AutoExtractEnabled_ExceptionInExtraction_IsCaughtGracefully()
    {
        var sut = CreateSut(new AgentFrameworkOptions { AutoExtractOnPersist = true });
        var messages = new List<ChatMessage> { new(ChatRole.Assistant, "Important data.") };
        var storedMessage = new Message
        {
            MessageId = "m-ext-err", SessionId = "s1", ConversationId = "c1",
            Role = "assistant", Content = "Important data.",
            TimestampUtc = FixedTime
        };
        _memoryService
            .AddMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(storedMessage);
        _memoryService
            .ExtractAndPersistAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("Extraction engine failed"));

        var act = () => sut.PerformStoreAsync(messages, "s1", "c1", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
