using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Extraction.Llm;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.Extraction;

public sealed class LlmEntityExtractorTests
{
    private static readonly Message SampleMessage = new()
    {
        MessageId = "m-1",
        ConversationId = "c-1",
        SessionId = "s-1",
        Role = "user",
        Content = "Alice works at Acme Corp in New York.",
        TimestampUtc = DateTimeOffset.UtcNow
    };

    private static LlmEntityExtractor CreateSut(IChatClient chatClient, Action<LlmExtractionOptions>? configure = null)
    {
        var options = new LlmExtractionOptions();
        configure?.Invoke(options);
        return new LlmEntityExtractor(chatClient, Options.Create(options), NullLogger<LlmEntityExtractor>.Instance);
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
    public async Task ExtractAsync_ValidJson_ReturnsEntities()
    {
        const string json = """
            {"entities": [
              {"name": "Alice", "type": "PERSON", "confidence": 0.95, "aliases": []},
              {"name": "Acme Corp", "type": "ORGANIZATION", "confidence": 0.9, "aliases": ["Acme"]}
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
        result[0].Name.Should().Be("Alice");
        result[0].Type.Should().Be("PERSON");
        result[0].Confidence.Should().Be(0.95);
        result[1].Name.Should().Be("Acme Corp");
        result[1].Aliases.Should().Contain("Acme");
    }

    [Fact]
    public async Task ExtractAsync_MalformedJson_ReturnsEmpty()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "not json at all!"))));

        var sut = CreateSut(client);
        var result = await sut.ExtractAsync(new[] { SampleMessage });

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_MultipleMessages_ConcatenatesContent()
    {
        const string json = """{"entities": [{"name": "Bob", "type": "PERSON", "confidence": 0.9, "aliases": []}]}""";

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
            SampleMessage with { MessageId = "m-2", Content = "Bob joined the meeting." }
        };

        var result = await sut.ExtractAsync(messages);

        result.Should().HaveCount(1);
        result[0].Name.Should().Be("Bob");
    }

    [Fact]
    public async Task ExtractAsync_TypeNormalization_ConceptBecomesObject()
    {
        const string json = """{"entities": [{"name": "Machine Learning", "type": "CONCEPT", "confidence": 0.85, "aliases": []}]}""";

        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json))));

        var sut = CreateSut(client);
        var result = await sut.ExtractAsync(new[] { SampleMessage });

        result.Should().HaveCount(1);
        result[0].Type.Should().Be("OBJECT");
    }

    [Theory]
    [InlineData("CONCEPT", "OBJECT")]
    [InlineData("PLACE", "LOCATION")]
    [InlineData("COMPANY", "ORGANIZATION")]
    [InlineData("INDIVIDUAL", "PERSON")]
    [InlineData("PERSON", "PERSON")]
    public async Task ExtractAsync_TypeNormalization_AllMappings(string inputType, string expectedType)
    {
        var json = $$"""{"entities": [{"name": "Test", "type": "{{inputType}}", "confidence": 0.9, "aliases": []}]}""";

        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json))));

        var sut = CreateSut(client);
        var result = await sut.ExtractAsync(new[] { SampleMessage });

        result[0].Type.Should().Be(expectedType);
    }

    [Fact]
    public async Task ExtractAsync_NullOptionalFields_HandledGracefully()
    {
        const string json = """{"entities": [{"name": "London", "type": "LOCATION", "confidence": 0.9}]}""";

        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json))));

        var sut = CreateSut(client);
        var result = await sut.ExtractAsync(new[] { SampleMessage });

        result.Should().HaveCount(1);
        result[0].Subtype.Should().BeNull();
        result[0].Description.Should().BeNull();
        result[0].Aliases.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_ClientThrows_ReturnsEmpty()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns<Task<ChatResponse>>(_ => throw new HttpRequestException("network error"));

        var sut = CreateSut(client);
        var result = await sut.ExtractAsync(new[] { SampleMessage });

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_ModelIdPropagated_WhenConfigured()
    {
        const string json = """{"entities": []}""";
        ChatOptions? capturedOptions = null;

        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Do<ChatOptions?>(o => capturedOptions = o),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json))));

        var sut = CreateSut(client, o => o.ModelId = "gpt-4o");
        await sut.ExtractAsync(new[] { SampleMessage });

        capturedOptions!.ModelId.Should().Be("gpt-4o");
    }
}
