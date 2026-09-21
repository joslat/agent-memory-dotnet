namespace AgentMemory.Inference;

/// <summary>
/// Embedding dimensions for models this repository knows, and nothing more.
/// </summary>
/// <remarks>
/// <para>
/// <b>This table exists so the resolver can fail closed, not so it can guess.</b> The dimension
/// becomes <c>Neo4jOptions.EmbeddingDimensions</c>, which defines the vector index: get it wrong and
/// nothing throws at configuration time — a store is built that cannot be searched correctly, and
/// the symptom appears much later as poor recall rather than as an error.
/// </para>
/// <para>
/// A model that is not here is not a failure of the operator; it is a fact this package does not
/// know. The answer is <c>AI_EMBEDDING_DIMENSIONS</c>, which wins over this table outright — so an
/// operator is never blocked by a missing row, only asked to state the number.
/// </para>
/// </remarks>
public static class KnownEmbeddingDimensions
{
    private static readonly Dictionary<string, int> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        // OpenAI / Azure OpenAI
        ["text-embedding-ada-002"] = 1536,
        ["text-embedding-3-small"] = 1536,
        ["text-embedding-3-large"] = 3072,

        // Bitdeer's default and the Qwen family it also serves
        ["BAAI/bge-m3"] = 1024,
        ["Qwen/Qwen3-Embedding-0.6B"] = 1024,
        ["Qwen/Qwen3-Embedding-4B"] = 2560,
        ["Qwen/Qwen3-Embedding-8B"] = 4096,

        // Common local models
        ["nomic-embed-text"] = 768,

        // NOT LISTED, DELIBERATELY: the Nemotron-3-Embed family. Its dimensions were not confirmed
        // against the host, and a plausible-looking number here would be worse than its absence --
        // the operator would never be asked, and the store would be built on a guess. It resolves
        // through AI_EMBEDDING_DIMENSIONS until someone verifies it.
    };

    /// <summary>Every model this table knows, for diagnostics.</summary>
    public static IReadOnlyCollection<string> KnownModels => Table.Keys;

    /// <summary>Looks up a model's dimension.</summary>
    /// <remarks>
    /// Matching is exact apart from case. A provider-qualified id (<c>BAAI/bge-m3</c>) is the key as
    /// the host serves it, so no prefix-stripping happens here: two hosts can serve different models
    /// under names that differ only by prefix, and being clever about that is how a wrong dimension
    /// gets chosen confidently.
    /// </remarks>
    public static bool TryGet(string? model, out int dimensions)
    {
        var trimmed = model?.Trim();
        if (!string.IsNullOrEmpty(trimmed)) return Table.TryGetValue(trimmed, out dimensions);

        dimensions = 0;
        return false;
    }
}
