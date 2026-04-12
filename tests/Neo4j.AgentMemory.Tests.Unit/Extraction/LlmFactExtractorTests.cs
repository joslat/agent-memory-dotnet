using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Extraction.Llm;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.Extraction;

public sealed class LlmFactExtractorTests
{
    private static readonly Message SampleMessage = new()
    {
        MessageId = "m-1",
        ConversationId = "c-1",
        SessionId = "s-1",
        Role = "user",
        Content = "Alice works at Acme Corp.",
        TimestampUtc = DateTimeOffset.UtcNow
    };

    private static LlmFactExtractor CreateSut(IChatClient chatClient, Action<LlmExtractionOptions>? configure = null)
    {
        var options = new LlmExtractionOptions();
        configure?.Invoke(options);
        return new LlmFactExtractor(chatClient, Options.Create(options), NullLogger<LlmFactExtractor>.Instance);
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
    public async Task ExtractAsync_ValidJson_ReturnsFacts()
    {
        const string json = """
            {"facts": [
              {"subject": "Alice", "predicate": "works_at", "object": "Acme Corp", "confidence": 0.95},
              {"subject": "Acme Corp", "predicate": "located_in", "object": "New York", "confidence": 0.85}
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
        result[0].Subject.Should().Be("Alice");
        result[0].Predicate.Should().Be("works_at");
        result[0].Object.Should().Be("Acme Corp");
        result[0].Confidence.Should().Be(0.95);
        result[1].Subject.Should().Be("Acme Corp");
    }

    [Fact]
    public async Task ExtractAsync_MalformedJson_ReturnsEmpty()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{invalid}"))));

        var sut = CreateSut(client);
        var result = await sut.ExtractAsync(new[] { SampleMessage });

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_MultipleMessages_ConcatenatesContent()
    {
        const string json = """{"facts": [{"subject": "Bob", "predicate": "is_cto_of", "object": "TechCo", "confidence": 0.9}]}""";

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
            SampleMessage with { MessageId = "m-2", Content = "Bob is the CTO of TechCo." }
        };

        var result = await sut.ExtractAsync(messages);

        result.Should().HaveCount(1);
        result[0].Subject.Should().Be("Bob");
    }

    [Fact]
    public async Task ExtractAsync_ConfidenceValues_MappedCorrectly()
    {
        const string json = """{"facts": [{"subject": "Alice", "predicate": "age", "object": "30", "confidence": 0.75}]}""";

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
    public async Task ExtractAsync_ClientThrows_ReturnsEmpty()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns<Task<ChatResponse>>(_ => throw new InvalidOperationException("timeout"));

        var sut = CreateSut(client);
        var result = await sut.ExtractAsync(new[] { SampleMessage });

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_EmptyFactsArray_ReturnsEmpty()
    {
        const string json = """{"facts": []}""";

        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json))));

        var sut = CreateSut(client);
        var result = await sut.ExtractAsync(new[] { SampleMessage });

        result.Should().BeEmpty();
    }
}
