using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Core.Services;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Services;

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

    // R6-D: Tier-3 keeps the MOST RECENT messages verbatim and summarizes older ones. The production caller
    // (ObservationTools → GetRecentMessagesAsync) passes NEWEST-FIRST, so the method must normalize order —
    // before the fix it kept the OLDEST verbatim and fed the NEWEST turns to summarization.
    [Fact]
    public async Task CompressAsync_NewestFirstInput_KeepsNewestVerbatim_SummarizesOldest()
    {
        var baseTime = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        // Newest-first: i=6 (newest) … i=1 (oldest). Each ~100 chars so 6 msgs (150 tokens) exceed threshold.
        var messages = Enumerable.Range(1, 6).Reverse()
            .Select(i => new Message
            {
                MessageId = $"m{i}",
                ConversationId = "conv-1",
                SessionId = "session-1",
                Role = "user",
                Content = $"MSG{i}-" + new string('x', 100),
                TimestampUtc = baseTime.AddMinutes(i)
            })
            .ToList();

        var result = await _sut.CompressAsync(messages, _defaultOptions); // RecentMessageCount = 3

        result.WasCompressed.Should().BeTrue();
        var keptIds = result.RecentMessages.Select(m => m.MessageId).ToList();
        keptIds.Should().BeEquivalentTo(new[] { "m4", "m5", "m6" },
            "the 3 most-recent messages (by timestamp) must be kept verbatim, regardless of input order");
        keptIds.Should().NotContain("m1", "the oldest message must be summarized away, not kept verbatim");
    }

    [Fact]
    public async Task CompressAsync_CallerCancellation_PropagatesInsteadOfFallbackText()
    {
        // When the caller cancels mid-compression, the LLM call's OperationCanceledException must
        // propagate — not be swallowed and replaced with placeholder summary/reflection text that would
        // make a cancelled operation look like a successful compression.
        using var cts = new CancellationTokenSource();
        _chatClient
            .GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns<Task<ChatResponse>>(_ => { cts.Cancel(); throw new OperationCanceledException(cts.Token); });

        var messages = Enumerable.Range(1, 6)
            .Select(i => CreateMessage($"m{i}", new string('x', 100)))
            .ToArray();

        var act = () => _sut.CompressAsync(messages, _defaultOptions, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
