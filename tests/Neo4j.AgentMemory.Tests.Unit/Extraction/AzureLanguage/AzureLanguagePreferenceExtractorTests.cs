using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Extraction.AzureLanguage;
using Neo4j.AgentMemory.Extraction.AzureLanguage.Internal;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Neo4j.AgentMemory.Tests.Unit.Extraction.AzureLanguage;

public sealed class AzureLanguagePreferenceExtractorTests
{
    private static readonly Message SampleMessage = new()
    {
        MessageId = "m-1",
        ConversationId = "c-1",
        SessionId = "s-1",
        Role = "user",
        Content = "I really love using C# and functional programming.",
        TimestampUtc = DateTimeOffset.UtcNow
    };

    private static AzureLanguagePreferenceExtractor CreateSut(
        ITextAnalyticsClientWrapper client,
        Action<AzureLanguageOptions>? configure = null)
    {
        var options = new AzureLanguageOptions
        {
            Endpoint = "https://test.cognitiveservices.azure.com",
            ApiKey = "test-key"
        };
        configure?.Invoke(options);
        return new AzureLanguagePreferenceExtractor(
            client,
            Options.Create(options),
            NullLogger<AzureLanguagePreferenceExtractor>.Instance);
    }

    private static ITextAnalyticsClientWrapper SetupPositiveSentiment(double score = 0.92)
    {
        var client = Substitute.For<ITextAnalyticsClientWrapper>();
        client.AnalyzeSentimentAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new AzureSentimentResult("positive", score, 0.05, 0.03));
        return client;
    }

    private static ITextAnalyticsClientWrapper SetupNegativeSentiment(double score = 0.88)
    {
        var client = Substitute.For<ITextAnalyticsClientWrapper>();
        client.AnalyzeSentimentAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new AzureSentimentResult("negative", 0.04, score, 0.08));
        return client;
    }

    [Fact]
    public async Task ExtractAsync_EmptyMessages_ReturnsEmpty()
    {
        var client = Substitute.For<ITextAnalyticsClientWrapper>();
        var sut = CreateSut(client);

        var result = await sut.ExtractAsync(Array.Empty<Message>());

        result.Should().BeEmpty();
        await client.DidNotReceive().AnalyzeSentimentAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_PositiveSentiment_ReturnsLikePreferences()
    {
        var client = SetupPositiveSentiment();
        client.ExtractKeyPhrasesAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<string> { "C#", "functional programming" });

        var sut = CreateSut(client);
        var result = await sut.ExtractAsync(new[] { SampleMessage });

        result.Should().HaveCount(2);
        result.Should().AllSatisfy(p => p.Category.Should().Be("like"));
        result.Should().Contain(p => p.PreferenceText == "likes C#");
        result.Should().Contain(p => p.PreferenceText == "likes functional programming");
    }

    [Fact]
    public async Task ExtractAsync_NegativeSentiment_ReturnsDislikePreferences()
    {
        var client = SetupNegativeSentiment();
        client.ExtractKeyPhrasesAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<string> { "long meetings", "bureaucracy" });

        var sut = CreateSut(client);
        var negativeMessage = SampleMessage with { Content = "I hate long meetings and bureaucracy." };
        var result = await sut.ExtractAsync(new[] { negativeMessage });

        result.Should().HaveCount(2);
        result.Should().AllSatisfy(p => p.Category.Should().Be("dislike"));
        result.Should().Contain(p => p.PreferenceText == "dislikes long meetings");
        result.Should().Contain(p => p.PreferenceText == "dislikes bureaucracy");
    }

    [Fact]
    public async Task ExtractAsync_NeutralSentiment_ReturnsEmpty()
    {
        var client = Substitute.For<ITextAnalyticsClientWrapper>();
        client.AnalyzeSentimentAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new AzureSentimentResult("neutral", 0.2, 0.2, 0.6));
        client.ExtractKeyPhrasesAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<string> { "the weather", "today" });

        var sut = CreateSut(client);
        var result = await sut.ExtractAsync(new[] { SampleMessage with { Content = "The weather is okay today." } });

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_MapsAzureConfidenceScoreCorrectly()
    {
        const double expectedScore = 0.95;
        var client = SetupPositiveSentiment(expectedScore);
        client.ExtractKeyPhrasesAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<string> { "dark chocolate" });

        var sut = CreateSut(client);
        var result = await sut.ExtractAsync(new[] { SampleMessage });

        result.Single().Confidence.Should().BeApproximately(expectedScore, 0.0001);
    }

    [Fact]
    public async Task ExtractAsync_NegativeMapsConfidenceScore()
    {
        const double expectedScore = 0.91;
        var client = SetupNegativeSentiment(expectedScore);
        client.ExtractKeyPhrasesAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<string> { "slow internet" });

        var sut = CreateSut(client);
        var result = await sut.ExtractAsync(new[] { SampleMessage });

        result.Single().Confidence.Should().BeApproximately(expectedScore, 0.0001);
    }

    [Fact]
    public async Task ExtractAsync_AzureServiceError_ReturnsEmpty()
    {
        var client = Substitute.For<ITextAnalyticsClientWrapper>();
        client.AnalyzeSentimentAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Azure service unavailable"));

        var sut = CreateSut(client);
        var result = await sut.ExtractAsync(new[] { SampleMessage });

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_SetsCorrectPreferenceType_Like()
    {
        var client = SetupPositiveSentiment();
        client.ExtractKeyPhrasesAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<string> { "open source" });

        var sut = CreateSut(client);
        var result = await sut.ExtractAsync(new[] { SampleMessage });

        result.Single().Category.Should().Be("like");
    }

    [Fact]
    public async Task ExtractAsync_SetsCorrectPreferenceType_Dislike()
    {
        var client = SetupNegativeSentiment();
        client.ExtractKeyPhrasesAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<string> { "vendor lock-in" });

        var sut = CreateSut(client);
        var result = await sut.ExtractAsync(new[] { SampleMessage });

        result.Single().Category.Should().Be("dislike");
    }

    [Fact]
    public async Task ExtractAsync_PopulatesTopicFromKeyPhrase()
    {
        var client = SetupPositiveSentiment();
        client.ExtractKeyPhrasesAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<string> { "unit testing" });

        var sut = CreateSut(client);
        var result = await sut.ExtractAsync(new[] { SampleMessage });

        result.Single().PreferenceText.Should().Contain("unit testing");
    }

    [Fact]
    public async Task ExtractAsync_MultiplePreferencesFromRichText()
    {
        var client = SetupPositiveSentiment();
        client.ExtractKeyPhrasesAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<string> { "SOLID principles", "clean code", "TDD", "pair programming" });

        var sut = CreateSut(client);
        var result = await sut.ExtractAsync(new[] { SampleMessage });

        result.Should().HaveCount(4);
        result.Should().AllSatisfy(p => p.Category.Should().Be("like"));
    }

    [Fact]
    public async Task ExtractAsync_SentimentBelowThreshold_ReturnsEmpty()
    {
        var client = Substitute.For<ITextAnalyticsClientWrapper>();
        // Positive score of 0.5 is below the default threshold of 0.7
        client.AnalyzeSentimentAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new AzureSentimentResult("mixed", 0.5, 0.3, 0.2));
        client.ExtractKeyPhrasesAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<string> { "something" });

        var sut = CreateSut(client);
        var result = await sut.ExtractAsync(new[] { SampleMessage });

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_CustomThreshold_UsedCorrectly()
    {
        var client = Substitute.For<ITextAnalyticsClientWrapper>();
        // Score of 0.65 is below the default 0.7 but above a custom 0.6
        client.AnalyzeSentimentAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new AzureSentimentResult("positive", 0.65, 0.2, 0.15));
        client.ExtractKeyPhrasesAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<string> { "agile" });

        var sut = CreateSut(client, opts => opts.PreferenceSentimentThreshold = 0.6);
        var result = await sut.ExtractAsync(new[] { SampleMessage });

        result.Should().HaveCount(1);
        result.Single().Category.Should().Be("like");
    }
}
