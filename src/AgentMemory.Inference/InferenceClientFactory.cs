using System.ClientModel;
using System.Diagnostics.CodeAnalysis;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using OpenAI;

namespace AgentMemory.Inference;

/// <summary>
/// Builds chat and embedding clients from resolved settings — two protocol families, one entry point.
/// </summary>
/// <remarks>
/// <para>
/// <b>Azure protocol</b> (<c>azure</c>, <c>foundry</c>) goes through <see cref="AzureOpenAIClient"/>,
/// where the model argument is a DEPLOYMENT name. <b>OpenAI protocol</b> (<c>bitdeer</c>,
/// <c>openai</c>, <c>openai-compatible</c>) goes through <see cref="OpenAIClient"/> with an explicit
/// endpoint, where it is a model id. Everything downstream sees
/// <see cref="IChatClient"/>/<see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> and cannot tell
/// which family produced it — which is the point, and the reason the library needs no changes at all.
/// </para>
/// <para>
/// <b>Resolved per access, never cached.</b> A cached client outlives the settings it was built from,
/// and the failure that produces — a process still talking to the previous host after the operator
/// changed the variables — looks like anything except a caching bug.
/// </para>
/// <para>
/// <b>Returns diagnostics; never writes to the console.</b> This package is used by an MCP host whose
/// stdout is a JSON-RPC stream. Deciding where a message goes is the caller's business.
/// </para>
/// </remarks>
public static class InferenceClientFactory
{
    /// <summary>
    /// A timeout for calls that are NOT the subject under test — a judge, or a scoring pass.
    /// </summary>
    /// <remarks>
    /// Timing out a judge does not protect a measurement; it discards one that has already been paid
    /// for. The subject keeps the operator's configured timeout so a hung model still fails fast.
    /// </remarks>
    private static readonly TimeSpan GenerousTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Builds a chat client for one role.</summary>
    /// <param name="settings">Resolved settings.</param>
    /// <param name="purpose">The role, used in diagnostics and raw logging: answer, judge, extraction.</param>
    /// <param name="client">The client, when this returns true.</param>
    /// <param name="diagnostic">Why not, when this returns false. Never contains the key or a raw URI.</param>
    /// <param name="model">Override the model, for a role that uses a different one.</param>
    /// <param name="generousTimeout">True for calls that are not the subject under test.</param>
    public static bool TryCreateChatClient(
        InferenceProviderSettings settings,
        string purpose,
        [NotNullWhen(true)] out IChatClient? client,
        out string? diagnostic,
        string? model = null,
        bool generousTimeout = false)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var effectiveModel = string.IsNullOrWhiteSpace(model) ? settings.Model : model.Trim();
        if (string.IsNullOrWhiteSpace(effectiveModel))
        {
            client = null;
            diagnostic = $"No model is configured for '{purpose}'.";
            return false;
        }

        try
        {
            var timeout = generousTimeout
                ? GenerousTimeout
                : TimeSpan.FromSeconds(settings.NetworkTimeoutSeconds);

            IChatClient built = IsAzureProtocol(settings.Provider)
                ? AzureClient(settings.Endpoint, settings.ApiKey, timeout)
                    .GetChatClient(effectiveModel).AsIChatClient()
                : OpenAIProtocolClient(settings.Endpoint, settings.ApiKey, timeout)
                    .GetChatClient(effectiveModel).AsIChatClient();

            client = settings.ShowRaw
                ? new RawInferenceLoggingChatClient(built, purpose, settings.SafeEndpoint, Console.Error)
                : built;

            diagnostic = null;
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The message is the SDK's and may quote the endpoint, so it is not forwarded verbatim.
            client = null;
            diagnostic =
                $"Could not build the '{purpose}' client for "
                + $"{effectiveModel}@{InferenceProviderNames.ToToken(settings.Provider)} at "
                + $"{settings.SafeEndpoint}: {exception.GetType().Name}.";
            return false;
        }
    }

    /// <summary>
    /// Builds the judge's chat client: the judge override when one is set, else the primary.
    /// </summary>
    /// <remarks>
    /// The fallback lives here rather than in each harness program, because "the judge is the subject
    /// unless told otherwise" is a rule about the contract, and three copies of it would eventually
    /// be two rules.
    /// </remarks>
    public static bool TryCreateJudgeChatClient(
        InferenceProviderSettings settings,
        [NotNullWhen(true)] out IChatClient? client,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.HasJudgeOverride)
        {
            // A judge is never the subject under test, so it gets the generous timeout either way.
            return TryCreateChatClient(settings, "judge", out client, out diagnostic, generousTimeout: true);
        }

        var judgeSettings = settings with
        {
            Provider = settings.JudgeProvider,
            Endpoint = settings.JudgeEndpoint!,
            ApiKey = settings.JudgeApiKey!,
            Model = settings.JudgeModel!,
        };

        return TryCreateChatClient(judgeSettings, "judge", out client, out diagnostic, generousTimeout: true);
    }

    /// <summary>Builds the embedding generator.</summary>
    /// <param name="settings">Resolved settings.</param>
    /// <param name="generator">The generator, when this returns true.</param>
    /// <param name="diagnostic">Why not, when this returns false.</param>
    /// <param name="model">Override the embedding model.</param>
    public static bool TryCreateEmbeddingGenerator(
        InferenceProviderSettings settings,
        [NotNullWhen(true)] out IEmbeddingGenerator<string, Embedding<float>>? generator,
        out string? diagnostic,
        string? model = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var effectiveModel = string.IsNullOrWhiteSpace(model) ? settings.EmbeddingModel : model.Trim();
        if (string.IsNullOrWhiteSpace(effectiveModel))
        {
            generator = null;
            diagnostic = "No embedding model is configured.";
            return false;
        }

        // THE DIMENSION IS NOT OPTIONAL HERE EITHER. Building a generator whose dimension nobody
        // established is how a store gets a vector index that does not match what writes into it.
        if (settings.EmbeddingDimensions is not > 0)
        {
            generator = null;
            diagnostic =
                $"The embedding dimension for '{effectiveModel}' is unknown. Set AI_EMBEDDING_DIMENSIONS.";
            return false;
        }

        var endpoint = settings.EmbeddingEndpoint ?? settings.Endpoint;
        var apiKey = settings.EmbeddingApiKey ?? settings.ApiKey;
        var provider = settings.EmbeddingProvider == InferenceProvider.None
            ? settings.Provider
            : settings.EmbeddingProvider;

        try
        {
            var timeout = TimeSpan.FromSeconds(settings.NetworkTimeoutSeconds);

            generator = IsAzureProtocol(provider)
                ? AzureClient(endpoint, apiKey, timeout)
                    .GetEmbeddingClient(effectiveModel).AsIEmbeddingGenerator()
                : OpenAIProtocolClient(endpoint, apiKey, timeout)
                    .GetEmbeddingClient(effectiveModel).AsIEmbeddingGenerator();

            diagnostic = null;
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            generator = null;
            diagnostic =
                $"Could not build the embedding client for "
                + $"{effectiveModel}@{InferenceProviderNames.ToToken(provider)} at "
                + $"{InferenceEndpoints.Sanitize(endpoint)}: {exception.GetType().Name}.";
            return false;
        }
    }

    /// <summary>Which protocol family a provider speaks.</summary>
    /// <remarks>
    /// Public because the McpHost README and the operator docs state it, and a statement about
    /// behaviour that is derived rather than restated cannot drift from the behaviour.
    /// </remarks>
    public static bool IsAzureProtocol(InferenceProvider provider) =>
        provider is InferenceProvider.AzureOpenAI or InferenceProvider.Foundry;

    private static AzureOpenAIClient AzureClient(string endpoint, string apiKey, TimeSpan timeout) =>
        new(new Uri(endpoint), new AzureKeyCredential(apiKey),
            new AzureOpenAIClientOptions { NetworkTimeout = timeout });

    private static OpenAIClient OpenAIProtocolClient(string endpoint, string apiKey, TimeSpan timeout) =>
        new(new ApiKeyCredential(apiKey),
            new OpenAIClientOptions { Endpoint = new Uri(endpoint), NetworkTimeout = timeout });
}
