using System.Diagnostics.CodeAnalysis;
using AgentMemory.Inference;
using Microsoft.Extensions.AI;

namespace AgentMemory.Samples.Shared;

/// <summary>
/// Live model wiring for the samples, on whichever provider the operator configured.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no offline mock fallback, and that is deliberate.</b> A sample that quietly degrades
/// to a stub teaches the reader that the library works when it has not been shown to. When no
/// provider is configured, <see cref="TryCreate"/> returns false and the caller prints
/// <see cref="PrintMissingProvider"/> and exits.
/// </para>
/// <para>
/// <b>Set <c>Neo4jOptions.EmbeddingDimensions</c> from <see cref="InferenceProviderSettings.EmbeddingDimensions"/>.</b>
/// Before the provider layer every sample used Azure's <c>text-embedding-ada-002</c> at 1536, which
/// is also the Neo4j default, so nobody had to. On Bitdeer the default embedding model is 1024-wide:
/// leave the store at 1536 and the index does not match what writes into it, silently.
/// </para>
/// </remarks>
public static class RealModel
{
    /// <summary>
    /// Resolves the configured provider and builds both clients.
    /// </summary>
    /// <param name="chat">The chat client.</param>
    /// <param name="embeddings">The embedding generator.</param>
    /// <param name="settings">The resolved settings — the banner and the store dimension come from here.</param>
    public static bool TryCreate(
        [NotNullWhen(true)] out IChatClient? chat,
        [NotNullWhen(true)] out IEmbeddingGenerator<string, Embedding<float>>? embeddings,
        [NotNullWhen(true)] out InferenceProviderSettings? settings)
    {
        chat = null;
        embeddings = null;
        settings = null;

        var resolution = InferenceProviderEnvironment.Resolve();
        if (resolution.Settings is not { } resolved) return false;

        if (!InferenceClientFactory.TryCreateChatClient(resolved, "sample", out var chatClient, out _)
            || !InferenceClientFactory.TryCreateEmbeddingGenerator(resolved, out var embeddingClient, out _))
        {
            return false;
        }

        chat = chatClient;
        embeddings = embeddingClient;
        settings = resolved;
        return true;
    }

    /// <summary>
    /// Prints why no model is available, and exactly what to set.
    /// </summary>
    /// <remarks>
    /// <b>stdio-transport MCP hosts must pass <see cref="Console.Error"/></b> — stdout carries the
    /// JSON-RPC stream, and a friendly message there corrupts the protocol rather than helping.
    /// <para>
    /// The reason is re-resolved rather than cached from <see cref="TryCreate"/>: resolution reads
    /// the environment and nothing else, so asking twice gives the same answer, and a stashed string
    /// is one more thing that can be stale.
    /// </para>
    /// </remarks>
    public static void PrintMissingProvider(string sampleTitle, TextWriter? writer = null)
    {
        var w = writer ?? Console.Out;
        var resolution = InferenceProviderEnvironment.Resolve();

        w.WriteLine($"=== {sampleTitle} ===\n");
        w.WriteLine("[!] No inference provider is configured. This sample calls a real model — there is");
        w.WriteLine("    no mock fallback.\n");
        w.WriteLine($"    {resolution.Diagnostic}");

        if (resolution.EmbeddingDiagnostic is { } embedding)
        {
            w.WriteLine($"    {embedding}");
        }

        w.WriteLine();
        w.WriteLine("    The short version — pick one:");
        w.WriteLine("      BITDEER_API_KEY=<key>                      (chat + embeddings, nothing else needed)");
        w.WriteLine("      OPENAI_API_KEY=<key>");
        w.WriteLine("      AZURE_OPENAI_ENDPOINT + AZURE_OPENAI_API_KEY + AZURE_OPENAI_DEPLOYMENT");
        w.WriteLine("      OPENAI_COMPATIBLE_ENDPOINT + OPENAI_COMPATIBLE_MODEL   (Ollama, LM Studio, vLLM)");
        w.WriteLine();
        w.WriteLine($"    Choose explicitly with {InferenceProviderEnvironment.SelectorVariable}="
                    + $"{string.Join("|", InferenceProviderNames.AllTokens)}.");
        w.WriteLine("    Full contract: docs/configuration/inference-providers.md");
    }

    /// <summary>Prints the one-line "what am I talking to" banner.</summary>
    /// <remarks>
    /// Worth a line of output in every sample: the commonest confusion when a provider layer exists
    /// is not knowing which host answered, and the identity here is the same
    /// <c>model@provider</c> string that stamps a measured number.
    /// </remarks>
    public static void PrintModelBanner(InferenceProviderSettings settings, TextWriter? writer = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        (writer ?? Console.Out).WriteLine($"[model] {settings.Summary}");
    }
}
