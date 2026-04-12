using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Extraction.Llm;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.Extraction;

public sealed class LlmPreferenceExtractorTests
{
    private static readonly Message SampleMessage = new()
    {
        MessageId = "m-1",
        ConversationId = "c-1",
        SessionId = "s-1",
        Role = "user",
        Content = "I prefer concise answers and use Python for scripting.",
        TimestampUtc = DateTimeOffset.UtcNow
    };

    private static LlmPreferenceExtractor CreateSut(IChatClient chatClient, Action<LlmExtractionOptions>? configure = null)
    {
        var options = new LlmExtractionOptions();
        configure?.Invoke(options);
        return new LlmPreferenceExtractor(chatClient, Options.Create(options), NullLogger<LlmPreferenceExtractor>.Instance);
    }

    [Fact]
    public async Task ExtractAsync_EmptyMessages_ReturnsEmpty()
    {
        var client = Substitute.For<IChatClient>();
        var sut = CreateSut(client);

        var result = await sut.ExtractAsync(Array.Empty<Message>());

        result.Should().BeEmpty();
        await client.DidNotReceive().GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_ValidJson_ReturnsPreferences()
    {
        const string json = """
            {"preferences": [
              {"category": "communication_style", "preference": "Prefers concise answers", "context": "explicitly stated", "confidence": 0.9},
              {"category": "technology", "preference": "Uses Python for scripting", "context": null, "confidence": 0.85}
            ]}
            """;

        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json))));

        var sut = CreateSut(client);
        var result = await sut.ExtractAsync(new[] { SampleMessage });

        result.Should().HaveCount(2);
        result[0].Category.Should().Be("communication_style");
        result[0].PreferenceText.Should().Be("Prefers concise answers");
        result[0].Context.Should().Be("explicitly stated");
        result[0].Confidence.Should().Be(0.9);
        result[1].Category.Should().Be("technology");
        result[1].Context.Should().BeNull();
    }

    [Fact]
    public async Task ExtractAsync_MalformedJson_ReturnsEmpty()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "```json\nnot parseable```"))));

        var sut = CreateSut(client);
        var result = await sut.ExtractAsync(new[] { SampleMessage });

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_MultipleMessages_ConcatenatesContent()
    {
        const string json = """{"preferences": [{"category": "tools", "preference": "Prefers VS Code", "confidence": 0.88}]}""";

        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json))));

        var sut = CreateSut(client);
        var messages = new[]
        {
            SampleMessage,
            SampleMessage with { MessageId = "m-2", Content = "I use VS Code as my editor." }
        };

        var result = await sut.ExtractAsync(messages);

        result.Should().HaveCount(1);
        result[0].PreferenceText.Should().Be("Prefers VS Code");
    }

    [Fact]
    public async Task ExtractAsync_ConfidenceValues_MappedCorrectly()
    {
        const string json = """{"preferences": [{"category": "format", "preference": "Likes bullet points", "confidence": 0.75}]}""";

        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json))));

        var sut = CreateSut(client);
        var result = await sut.ExtractAsync(new[] { SampleMessage });

        result[0].Confidence.Should().Be(0.75);
    }

    [Fact]
    public async Task ExtractAsync_NullContext_HandledGracefully()
    {
        const string json = """{"preferences": [{"category": "language", "preference": "Prefers English", "confidence": 0.9}]}""";

        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json))));

        var sut = CreateSut(client);
        var result = await sut.ExtractAsync(new[] { SampleMessage });

        result.Should().HaveCount(1);
        result[0].Context.Should().BeNull();
    }

    [Fact]
    public async Task ExtractAsync_ClientThrows_ReturnsEmpty()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns<Task<ChatResponse>>(_ => throw new TaskCanceledException("cancelled"));

        var sut = CreateSut(client);
        var result = await sut.ExtractAsync(new[] { SampleMessage });

        result.Should().BeEmpty();
    }
}
