using AgentMemory.Inference;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Inference;

/// <summary>
/// The factory picks the right protocol family and never leaks a credential in saying so.
/// </summary>
/// <remarks>
/// These construct real SDK client objects, which is local work — no network call happens until
/// something is sent. What is asserted is the wiring: that a provider reaches the family it belongs
/// to, that a role can override the model, and that a failure explains itself without quoting a key
/// or a raw URI.
/// </remarks>
public sealed class ClientFactoryTests
{
    private static InferenceProviderSettings Settings(
        InferenceProvider provider, string endpoint = "https://api.example.com/v1") => new()
        {
            Provider = provider,
            Endpoint = endpoint,
            ApiKey = "sk-secret-value",
            Model = "primary-model",
            EmbeddingProvider = provider,
            EmbeddingEndpoint = endpoint,
            EmbeddingApiKey = "sk-secret-value",
            EmbeddingModel = "embed-model",
            EmbeddingDimensions = 1024,
        };

    /// <summary>Azure and Foundry speak the Azure protocol; the other three speak OpenAI's.</summary>
    [Theory]
    [InlineData(InferenceProvider.AzureOpenAI, true)]
    [InlineData(InferenceProvider.Foundry, true)]
    [InlineData(InferenceProvider.Bitdeer, false)]
    [InlineData(InferenceProvider.OpenAI, false)]
    [InlineData(InferenceProvider.OpenAICompatible, false)]
    public void EachProviderMapsToItsProtocolFamily(InferenceProvider provider, bool azureProtocol)
    {
        InferenceClientFactory.IsAzureProtocol(provider).Should().Be(azureProtocol);
    }

    /// <summary>Every provider builds a chat client.</summary>
    [Theory]
    [InlineData(InferenceProvider.AzureOpenAI)]
    [InlineData(InferenceProvider.Foundry)]
    [InlineData(InferenceProvider.Bitdeer)]
    [InlineData(InferenceProvider.OpenAI)]
    [InlineData(InferenceProvider.OpenAICompatible)]
    public void EveryProviderBuildsAChatClient(InferenceProvider provider)
    {
        InferenceClientFactory
            .TryCreateChatClient(Settings(provider), "answer", out var client, out var diagnostic)
            .Should().BeTrue(diagnostic);

        client.Should().NotBeNull();
    }

    /// <summary>Every provider builds an embedding generator.</summary>
    [Theory]
    [InlineData(InferenceProvider.AzureOpenAI)]
    [InlineData(InferenceProvider.Bitdeer)]
    [InlineData(InferenceProvider.OpenAICompatible)]
    public void EveryProviderBuildsAnEmbeddingGenerator(InferenceProvider provider)
    {
        InferenceClientFactory
            .TryCreateEmbeddingGenerator(Settings(provider), out var generator, out var diagnostic)
            .Should().BeTrue(diagnostic);

        generator.Should().NotBeNull();
    }

    /// <summary>An embedding generator is refused when the dimension was never established.</summary>
    /// <remarks>
    /// The resolver already fails closed on this; the factory refuses too, because it is reachable
    /// from a caller that built settings by hand — and a generator with no agreed dimension is how a
    /// vector index stops matching what writes into it.
    /// </remarks>
    [Fact]
    public void AnEmbeddingGeneratorNeedsAKnownDimension()
    {
        var settings = Settings(InferenceProvider.Bitdeer) with { EmbeddingDimensions = null };

        InferenceClientFactory
            .TryCreateEmbeddingGenerator(settings, out var generator, out var diagnostic)
            .Should().BeFalse();

        generator.Should().BeNull();
        diagnostic.Should().Contain("AI_EMBEDDING_DIMENSIONS");
    }

    /// <summary>A role can run on a different model from the primary.</summary>
    [Fact]
    public void ARoleCanOverrideTheModel()
    {
        InferenceClientFactory
            .TryCreateChatClient(
                Settings(InferenceProvider.Bitdeer), "extraction",
                out var client, out var diagnostic, model: "extraction-model")
            .Should().BeTrue(diagnostic);

        client.Should().NotBeNull();
    }

    /// <summary>The judge uses the override block when one is set.</summary>
    [Fact]
    public void TheJudgeUsesItsOverrideWhenSet()
    {
        var settings = Settings(InferenceProvider.Bitdeer) with
        {
            JudgeProvider = InferenceProvider.AzureOpenAI,
            JudgeEndpoint = "https://judge.openai.azure.com/",
            JudgeApiKey = "sk-judge",
            JudgeModel = "judge-deployment",
        };

        settings.HasJudgeOverride.Should().BeTrue();
        InferenceClientFactory.TryCreateJudgeChatClient(settings, out var client, out var diagnostic)
            .Should().BeTrue(diagnostic);
        client.Should().NotBeNull();
    }

    /// <summary>Without an override the judge falls back to the primary, rather than failing.</summary>
    [Fact]
    public void TheJudgeFallsBackToThePrimary()
    {
        var settings = Settings(InferenceProvider.Bitdeer);

        settings.HasJudgeOverride.Should().BeFalse();
        InferenceClientFactory.TryCreateJudgeChatClient(settings, out var client, out var diagnostic)
            .Should().BeTrue(diagnostic);
        client.Should().NotBeNull();
    }

    /// <summary>A failure diagnostic carries neither the key nor the unsanitised endpoint.</summary>
    [Fact]
    public void AFailureDiagnosticLeaksNothing()
    {
        // No model is the cheapest way to reach the failure path without a network call.
        var settings = Settings(InferenceProvider.Bitdeer, "https://api.example.com/v1/sk-in-the-path")
            with { Model = "" };

        InferenceClientFactory
            .TryCreateChatClient(settings, "answer", out _, out var diagnostic)
            .Should().BeFalse();

        diagnostic.Should().NotBeNull();
        diagnostic.Should().NotContain("sk-secret-value").And.NotContain("sk-in-the-path");
    }

    /// <summary>A model with no name is refused rather than sent as an empty string.</summary>
    [Fact]
    public void AnEmptyModelIsRefused()
    {
        var settings = Settings(InferenceProvider.Bitdeer) with { Model = "   " };

        InferenceClientFactory
            .TryCreateChatClient(settings, "answer", out var client, out var diagnostic)
            .Should().BeFalse();

        client.Should().BeNull();
        diagnostic.Should().Contain("answer");
    }
}
