using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace AgentMemory.McpHost.Coding.Client;

/// <summary>Bounded project HTTP requests shared by native hook contracts.</summary>
internal sealed class CodingHookHttp(CodingHookOptions options, HttpClient client)
{
    public async Task<JsonElement> PostAsync(string operation, object body, HttpStatusCode expected, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/projects/{options.Project}/{operation}")
            { Content = JsonContent.Create(body, options: CodingApi.Json) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != expected) throw new HttpRequestException("Memory request was not accepted.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var bytes = new byte[CodingBodyLimit.MaximumBytes + 1];
        var count = await stream.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        if (count > CodingBodyLimit.MaximumBytes) throw new JsonException("Memory response exceeds the hook limit.");
        using var document = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 16 });
        return document.RootElement.Clone();
    }
}
