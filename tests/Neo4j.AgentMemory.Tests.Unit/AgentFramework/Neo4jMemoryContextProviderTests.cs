using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.AgentFramework;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Neo4j.AgentMemory.Tests.Unit.AgentFramework;

public sealed class Neo4jMemoryContextProviderTests
{
    private readonly IMemoryService _memoryService = Substitute.For<IMemoryService>();
    private readonly IEmbeddingProvider _embeddingProvider = Substitute.For<IEmbeddingProvider>();
    private readonly Neo4jMemoryContextProvider _sut;

    public Neo4jMemoryContextProviderTests()
    {
        _sut = new Neo4jMemoryContextProvider(
            _memoryService,
            _embeddingProvider,
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
        _embeddingProvider.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([0.1f, 0.2f]);
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
        _embeddingProvider.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([0.1f]);
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
        _embeddingProvider.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
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
        _embeddingProvider.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([0.1f]);
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("DB down"));

        var messages = new List<ChatMessage> { new(ChatRole.User, "Hello") };
        var result = await _sut.BuildContextAsync(messages, "s1", "c1", CancellationToken.None);

        result.Messages.Should().BeNullOrEmpty();
    }
}
