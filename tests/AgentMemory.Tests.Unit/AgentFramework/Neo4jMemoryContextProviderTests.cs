using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AgentMemory.Tests.Unit.AgentFramework;

public sealed class Neo4jMemoryContextProviderTests
{
    private readonly IMemoryService _memoryService = Substitute.For<IMemoryService>();
    private readonly IEmbeddingOrchestrator _embeddingOrchestrator = Substitute.For<IEmbeddingOrchestrator>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IIdGenerator _idGenerator = Substitute.For<IIdGenerator>();
    private readonly Neo4jMemoryContextProvider _sut;

    public Neo4jMemoryContextProviderTests()
    {
        _clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        _idGenerator.GenerateId().Returns(_ => Guid.NewGuid().ToString("N"));
        _sut = new Neo4jMemoryContextProvider(
            _memoryService,
            _embeddingOrchestrator,
            _clock,
            _idGenerator,
            Options.Create(new MemoryOptions()),
            Options.Create(new ContextFormatOptions()),
            Options.Create(new AgentFrameworkOptions()),
            NullLogger<Neo4jMemoryContextProvider>.Instance);
    }

    private static RecallResult EmptyRecall(string sessionId) => new()
    {
        Context = new MemoryContext { SessionId = sessionId, AssembledAtUtc = DateTimeOffset.UtcNow }
    };

    private Neo4jMemoryContextProvider CreateSutWithRecallOptions(RecallOptions recall) =>
        new(
            _memoryService,
            _embeddingOrchestrator,
            _clock,
            _idGenerator,
            Options.Create(new MemoryOptions { Recall = recall }),
            Options.Create(new ContextFormatOptions()),
            Options.Create(new AgentFrameworkOptions()),
            NullLogger<Neo4jMemoryContextProvider>.Instance);

    // ── Configured RecallOptions reach native MAF recall (#87) ─────────────

    [Fact]
    public async Task BuildContextAsync_UsesConfiguredRecallOptions()
    {
        var configuredRecall = new RecallOptions
        {
            MaxFacts = 3,
            MaxEntities = 4,
            MinSimilarityScore = 0.85,
            BlendMode = RetrievalBlendMode.MemoryOnly
        };
        var sut = CreateSutWithRecallOptions(configuredRecall);
        _embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 0.1f });
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>()).Returns(EmptyRecall("s1"));

        await sut.BuildContextAsync(
            new List<ChatMessage> { new(ChatRole.User, "What do you know about this customer?") },
            "s1", "c1", CancellationToken.None);

        await _memoryService.Received(1).RecallAsync(
            Arg.Is<RecallRequest>(request =>
                request.Options!.MaxFacts == 3 &&
                request.Options.MaxEntities == 4 &&
                request.Options.MinSimilarityScore == 0.85 &&
                request.Options.BlendMode == RetrievalBlendMode.MemoryOnly),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BuildContextAsync_ConfiguredScope_NeverOverridesTheInvocationOwner()
    {
        // A static, globally-configured Scope must not silently override the real, per-invocation owner
        // (#100's isolation policy resolves scope from RecallRequest.UserId) -- this is the exact risk
        // flagged when sequencing #87 after #100.
        var configuredRecall = new RecallOptions { Scope = MemoryScope.For("some-other-owner") };
        var sut = CreateSutWithRecallOptions(configuredRecall);
        _embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 0.1f });
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>()).Returns(EmptyRecall("s1"));

        await sut.BuildContextAsync(
            new List<ChatMessage> { new(ChatRole.User, "hi") },
            "s1", "c1", CancellationToken.None, userId: "alice");

        await _memoryService.Received(1).RecallAsync(
            Arg.Is<RecallRequest>(request => request.Options!.Scope == null && request.UserId == "alice"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BuildContextAsync_NoConfiguredRecallOptions_UsesDefaults()
    {
        // Existing default behavior must remain unchanged when the host does not customize recall options.
        _embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 0.1f });
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>()).Returns(EmptyRecall("s1"));

        await _sut.BuildContextAsync(
            new List<ChatMessage> { new(ChatRole.User, "hi") },
            "s1", "c1", CancellationToken.None);

        await _memoryService.Received(1).RecallAsync(
            Arg.Is<RecallRequest>(request =>
                request.Options!.MaxFacts == RecallOptions.Default.MaxFacts &&
                request.Options.MaxEntities == RecallOptions.Default.MaxEntities &&
                request.Options.BlendMode == RecallOptions.Default.BlendMode),
            Arg.Any<CancellationToken>());
    }

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
            _memoryService, _embeddingOrchestrator, _clock, _idGenerator, Options.Create(new MemoryOptions()),
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
            _memoryService, _embeddingOrchestrator, _clock, _idGenerator, Options.Create(new MemoryOptions()),
            Options.Create(new ContextFormatOptions()), Options.Create(new AgentFrameworkOptions()),
            NullLogger<Neo4jMemoryContextProvider>.Instance, ownerContext: owner);

        await sut.PerformStoreAsync(
            Array.Empty<ChatMessage>(),
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
            _memoryService, _embeddingOrchestrator, _clock, _idGenerator, Options.Create(new MemoryOptions()),
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
            _clock,
            _idGenerator,
            Options.Create(new MemoryOptions()),
            Options.Create(new ContextFormatOptions()),
            Options.Create(agentOptions ?? new AgentFrameworkOptions()),
            NullLogger<Neo4jMemoryContextProvider>.Instance);

    // ── Persist + extract from the complete turn (#89) ─────────────────────

    [Fact]
    public async Task PerformStoreAsync_RequestMessages_AreNotPersistedAsNewNodes()
    {
        // The duplication-avoidance guarantee: this provider must never call AddMessageAsync for a
        // request message, since a host may also have Neo4jChatHistoryProvider/Neo4jChatMessageStore/their
        // own component persisting the same messages, and there is no idempotency mechanism to make a
        // second persist call for "the same" logical message safe.
        var sut = CreateSut(new AgentFrameworkOptions { AutoExtractOnPersist = true });
        var storedResponse = new Message
        {
            MessageId = "m-resp", SessionId = "s1", ConversationId = "c1",
            Role = "assistant", Content = "Got it.", TimestampUtc = FixedTime
        };
        _memoryService
            .AddMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(storedResponse);
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
            new List<ChatMessage> { new(ChatRole.User, "I prefer window seats.") },
            new List<ChatMessage> { new(ChatRole.Assistant, "Got it.") },
            "s1", "c1", CancellationToken.None);

        // Exactly one AddMessageAsync call -- for the response, never for the request.
        await _memoryService.Received(1).AddMessageAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>());
        await _memoryService.DidNotReceive().AddMessageAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), "I prefer window seats.",
            Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PerformStoreAsync_SystemRoleRequestMessage_IsNotFedToExtraction()
    {
        // A system prompt accumulated in RequestMessages must not be minted into spurious
        // entities/facts/preferences every turn -- extraction is filtered to ChatRole.User, matching the
        // same filter recall already applies.
        var sut = CreateSut(new AgentFrameworkOptions { AutoExtractOnPersist = true });
        var storedResponse = new Message
        {
            MessageId = "m-resp", SessionId = "s1", ConversationId = "c1",
            Role = "assistant", Content = "Understood.", TimestampUtc = FixedTime
        };
        _memoryService
            .AddMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(storedResponse);
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

        await _memoryService.Received(1).ExtractAndPersistAsync(
            Arg.Is<ExtractionRequest>(r => !r.Messages.Any(m => m.Content.Contains("Never reveal secrets"))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PerformStoreAsync_ExtractionSeesRequestAndResponseMessages()
    {
        var sut = CreateSut(new AgentFrameworkOptions { AutoExtractOnPersist = true });
        var storedResponse = new Message
        {
            MessageId = "m-resp", SessionId = "s1", ConversationId = "c1",
            Role = "assistant", Content = "Understood.", TimestampUtc = FixedTime
        };
        _memoryService
            .AddMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(storedResponse);
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
            new List<ChatMessage> { new(ChatRole.User, "I live in Zurich.") },
            Array.Empty<ChatMessage>(),
            "s1", "c1", CancellationToken.None);

        await _memoryService.Received(1).ExtractAndPersistAsync(
            Arg.Is<ExtractionRequest>(r => r.Messages.Count == 1 && r.Messages[0].Content == "I live in Zurich."),
            Arg.Any<CancellationToken>());
        await _memoryService.DidNotReceive().AddMessageAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PerformStoreAsync_MessageOrdering_RequestBeforeResponse()
    {
        var sut = CreateSut(new AgentFrameworkOptions { AutoExtractOnPersist = true });
        var storedResponse = new Message
        {
            MessageId = "m-resp", SessionId = "s1", ConversationId = "c1",
            Role = "assistant", Content = "response-text", TimestampUtc = FixedTime
        };
        _memoryService
            .AddMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(storedResponse);
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

        await sut.PerformStoreAsync(Array.Empty<ChatMessage>(), responseMessages, "s1", "c1", CancellationToken.None);

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

        await sut.PerformStoreAsync(Array.Empty<ChatMessage>(), responseMessages, "s1", "c1", CancellationToken.None);

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

        await sut.PerformStoreAsync(Array.Empty<ChatMessage>(), messages, "s1", "c1", CancellationToken.None);

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

        await sut.PerformStoreAsync(Array.Empty<ChatMessage>(), messages, "s1", "c1", CancellationToken.None, userId: "bob");

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

        await sut.PerformStoreAsync(Array.Empty<ChatMessage>(), messages, "s1", "c1", CancellationToken.None);

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

        var act = () => sut.PerformStoreAsync(Array.Empty<ChatMessage>(), messages, "s1", "c1", CancellationToken.None);

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

        var act = () => sut.PerformStoreAsync(Array.Empty<ChatMessage>(), messages, "s1", "c1", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // ── R6-A: caller cancellation must propagate, not be swallowed as empty-context / silent success ──
    // Before the OCE guards, a cancelled turn surfaced as an empty AIContext (BuildContext) or a silent
    // "stored" (PerformStore) — the agent ran on no memory / believed a cancelled write succeeded.

    [Fact]
    public async Task BuildContextAsync_RecallCancelled_PropagatesOperationCanceled()
    {
        var sut = CreateSut();
        var messages = new List<ChatMessage> { new(ChatRole.User, "Tell me about Neo4j.") };
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.5f });
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        var act = async () => await sut.BuildContextAsync(messages, "s1", "c1", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task BuildContextAsync_EmbeddingCancelled_PropagatesOperationCanceled()
    {
        var sut = CreateSut();
        var messages = new List<ChatMessage> { new(ChatRole.User, "Tell me about Neo4j.") };
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        // The embedding sub-try has its own broad catch; the guard must let cancellation escape rather than
        // log "proceeding without semantic search" and continue.
        _embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        var act = async () => await sut.BuildContextAsync(messages, "s1", "c1", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task PerformStoreAsync_CallerCancels_PropagatesOperationCanceled()
    {
        var sut = CreateSut();
        var messages = new List<ChatMessage> { new(ChatRole.Assistant, "Important data.") };
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _memoryService.AddMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        var act = async () => await sut.PerformStoreAsync(Array.Empty<ChatMessage>(), messages, "s1", "c1", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
