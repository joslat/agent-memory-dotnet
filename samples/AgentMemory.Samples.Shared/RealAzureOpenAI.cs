using System.Diagnostics.CodeAnalysis;
using Azure;
using Azure.AI.OpenAI;

namespace AgentMemory.Samples.Shared;

/// <summary>
/// Shared live Azure OpenAI wiring for the AgentMemory samples. Every sample that talks to a model
/// uses this — there is no offline mock fallback. If credentials are missing, <see cref="TryCreate"/>
/// returns <see langword="false"/> and the caller should print <see cref="MissingCredentialsMessage"/>
/// and exit.
/// </summary>
public static class RealAzureOpenAI
{
    /// <summary>
    /// Resolves credentials and deployment names from the environment and creates an
    /// <see cref="AzureOpenAIClient"/>. Returns <see langword="false"/> (with a <see langword="null"/>
    /// client) when <c>AZURE_OPENAI_ENDPOINT</c> / <c>AZURE_OPENAI_API_KEY</c> are not set.
    /// </summary>
    public static bool TryCreate(
        [NotNullWhen(true)] out AzureOpenAIClient? client, out string chatDeployment, out string embeddingDeployment)
    {
        var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var apiKey   = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");

        chatDeployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT")
            ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_NAME")
            ?? "gpt-4o-mini";
        embeddingDeployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_EMBEDDING_DEPLOYMENT")
            ?? "text-embedding-ada-002";

        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey))
        {
            client = null;
            return false;
        }

        client = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
        return true;
    }

    /// <summary>
    /// Prints the missing-credentials instructions for <paramref name="sampleTitle"/> to
    /// <paramref name="writer"/> (default <see cref="Console.Out"/>). stdio-transport MCP hosts must
    /// pass <see cref="Console.Error"/> — stdout is reserved for the JSON-RPC stream.
    /// </summary>
    public static void PrintMissingCredentials(string sampleTitle, TextWriter? writer = null)
    {
        var w = writer ?? Console.Out;
        w.WriteLine($"=== {sampleTitle} ===\n");
        w.WriteLine("[!] Azure OpenAI is not configured. This sample calls a real model — there is no");
        w.WriteLine("    mock fallback. Set these and re-run:");
        w.WriteLine("      AZURE_OPENAI_ENDPOINT              (required, e.g. https://<resource>.openai.azure.com/)");
        w.WriteLine("      AZURE_OPENAI_API_KEY               (required)");
        w.WriteLine("      AZURE_OPENAI_DEPLOYMENT            (optional, default gpt-4o-mini)");
        w.WriteLine("      AZURE_OPENAI_EMBEDDING_DEPLOYMENT  (optional, default text-embedding-ada-002)");
    }
}
