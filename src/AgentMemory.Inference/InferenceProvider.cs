namespace AgentMemory.Inference;

/// <summary>The inference host a chat or embedding client talks to.</summary>
/// <remarks>
/// <para>
/// Two protocol families sit under these five: the Azure protocol (<see cref="AzureOpenAI"/>,
/// <see cref="Foundry"/>) and the OpenAI protocol (<see cref="Bitdeer"/>, <see cref="OpenAI"/>,
/// <see cref="OpenAICompatible"/>). The distinction is what the client factory switches on; the
/// names here are what an operator types.
/// </para>
/// <para>
/// <see cref="AzureOpenAI"/> is first in the auto-detect order and is pinned there by a test. The
/// Azure deployments this repository used are deleted and will not work, but a machine that still
/// has only <c>AZURE_OPENAI_*</c> set must behave exactly as it did before this package existed.
/// </para>
/// </remarks>
public enum InferenceProvider
{
    /// <summary>Nothing configured. Never a silent fallback — always carries a diagnostic.</summary>
    None = 0,

    /// <summary>Azure OpenAI. Azure protocol, deployment names rather than model ids.</summary>
    AzureOpenAI,

    /// <summary>Bitdeer's inference host. OpenAI protocol.</summary>
    Bitdeer,

    /// <summary>OpenAI proper. OpenAI protocol.</summary>
    OpenAI,

    /// <summary>Azure AI Foundry. Azure protocol.</summary>
    Foundry,

    /// <summary>Any other OpenAI-compatible host: Ollama, LM Studio, vLLM, a gateway.</summary>
    OpenAICompatible,

    /// <summary>
    /// A local Ollama server: http://127.0.0.1:11434/v1, no key, <c>bge-m3</c> for embeddings by default.
    /// </summary>
    Ollama,
}

/// <summary>How the provider was chosen.</summary>
/// <remarks>
/// Printed in the banner, because "which host answered" and "why that host" are different questions
/// and an operator debugging a surprising result needs the second one. A run that auto-detected
/// Azure because a stale variable was still set looks identical to one that chose it.
/// </remarks>
public enum InferenceProviderSelection
{
    /// <summary>Nothing selected and nothing detected.</summary>
    None = 0,

    /// <summary><c>AI_INFERENCE_PROVIDER</c> named it.</summary>
    Explicit,

    /// <summary>The selector was unset; the first provider with complete credentials won.</summary>
    AutoDetected,
}
