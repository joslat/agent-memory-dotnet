using AgentMemory.Abstractions.Domain;
using AgentMemory.Extraction.Llm;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Extraction;

public sealed class LlmUnifiedMemoryExtractorTests
{
    private static readonly Message Message = new()
    {
        MessageId = "message-1",
        ConversationId = "conversation-1",
        SessionId = "session-1",
        Role = "user",
        Content = "Alice knows Bob, works at Acme, and prefers tea.",
        TimestampUtc = DateTimeOffset.UtcNow,
    };

    private const string CompleteJson =
        """
        {
          "entities": [
            {"name":"Alice","type":"PERSON","confidence":0.95,"aliases":[]},
            {"name":"Bob","type":"PERSON","confidence":0.94,"aliases":[]}
          ],
          "facts": [
            {"subject":"Alice","predicate":"knows","object":"Bob","confidence":0.93},
            {"subject":"Alice","predicate":"works_at","object":"Acme","confidence":0.92}
          ],
          "preferences": [
            {"category":"drink","preference":"tea","confidence":0.91}
          ],
          "relations": [
            {"source":"Alice","target":"Bob","relation_type":"KNOWS","confidence":0.90}
          ]
        }
        """;

    [Fact]
    public async Task ExtractAsync_CompleteResponse_MapsEveryCategoryInOneCall()
    {
        var client = ClientReturning(CompleteJson);
        var sut = CreateSut(client, enabled: true);

        var result = await sut.ExtractAsync([Message]);

        result.Entities.Should().HaveCount(2);
        result.Facts.Should().HaveCount(2);
        result.Preferences.Should().ContainSingle();
        result.Relationships.Should().ContainSingle();
        await client.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Is<ChatOptions>(options => options.ResponseFormat == ChatResponseFormat.Json),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_ParseRetryThenSuccess_UsesExactlyTwoCalls()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(Response("{invalid}")),
                Task.FromResult(Response(CompleteJson)));
        var sut = CreateSut(client, maxRetries: 1);

        var result = await sut.ExtractAsync([Message]);

        result.Entities.Should().HaveCount(2);
        await client.Received(2).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_ParseRetriesExhausted_Throws()
    {
        var client = ClientReturning("{invalid}");
        var sut = CreateSut(client, maxRetries: 1);

        var act = () => sut.ExtractAsync([Message]);

        await act.Should().ThrowAsync<FormatException>()
            .WithMessage("*exhausted*valid JSON*");
        await client.Received(2).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_EmptyInput_DoesNotCallProvider()
    {
        var client = Substitute.For<IChatClient>();
        var sut = CreateSut(client);

        var result = await sut.ExtractAsync([]);

        result.Should().Be(new UnifiedExtractionResult());
        await client.DidNotReceive().GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void IsEnabled_ReflectsExplicitOption()
    {
        var client = Substitute.For<IChatClient>();

        CreateSut(client, enabled: false).IsEnabled.Should().BeFalse();
        CreateSut(client, enabled: true).IsEnabled.Should().BeTrue();
    }

    private static LlmUnifiedMemoryExtractor CreateSut(
        IChatClient client,
        bool enabled = false,
        int maxRetries = 0)
    {
        var options = new LlmExtractionOptions
        {
            UseUnifiedExtraction = enabled,
            MaxRetries = maxRetries,
        };
        return new LlmUnifiedMemoryExtractor(
            client,
            Options.Create(options),
            NullLogger<LlmUnifiedMemoryExtractor>.Instance);
    }

    private static IChatClient ClientReturning(string text)
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Response(text)));
        return client;
    }

    private static ChatResponse Response(string text) =>
        new(new ChatMessage(ChatRole.Assistant, text));
}
