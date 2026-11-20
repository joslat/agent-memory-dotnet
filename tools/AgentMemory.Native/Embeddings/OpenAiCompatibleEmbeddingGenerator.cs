using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace AgentMemory.Native.Embeddings;

/// <summary>Where query text becomes a vector: an OpenAI-compatible <c>/v1/embeddings</c> endpoint.</summary>
internal sealed record EmbeddingEndpoint(Uri Url, string Model, string? ApiKey, TimeSpan Timeout);

/// <summary>The endpoint answered, but not with what the store needs (count or dimensions).</summary>
internal sealed class EmbeddingMismatchException(string message) : Exception(message);

/// <summary>
/// A minimal client for an OpenAI-compatible embeddings endpoint (Ollama, llama.cpp, vLLM, OpenAI itself) with
/// generated JSON contracts: no SDK, no reflection, so it publishes cleanly with Native AOT. Every vector must have the
/// store's dimensions; anything else is refused rather than searched with.
/// </summary>
internal sealed class OpenAiCompatibleEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly EmbeddingEndpoint _endpoint;
    private readonly int _dimensions;
    private readonly HttpClient _http;

    public OpenAiCompatibleEmbeddingGenerator(EmbeddingEndpoint endpoint, int dimensions)
    {
        _endpoint = endpoint;
        _dimensions = dimensions;
        _http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = endpoint.Timeout,
        };
        if (endpoint.ApiKey is not null)
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
    }

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var inputs = values.ToArray();
        var body = JsonSerializer.SerializeToUtf8Bytes(
            new EmbeddingsRequest(_endpoint.Model, inputs, "float"), EmbeddingJsonContext.Default.EmbeddingsRequest);
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await _http.PostAsync(_endpoint.Url, content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"The embedding endpoint answered {(int)response.StatusCode} {response.ReasonPhrase}.", null, response.StatusCode);

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var parsed = await JsonSerializer.DeserializeAsync(stream, EmbeddingJsonContext.Default.EmbeddingsResponse, cancellationToken)
                .ConfigureAwait(false);
            var data = parsed?.Data ?? [];
            if (data.Count != inputs.Length)
                throw new EmbeddingMismatchException($"The embedding endpoint returned {data.Count} vectors for {inputs.Length} inputs.");

            var result = new GeneratedEmbeddings<Embedding<float>>();
            foreach (var item in data.OrderBy(d => d.Index))
            {
                var vector = item.Embedding ?? [];
                if (vector.Length != _dimensions)
                    throw new EmbeddingMismatchException(
                        $"The embedding model '{_endpoint.Model}' returned {vector.Length} dimensions; the store's vectors have {_dimensions}.");
                result.Add(new Embedding<float>(vector));
            }
            return result;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() => _http.Dispose();
}

internal sealed record EmbeddingsRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("input")] string[] Input,
    [property: JsonPropertyName("encoding_format")] string EncodingFormat);

internal sealed class EmbeddingsResponse
{
    [JsonPropertyName("data")]
    public List<EmbeddingsResponseItem>? Data { get; set; }
}

internal sealed class EmbeddingsResponseItem
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("embedding")]
    public float[]? Embedding { get; set; }
}

[JsonSerializable(typeof(EmbeddingsRequest))]
[JsonSerializable(typeof(EmbeddingsResponse))]
internal sealed partial class EmbeddingJsonContext : JsonSerializerContext
{
}
