using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Extraction.AzureLanguage;
using Neo4j.AgentMemory.Extraction.AzureLanguage.Internal;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Neo4j.AgentMemory.Tests.Unit.Extraction.AzureLanguage;

public sealed class AzureLanguageRelationshipExtractorTests
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

    private static AzureLanguageRelationshipExtractor CreateSut(
        ITextAnalyticsClientWrapper client,
        Action<AzureLanguageOptions>? configure = null)
    {
        var options = new AzureLanguageOptions { Endpoint = "https://test.cognitiveservices.azure.com", ApiKey = "test-key" };
        configure?.Invoke(options);
        return new AzureLanguageRelationshipExtractor(
            client,
            Options.Create(options),
            NullLogger<AzureLanguageRelationshipExtractor>.Instance);
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
    public async Task Extract_CoOccurringEntities_ReturnsRelationships()
    {
        var client = Substitute.For<ITextAnalyticsClientWrapper>();
        client.RecognizeEntitiesAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzureRecognizedEntity>
            {
                new("Alice", "Person", 0.9, null),
                new("Acme Corp", "Organization", 0.85, null),
                new("New York", "Location", 0.8, null)
            });

        var sut = CreateSut(client);
        var result = await sut.ExtractAsync(new[] { SampleMessage });

        // 3 entities → 3 pairs: (Alice,Acme), (Alice,NewYork), (Acme,NewYork)
        result.Should().HaveCount(3);
        result.Should().AllSatisfy(r => r.RelationshipType.Should().Be("co-occurs with"));
    }

    [Fact]
    public async Task Extract_SingleEntity_NoRelationships()
    {
        var client = Substitute.For<ITextAnalyticsClientWrapper>();
        client.RecognizeEntitiesAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzureRecognizedEntity>
            {
                new("Alice", "Person", 0.9, null)
            });

        var sut = CreateSut(client);
        var result = await sut.ExtractAsync(new[] { SampleMessage });

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Extract_ClientError_ReturnsEmpty()
    {
        var client = Substitute.For<ITextAnalyticsClientWrapper>();
        client.RecognizeEntitiesAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Azure service unavailable"));

        var sut = CreateSut(client);
        var result = await sut.ExtractAsync(new[] { SampleMessage });

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Extract_SetsConfidenceScore()
    {
        var client = Substitute.For<ITextAnalyticsClientWrapper>();
        client.RecognizeEntitiesAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzureRecognizedEntity>
            {
                new("Alice", "Person", 0.8, null),
                new("Bob", "Person", 0.6, null)
            });

        var sut = CreateSut(client);
        var result = await sut.ExtractAsync(new[] { SampleMessage });

        result.Should().HaveCount(1);
        // Confidence = average of the two entity scores
        result.Single().Confidence.Should().BeApproximately(0.7, 0.001);
    }
}
