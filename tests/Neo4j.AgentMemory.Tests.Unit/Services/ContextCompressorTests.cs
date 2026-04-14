using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Options;
using Neo4j.AgentMemory.Core.Services;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.Services;

public sealed class ContextCompressorTests
{
    private readonly IChatClient _chatClient;
    private readonly ContextCompressor _sut;
    private readonly ContextCompressionOptions _defaultOptions = new()
    {
        TokenThreshold = 100,
        RecentMessageCount = 3,
        MaxObservations = 2,
        EnableReflections = true
    };

    public ContextCompressorTests()
    {
        _chatClient = Substitute.For<IChatClient>();

        // Default: return a short summary for any chat request
        _chatClient
            .GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci => new ChatResponse([new ChatMessage(ChatRole.Assistant, "Summary of conversation.")]));

        _sut = new ContextCompressor(_chatClient, NullLogger<ContextCompressor>.Instance);
    }

    [Fact]
    public async Task CompressAsync_WhenMessagesUnderThreshold_DoesNotCompress()
    {
        var messages = new[] { CreateMessage("m1", "Hi") };

        var result = await _sut.CompressAsync(messages, _defaultOptions);

        result.WasCompressed.Should().BeFalse();
        result.RecentMessages.Should().BeEquivalentTo(messages);
        result.Observations.Should().BeEmpty();
        result.Reflections.Should().BeEmpty();
    }

    [Fact]
    public async Task CompressAsync_WhenMessagesOverThreshold_CompressesContext()
    {
        // Each message content is 100 chars → 25 tokens each, 6 messages = 150 tokens > threshold 100
        var messages = Enumerable.Range(1, 6)
            .Select(i => CreateMessage($"m{i}", new string('x', 100)))
            .ToArray();

        var result = await _sut.CompressAsync(messages, _defaultOptions);

        result.WasCompressed.Should().BeTrue();
    }

    [Fact]
    public async Task CompressAsync_WhenCompressed_RecentMessagesContainsLastN()
    {
        var messages = Enumerable.Range(1, 10)
            .Select(i => CreateMessage($"m{i}", new string('x', 100)))
            .ToArray();

        var result = await _sut.CompressAsync(messages, _defaultOptions);

        result.WasCompressed.Should().BeTrue();
        result.RecentMessages.Should().HaveCount(_defaultOptions.RecentMessageCount);
        // Should be the last N messages by order
        var expectedIds = messages.TakeLast(_defaultOptions.RecentMessageCount).Select(m => m.MessageId);
        result.RecentMessages.Select(m => m.MessageId).Should().BeEquivalentTo(expectedIds);
    }

    [Fact]
    public async Task CompressAsync_WhenCompressed_ReducesTokenCount()
    {
        var messages = Enumerable.Range(1, 10)
            .Select(i => CreateMessage($"m{i}", new string('x', 100)))
            .ToArray();

        var result = await _sut.CompressAsync(messages, _defaultOptions);

        result.WasCompressed.Should().BeTrue();
        result.CompressedTokenCount.Should().BeLessThan(result.OriginalTokenCount);
    }

    [Fact]
    public async Task CompressAsync_WhenCompressed_GeneratesObservations()
    {
        var messages = Enumerable.Range(1, 10)
            .Select(i => CreateMessage($"m{i}", new string('x', 100)))
            .ToArray();

        var result = await _sut.CompressAsync(messages, _defaultOptions);

        result.WasCompressed.Should().BeTrue();
        result.Observations.Should().NotBeEmpty();
    }

    [Fact]
    public async Task CompressAsync_WhenReflectionsEnabled_GeneratesReflection()
    {
        var messages = Enumerable.Range(1, 10)
            .Select(i => CreateMessage($"m{i}", new string('x', 100)))
            .ToArray();

        var result = await _sut.CompressAsync(messages, _defaultOptions);

        result.WasCompressed.Should().BeTrue();
        result.Reflections.Should().NotBeEmpty();
    }

    [Fact]
    public async Task CompressAsync_WhenReflectionsDisabled_DoesNotGenerateReflection()
    {
        var options = new ContextCompressionOptions
        {
            TokenThreshold = 100,
            RecentMessageCount = 3,
            MaxObservations = 2,
            EnableReflections = false
        };
        var messages = Enumerable.Range(1, 10)
            .Select(i => CreateMessage($"m{i}", new string('x', 100)))
            .ToArray();

        var result = await _sut.CompressAsync(messages, options);

        result.Reflections.Should().BeEmpty();
    }

    [Fact]
    public async Task CompressAsync_WithEmptyList_ReturnsUncompressedEmptyResult()
    {
        var result = await _sut.CompressAsync(Array.Empty<Message>(), _defaultOptions);

        result.WasCompressed.Should().BeFalse();
        result.OriginalTokenCount.Should().Be(0);
        result.CompressedTokenCount.Should().Be(0);
        result.RecentMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task CompressAsync_WithExactlyThresholdTokens_DoesNotCompress()
    {
        // threshold=100 tokens → 400 chars; one message with exactly 400 chars
        var messages = new[] { CreateMessage("m1", new string('a', 400)) };

        var result = await _sut.CompressAsync(messages, _defaultOptions);

        result.WasCompressed.Should().BeFalse();
    }

    [Fact]
    public void EstimateTokenCount_ReturnsReasonableValue()
    {
        // 400 chars / 4 = 100 tokens
        var messages = new[] { CreateMessage("m1", new string('a', 400)) };

        var tokens = _sut.EstimateTokenCount(messages);

        tokens.Should().Be(100);
    }

    [Fact]
    public void EstimateTokenCount_WithEmptyList_ReturnsZero()
    {
        var tokens = _sut.EstimateTokenCount(Array.Empty<Message>());

        tokens.Should().Be(0);
    }

    [Fact]
    public void EstimateTokenCount_SumsAcrossAllMessages()
    {
        var messages = new[]
        {
            CreateMessage("m1", new string('a', 40)),   // 10 tokens
            CreateMessage("m2", new string('b', 80)),   // 20 tokens
        };

        var tokens = _sut.EstimateTokenCount(messages);

        tokens.Should().Be(30);
    }

    private static Message CreateMessage(string id, string content) => new()
    {
        MessageId = id,
        ConversationId = "conv-1",
        SessionId = "session-1",
        Role = "user",
        Content = content,
        TimestampUtc = DateTimeOffset.UtcNow
    };
}
