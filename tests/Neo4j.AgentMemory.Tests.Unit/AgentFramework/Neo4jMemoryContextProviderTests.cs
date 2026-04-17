using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.AgentFramework;
using Neo4j.AgentMemory.Tests.Unit.TestHelpers;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Neo4j.AgentMemory.Tests.Unit.AgentFramework;

public sealed class Neo4jMemoryContextProviderTests
{
    private readonly IMemoryService _memoryService = Substitute.For<IMemoryService>();
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
    private readonly Neo4jMemoryContextProvider _sut;

    public Neo4jMemoryContextProviderTests()
    {
        _sut = new Neo4jMemoryContextProvider(
            _memoryService,
            _embeddingGenerator,
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
    public async Task BuildContextAsync_WithUserMessage_CallsRecallAsync()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "What is Neo4j?") };
        _embeddingGenerator.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(call => MockFactory.EmbeddingResult(call, new float[] { 0.1f, 0.2f }));
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(EmptyRecall("s1"));

        await _sut.BuildContextAsync(messages, "s1", "c1", CancellationToken.None);

        await _memoryService.Received(1).RecallAsync(
            Arg.Is<RecallRequest>(r => r.SessionId == "s1" && r.Query.Contains("Neo4j")),
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
        _embeddingGenerator.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(call => MockFactory.EmbeddingResult(call, new float[] { 0.1f }));
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
        _embeddingGenerator.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
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
        _embeddingGenerator.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(call => MockFactory.EmbeddingResult(call, new float[] { 0.1f }));
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("DB down"));

        var messages = new List<ChatMessage> { new(ChatRole.User, "Hello") };
        var result = await _sut.BuildContextAsync(messages, "s1", "c1", CancellationToken.None);

        result.Messages.Should().BeNullOrEmpty();
    }

    // ── PerformStoreAsync (internal, tested via InternalsVisibleTo) ────────

    private static readonly DateTimeOffset FixedTime = new(2025, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private Neo4jMemoryContextProvider CreateSut(AgentFrameworkOptions? agentOptions = null) =>
        new(
            _memoryService,
            _embeddingGenerator,
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
