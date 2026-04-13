using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Extraction.AzureLanguage;
using Neo4j.AgentMemory.Extraction.AzureLanguage.Internal;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Neo4j.AgentMemory.Tests.Unit.Extraction.AzureLanguage;

public sealed class AzureLanguageFactExtractorTests
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

    private static AzureLanguageFactExtractor CreateSut(
        ITextAnalyticsClientWrapper client,
        Action<AzureLanguageOptions>? configure = null)
    {
        var options = new AzureLanguageOptions { Endpoint = "https://test.cognitiveservices.azure.com", ApiKey = "test-key" };
        configure?.Invoke(options);
        return new AzureLanguageFactExtractor(
            client,
            Options.Create(options),
            NullLogger<AzureLanguageFactExtractor>.Instance);
    }

    [Fact]
    public async Task Extract_EmptyMessages_ReturnsEmpty()
    {
        var client = Substitute.For<ITextAnalyticsClientWrapper>();
        var sut = CreateSut(client);

        var result = await sut.ExtractAsync(Array.Empty<Message>());

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Extract_KeyPhrases_ReturnsFacts()
    {
        var client = Substitute.For<ITextAnalyticsClientWrapper>();
        client.ExtractKeyPhrasesAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<string> { "machine learning", "data science" });
        client.RecognizeLinkedEntitiesAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzureLinkedEntity>());

        var sut = CreateSut(client);
        var result = await sut.ExtractAsync(new[] { SampleMessage });

        result.Should().HaveCount(2);
        result.Should().Contain(f => f.Subject == "machine learning" && f.Predicate == "mentioned in conversation");
        result.Should().Contain(f => f.Subject == "data science");
    }

    [Fact]
    public async Task Extract_LinkedEntities_ReturnsFacts()
    {
        var client = Substitute.For<ITextAnalyticsClientWrapper>();
        client.ExtractKeyPhrasesAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<string>());
        client.RecognizeLinkedEntitiesAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzureLinkedEntity>
            {
                new("New York", "https://en.wikipedia.org/wiki/New_York_City")
            });

        var sut = CreateSut(client);
        var result = await sut.ExtractAsync(new[] { SampleMessage });

        result.Should().HaveCount(1);
        result.Single().Subject.Should().Be("New York");
        result.Single().Predicate.Should().Be("is described as");
        result.Single().Object.Should().Be("https://en.wikipedia.org/wiki/New_York_City");
    }

    [Fact]
    public async Task Extract_EmptyResponse_ReturnsEmpty()
    {
        var client = Substitute.For<ITextAnalyticsClientWrapper>();
        client.ExtractKeyPhrasesAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<string>());
        client.RecognizeLinkedEntitiesAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzureLinkedEntity>());

        var sut = CreateSut(client);
        var result = await sut.ExtractAsync(new[] { SampleMessage });

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Extract_ClientError_ReturnsEmpty()
    {
        var client = Substitute.For<ITextAnalyticsClientWrapper>();
        client.ExtractKeyPhrasesAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Service error"));

        var sut = CreateSut(client);
        var result = await sut.ExtractAsync(new[] { SampleMessage });

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Extract_MapsFieldsCorrectly()
    {
        var client = Substitute.For<ITextAnalyticsClientWrapper>();
        client.ExtractKeyPhrasesAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<string> { "key phrase" });
        client.RecognizeLinkedEntitiesAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzureLinkedEntity>());

        var sut = CreateSut(client);
        var result = await sut.ExtractAsync(new[] { SampleMessage });

        var fact = result.Single();
        fact.Subject.Should().Be("key phrase");
        fact.Predicate.Should().Be("mentioned in conversation");
        fact.Confidence.Should().Be(0.7);
        fact.Object.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Extract_MultipleMessages_CombinesResults()
    {
        var client = Substitute.For<ITextAnalyticsClientWrapper>();
        client.ExtractKeyPhrasesAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<string> { "phrase one" });
        client.RecognizeLinkedEntitiesAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzureLinkedEntity>());

        var sut = CreateSut(client);
        var messages = new[]
        {
            SampleMessage,
            SampleMessage with { MessageId = "m-2", Content = "Another message." }
        };

        var result = await sut.ExtractAsync(messages);

        // One fact per message (phrase one from each)
        result.Should().HaveCount(2);
    }
}
