using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Extraction.Llm;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.Extraction;

public sealed class LlmRelationshipExtractorTests
{
    private static readonly Message SampleMessage = new()
    {
        MessageId = "m-1",
        ConversationId = "c-1",
        SessionId = "s-1",
        Role = "user",
        Content = "Alice knows Bob. Alice works at Acme Corp.",
        TimestampUtc = DateTimeOffset.UtcNow
    };

    private static LlmRelationshipExtractor CreateSut(IChatClient chatClient, Action<LlmExtractionOptions>? configure = null)
    {
        var options = new LlmExtractionOptions();
        configure?.Invoke(options);
        return new LlmRelationshipExtractor(chatClient, Options.Create(options), NullLogger<LlmRelationshipExtractor>.Instance);
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
    public async Task ExtractAsync_ValidJson_ReturnsRelationships()
    {
        const string json = """
            {"relations": [
              {"source": "Alice", "target": "Bob", "relation_type": "KNOWS", "description": "colleagues", "confidence": 0.9},
              {"source": "Alice", "target": "Acme Corp", "relation_type": "WORKS_AT", "description": null, "confidence": 0.95}
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
        result[0].SourceEntity.Should().Be("Alice");
        result[0].TargetEntity.Should().Be("Bob");
        result[0].RelationshipType.Should().Be("KNOWS");
        result[0].Description.Should().Be("colleagues");
        result[0].Confidence.Should().Be(0.9);
        result[1].SourceEntity.Should().Be("Alice");
        result[1].TargetEntity.Should().Be("Acme Corp");
        result[1].Description.Should().BeNull();
    }

    [Fact]
    public async Task ExtractAsync_MalformedJson_ReturnsEmpty()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "I found some relationships..."))));

        var sut = CreateSut(client);
        var result = await sut.ExtractAsync(new[] { SampleMessage });

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_MultipleMessages_ConcatenatesContent()
    {
        const string json = """{"relations": [{"source": "Carol", "target": "TechCo", "relation_type": "FOUNDED", "confidence": 0.85}]}""";

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
            SampleMessage with { MessageId = "m-2", Content = "Carol founded TechCo." }
        };

        var result = await sut.ExtractAsync(messages);

        result.Should().HaveCount(1);
        result[0].SourceEntity.Should().Be("Carol");
        result[0].RelationshipType.Should().Be("FOUNDED");
    }

    [Fact]
    public async Task ExtractAsync_ConfidenceValues_MappedCorrectly()
    {
        const string json = """{"relations": [{"source": "X", "target": "Y", "relation_type": "PART_OF", "confidence": 0.7}]}""";

        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json))));

        var sut = CreateSut(client);
        var result = await sut.ExtractAsync(new[] { SampleMessage });

        result[0].Confidence.Should().Be(0.7);
    }

    [Fact]
    public async Task ExtractAsync_NullDescription_HandledGracefully()
    {
        const string json = """{"relations": [{"source": "Alice", "target": "Bob", "relation_type": "KNOWS", "confidence": 0.9}]}""";

        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json))));

        var sut = CreateSut(client);
        var result = await sut.ExtractAsync(new[] { SampleMessage });

        result.Should().HaveCount(1);
        result[0].Description.Should().BeNull();
    }

    [Fact]
    public async Task ExtractAsync_ClientThrows_ReturnsEmpty()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns<Task<ChatResponse>>(_ => throw new HttpRequestException("server unavailable"));

        var sut = CreateSut(client);
        var result = await sut.ExtractAsync(new[] { SampleMessage });

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_EmptyRelationsArray_ReturnsEmpty()
    {
        const string json = """{"relations": []}""";

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
