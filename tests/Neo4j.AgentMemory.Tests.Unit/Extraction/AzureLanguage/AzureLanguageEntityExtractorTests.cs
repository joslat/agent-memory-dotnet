using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Extraction.AzureLanguage;
using Neo4j.AgentMemory.Extraction.AzureLanguage.Internal;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Neo4j.AgentMemory.Tests.Unit.Extraction.AzureLanguage;

public sealed class AzureLanguageEntityExtractorTests
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

    private static AzureLanguageEntityExtractor CreateSut(
        ITextAnalyticsClientWrapper client,
        Action<AzureLanguageOptions>? configure = null)
    {
        var options = new AzureLanguageOptions { Endpoint = "https://test.cognitiveservices.azure.com", ApiKey = "test-key" };
        configure?.Invoke(options);
        return new AzureLanguageEntityExtractor(
            client,
            Options.Create(options),
            NullLogger<AzureLanguageEntityExtractor>.Instance,
            new AzureExtractionContext());
    }

    [Fact]
    public async Task Extract_EmptyMessages_ReturnsEmpty()
    {
        var client = Substitute.For<ITextAnalyticsClientWrapper>();
        var sut = CreateSut(client);

        var result = await sut.ExtractAsync(Array.Empty<Message>());

        result.Should().BeEmpty();
        await client.DidNotReceive().RecognizeEntitiesAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Extract_SingleMessage_ReturnsMappedEntities()
    {
        var client = Substitute.For<ITextAnalyticsClientWrapper>();
        client.RecognizeEntitiesAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzureRecognizedEntity>
            {
                new("Alice", "Person", 0.95, null),
                new("Acme Corp", "Organization", 0.9, null)
            });

        var sut = CreateSut(client);
        var result = await sut.ExtractAsync(new[] { SampleMessage });

        result.Should().HaveCount(2);
        result.Should().Contain(e => e.Name == "Alice");
        result.Should().Contain(e => e.Name == "Acme Corp");
    }

    [Fact]
    public async Task Extract_PersonCategory_MapsToPersonType()
    {
        var client = Substitute.For<ITextAnalyticsClientWrapper>();
        client.RecognizeEntitiesAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzureRecognizedEntity> { new("Alice", "Person", 0.9, null) });

        var sut = CreateSut(client);
        var result = await sut.ExtractAsync(new[] { SampleMessage });

        result.Single().Type.Should().Be("PERSON");
    }

    [Fact]
    public async Task Extract_OrganizationCategory_MapsToOrganizationType()
    {
        var client = Substitute.For<ITextAnalyticsClientWrapper>();
        client.RecognizeEntitiesAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzureRecognizedEntity> { new("Acme Corp", "Organization", 0.9, null) });

        var sut = CreateSut(client);
        var result = await sut.ExtractAsync(new[] { SampleMessage });

        result.Single().Type.Should().Be("ORGANIZATION");
    }

    [Fact]
    public async Task Extract_LocationCategory_MapsToLocationType()
    {
        var client = Substitute.For<ITextAnalyticsClientWrapper>();
        client.RecognizeEntitiesAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzureRecognizedEntity> { new("New York", "Location", 0.9, "City") });

        var sut = CreateSut(client);
        var result = await sut.ExtractAsync(new[] { SampleMessage });

        result.Single().Type.Should().Be("LOCATION");
    }

    [Fact]
    public async Task Extract_EventCategory_MapsToEventType()
    {
        var client = Substitute.For<ITextAnalyticsClientWrapper>();
        client.RecognizeEntitiesAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzureRecognizedEntity> { new("World Cup", "Event", 0.85, null) });

        var sut = CreateSut(client);
        var result = await sut.ExtractAsync(new[] { SampleMessage });

        result.Single().Type.Should().Be("EVENT");
    }

    [Fact]
    public async Task Extract_UnknownCategory_MapsToObjectType()
    {
        var client = Substitute.For<ITextAnalyticsClientWrapper>();
        client.RecognizeEntitiesAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzureRecognizedEntity> { new("Widget", "UnknownCategory", 0.7, null) });

        var sut = CreateSut(client);
        var result = await sut.ExtractAsync(new[] { SampleMessage });

        result.Single().Type.Should().Be("OBJECT");
    }

    [Fact]
    public async Task Extract_DuplicateEntities_Deduplicated()
    {
        var client = Substitute.For<ITextAnalyticsClientWrapper>();
        // Two messages both mention "Alice" — should be deduplicated
        client.RecognizeEntitiesAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzureRecognizedEntity> { new("Alice", "Person", 0.9, null) });

        var sut = CreateSut(client);
        var messages = new[]
        {
            SampleMessage,
            SampleMessage with { MessageId = "m-2", Content = "Alice left the building." }
        };

        var result = await sut.ExtractAsync(messages);

        result.Should().HaveCount(1);
        result.Single().Name.Should().Be("Alice");
    }

    [Fact]
    public async Task Extract_ClientError_ReturnsEmpty_LogsWarning()
    {
        var client = Substitute.For<ITextAnalyticsClientWrapper>();
        client.RecognizeEntitiesAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Azure service unavailable"));

        var sut = CreateSut(client);
        var result = await sut.ExtractAsync(new[] { SampleMessage });

        result.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Person", "PERSON")]
    [InlineData("Organization", "ORGANIZATION")]
    [InlineData("Location", "LOCATION")]
    [InlineData("Address", "LOCATION")]
    [InlineData("GPE", "LOCATION")]
    [InlineData("Event", "EVENT")]
    [InlineData("Product", "OBJECT")]
    [InlineData("Skill", "OBJECT")]
    [InlineData("DateTime", "OBJECT")]
    [InlineData("Quantity", "OBJECT")]
    [InlineData("AnythingElse", "OBJECT")]
    public void MapCategory_AllKnownMappings(string input, string expected)
    {
        AzureLanguageEntityExtractor.MapCategory(input).Should().Be(expected);
    }
}
