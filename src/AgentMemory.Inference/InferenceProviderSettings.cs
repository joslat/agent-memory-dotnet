namespace AgentMemory.Inference;

/// <summary>
/// Everything needed to build chat and embedding clients, with no SDK type in sight.
/// </summary>
/// <remarks>
/// <para>
/// <b>SDK-free on purpose.</b> This is the value that crosses from "read the environment" to "build
/// a client", and keeping it free of <c>Azure.AI.OpenAI</c> and <c>OpenAI</c> types is what lets the
/// resolver be tested without a network, a key, or a decision about which protocol family wins.
/// </para>
/// <para>
/// <b>It holds an API key, so it does not get the compiler's <c>ToString</c>.</b> A record prints
/// every property it has; this one would print the key into any log line, exception message or
/// debugger watch that touched it. <see cref="ToString"/> is overridden to redact, and
/// <see cref="Endpoint"/> is never printed raw — only <see cref="SafeEndpoint"/>, which is
/// scheme, host and port and nothing else.
/// </para>
/// </remarks>
public sealed record InferenceProviderSettings
{
    /// <summary>The resolved chat provider.</summary>
    public required InferenceProvider Provider { get; init; }

    /// <summary>The chat endpoint. Print <see cref="SafeEndpoint"/>, never this.</summary>
    public required string Endpoint { get; init; }

    /// <summary>The chat API key. Never printed, never logged, never put in a diagnostic.</summary>
    public required string ApiKey { get; init; }

    /// <summary>The primary chat model, or for Azure the deployment name.</summary>
    public required string Model { get; init; }

    /// <summary>The second comparison slot, kept for AgentEval contract parity.</summary>
    public string? Model2 { get; init; }

    /// <summary>The third comparison slot, kept for AgentEval contract parity.</summary>
    public string? Model3 { get; init; }

    /// <summary>
    /// The extraction model, when the operator named one.
    /// </summary>
    /// <remarks>Read through <see cref="EffectiveExtractionModel"/>, which falls back correctly.</remarks>
    public string? ExtractionModel { get; init; }

    /// <summary>The judge provider, when the judge override block is complete.</summary>
    public InferenceProvider JudgeProvider { get; init; } = InferenceProvider.None;

    /// <summary>The judge endpoint, when the judge override block is complete.</summary>
    public string? JudgeEndpoint { get; init; }

    /// <summary>The judge API key, when the judge override block is complete.</summary>
    public string? JudgeApiKey { get; init; }

    /// <summary>The judge model, when the judge override block is complete.</summary>
    public string? JudgeModel { get; init; }

    /// <summary>The embedding provider, which may differ from <see cref="Provider"/>.</summary>
    public InferenceProvider EmbeddingProvider { get; init; } = InferenceProvider.None;

    /// <summary>The embedding endpoint.</summary>
    public string? EmbeddingEndpoint { get; init; }

    /// <summary>The embedding API key.</summary>
    public string? EmbeddingApiKey { get; init; }

    /// <summary>The embedding model, or for Azure the embedding deployment name.</summary>
    public string? EmbeddingModel { get; init; }

    /// <summary>
    /// The embedding dimension. Null only when embeddings are not configured at all.
    /// </summary>
    /// <remarks>
    /// <b>Store-defining, which is why the resolver fails closed rather than guessing it.</b> It
    /// becomes <c>Neo4jOptions.EmbeddingDimensions</c> and is validated against the live vector
    /// index: a wrong number does not throw at configuration time, it builds a wrong index.
    /// </remarks>
    public int? EmbeddingDimensions { get; init; }

    /// <summary>Per-attempt network timeout for the subject under test, in seconds.</summary>
    public int NetworkTimeoutSeconds { get; init; } = 180;

    /// <summary>Whether to print every request and reply with key and URI scrubbed.</summary>
    public bool ShowRaw { get; init; }

    /// <summary>The extraction model actually used: the named one, else the primary.</summary>
    public string EffectiveExtractionModel =>
        string.IsNullOrWhiteSpace(ExtractionModel) ? Model : ExtractionModel;

    /// <summary>Whether a judge override is in force.</summary>
    public bool HasJudgeOverride =>
        JudgeProvider != InferenceProvider.None
        && !string.IsNullOrWhiteSpace(JudgeEndpoint)
        && !string.IsNullOrWhiteSpace(JudgeApiKey)
        && !string.IsNullOrWhiteSpace(JudgeModel);

    /// <summary>Whether embeddings are configured.</summary>
    public bool HasEmbeddings =>
        EmbeddingProvider != InferenceProvider.None
        && !string.IsNullOrWhiteSpace(EmbeddingModel)
        && EmbeddingDimensions is > 0;

    /// <summary>
    /// The run identity for a measured number: <c>model@provider</c>.
    /// </summary>
    /// <remarks>
    /// The same model id on two hosts is not the same measurement — different weights quantisation,
    /// different serving stack, different sampling defaults. A number stamped only with a model name
    /// cannot be told apart from one produced somewhere else.
    /// </remarks>
    public string ModelIdentity => $"{Model}@{InferenceProviderNames.ToToken(Provider)}";

    /// <summary>
    /// The embedding identity: <c>model@provider/dims</c>, or null when embeddings are unconfigured.
    /// </summary>
    /// <remarks>
    /// The dimension is part of the identity because it is part of the STORE. Two runs against the
    /// same model at different dimensions did not read the same index.
    /// </remarks>
    public string? EmbeddingIdentity =>
        HasEmbeddings
            ? $"{EmbeddingModel}@{InferenceProviderNames.ToToken(EmbeddingProvider)}/{EmbeddingDimensions}"
            : null;

    /// <summary>The chat endpoint reduced to scheme, host and port.</summary>
    public string SafeEndpoint => InferenceEndpoints.Sanitize(Endpoint);

    /// <summary>The embedding endpoint reduced to scheme, host and port.</summary>
    public string? SafeEmbeddingEndpoint =>
        string.IsNullOrWhiteSpace(EmbeddingEndpoint) ? null : InferenceEndpoints.Sanitize(EmbeddingEndpoint);

    /// <summary>A one-line description for a startup banner.</summary>
    public string DisplayName =>
        EmbeddingIdentity is { } embedding
            ? $"{ModelIdentity} + {embedding} at {SafeEndpoint}"
            : $"{ModelIdentity} at {SafeEndpoint} (no embeddings)";

    /// <summary>Redacted. See the remarks on the type.</summary>
    public override string ToString() =>
        $"InferenceProviderSettings {{ Provider = {InferenceProviderNames.ToToken(Provider)}, "
        + $"Endpoint = {SafeEndpoint}, ApiKey = ***, Model = {Model}, "
        + $"Embedding = {EmbeddingIdentity ?? "(none)"} }}";
}
