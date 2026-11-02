#pragma warning disable AMEXT001 // The judge implements a preview Extensibility interface.
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AgentMemory.Cli.Commands;

/// <summary>
/// Embeddings from a local Ollama's native API (<c>/api/embed</c>): free and local: the trial runner's real embedder (feature 19), the same
/// bge-m3 the hosted provider serves.
/// </summary>
internal sealed class OllamaEmbeddings(string baseUrl, string model) : IEmbeddingGenerator<string, Embedding<float>>
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

/// <summary>
/// A local chat model as a reranker (feature 19): asked, for one question and one record, whether the record answers it; yes or no,
/// at temperature 0. Free and local; the trial reports its arm beside the decision and never decides with it.
/// </summary>
internal sealed class OllamaJudge(string baseUrl, string model) : AgentMemory.Extensibility.Neo4j.Storage.IPackMeaningJudge, IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(300) };

    public async Task<bool> AnswersAsync(string question, string text, CancellationToken cancellationToken = default)
    {
        var prompt = "You decide whether a note answers a question.\nQuestion: " + question + "\nNote: " + text
            + "\nDoes the note contain the answer to the question? Answer with one word: yes or no.";
        using var response = await _http.PostAsJsonAsync($"{baseUrl}/api/generate",
            new { model, prompt, stream = false, options = new { temperature = 0, num_predict = 3 } }, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return document.RootElement.GetProperty("response").GetString()?.Trim().StartsWith("yes", StringComparison.OrdinalIgnoreCase) == true;
    }

    public void Dispose() => _http.Dispose();
}
