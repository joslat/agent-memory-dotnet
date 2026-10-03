using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AgentMemory.RouterArena.Record;

/// <summary>
/// Embeddings from a local Ollama's native API (<c>/api/embed</c>): the recorder's only model, free and local, the same
/// bge-m3 the hosted provider serves.
/// </summary>
public sealed class OllamaEmbeddingGenerator(string baseUrl, string model) : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(120) };

    /// <summary>Known after the first call; reported in the metadata.</summary>
    public int? Dimensions { get; set; }

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync($"{baseUrl}/api/embed", new { model, input = values.ToList() }, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (body.ConfigureAwait(false))
        {
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new GeneratedEmbeddings<Embedding<float>>([.. document.RootElement.GetProperty("embeddings").EnumerateArray()
                .Select(vector => new Embedding<float>(vector.EnumerateArray().Select(x => x.GetSingle()).ToArray()))]);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is not null ? null
        : serviceType == typeof(EmbeddingGeneratorMetadata) ? new EmbeddingGeneratorMetadata("ollama", new Uri(baseUrl), model, Dimensions)
        : serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() => _http.Dispose();
}
