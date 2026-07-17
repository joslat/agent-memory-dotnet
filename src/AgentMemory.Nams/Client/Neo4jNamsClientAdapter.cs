using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Nams.Domain;

namespace AgentMemory.Nams.Client;

/// <summary>
/// <see cref="INamsClient"/> implementation calling the NAMS REST API directly (no dependency on the
/// <c>Neo4j.AgentMemory</c> TCK C# client -- see the Phase 2 planning doc §2 for why). Request paths are built
/// relative to <see cref="NamsOptions.Endpoint"/> without a leading <c>/</c>: <see cref="NamsClientFactory"/>
/// normalizes the endpoint to end with a trailing <c>/</c>, and a leading <c>/</c> on the relative path would
/// make it host-root-relative per <see cref="Uri"/> combination rules, silently dropping any path segment
/// (e.g. <c>/v1</c>) the configured endpoint carries.
/// </summary>
internal sealed class Neo4jNamsClientAdapter : INamsClient
{
    // Every Domain record already carries an explicit [JsonPropertyName] matching the pinned OpenAPI snapshot,
    // so this isn't load-bearing today -- it's defense-in-depth against a future field added without one.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly NamsRetryPolicy _retryPolicy;
    private readonly string? _apiKeyForRedaction;

    public Neo4jNamsClientAdapter(HttpClient httpClient, IOptions<NamsOptions> options, ILogger<Neo4jNamsClientAdapter> logger)
    {
        _httpClient = httpClient;
        var namsOptions = options.Value;
        _retryPolicy = new NamsRetryPolicy(namsOptions.MaxRetryAttempts, namsOptions.InitialRetryDelay, logger);
        _apiKeyForRedaction = namsOptions.ApiKey;
    }

    public Task<NamsConversation> CreateConversationAsync(
        string? userId, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken) =>
        InvokeAsync(
            () => BuildJsonRequest(HttpMethod.Post, "conversations", new CreateConversationRequestBody(userId, metadata)),
            isIdempotent: false,
            DeserializeAsync<NamsConversation>,
            cancellationToken);

    public Task<NamsContext> GetContextAsync(string conversationId, CancellationToken cancellationToken) =>
        InvokeAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"conversations/{Uri.EscapeDataString(conversationId)}/context"),
            isIdempotent: true,
            DeserializeAsync<NamsContext>,
            cancellationToken);

    public async Task<IReadOnlyList<NamsMessage>> AddMessagesAsync(
        string conversationId, IReadOnlyList<NamsMessageInput> messages, CancellationToken cancellationToken)
    {
        var path = $"conversations/{Uri.EscapeDataString(conversationId)}/messages/bulk";
        var response = await InvokeAsync(
            () => BuildJsonRequest(HttpMethod.Post, path, new AddMessagesBulkRequestBody(messages)),
            isIdempotent: false,
            DeserializeAsync<AddMessagesBatchResponseBody>,
            cancellationToken).ConfigureAwait(false);
        return response.Messages;
    }

    public async Task<IReadOnlyList<NamsEntity>> SearchEntitiesAsync(
        string query, string? type, int limit, CancellationToken cancellationToken)
    {
        // A POST verb, but a pure read-only query with no server-side side effects -- safe to treat as
        // idempotent for retry purposes (matches the engineering plan's retry matrix, which lists "Search" as
        // always-retryable regardless of the verb it happens to use).
        var response = await InvokeAsync(
            () => BuildJsonRequest(HttpMethod.Post, "entities/search", new SearchEntitiesRequestBody(query, type, limit)),
            isIdempotent: true,
            DeserializeAsync<SearchEntitiesResponseBody>,
            cancellationToken).ConfigureAwait(false);
        return response.Entities;
    }

    private async Task<T> InvokeAsync<T>(
        Func<HttpRequestMessage> requestFactory,
        bool isIdempotent,
        Func<Stream, CancellationToken, Task<T>> deserialize,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _retryPolicy.ExecuteAsync(requestFactory, _httpClient, isIdempotent, cancellationToken)
                .ConfigureAwait(false);
            var result = await NamsClientExceptionMapper.MapResponseAsync(response, deserialize, _apiKeyForRedaction, cancellationToken)
                .ConfigureAwait(false);
            return result.IsSuccess ? result.Value! : throw NamsClientExceptionMapper.ToException(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // caller cancellation -- never wrap
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw NamsClientExceptionMapper.FromTransportException(ex, _apiKeyForRedaction);
        }
    }

    private static HttpRequestMessage BuildJsonRequest<TBody>(HttpMethod method, string relativePath, TBody body) =>
        new(method, relativePath) { Content = JsonContent.Create(body, options: JsonOptions) };

    private static async Task<T> DeserializeAsync<T>(Stream stream, CancellationToken cancellationToken) =>
        await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException($"NAMS response deserialized to null for {typeof(T).Name}.");

    // ---- Wire-only request/response envelopes (not part of the public interface's domain shapes) ----

    private sealed record CreateConversationRequestBody(
        [property: JsonPropertyName("userId")] string? UserId,
        [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, string>? Metadata);

    private sealed record AddMessagesBulkRequestBody(
        [property: JsonPropertyName("messages")] IReadOnlyList<NamsMessageInput> Messages);

    private sealed record AddMessagesBatchResponseBody(
        [property: JsonPropertyName("messages")] IReadOnlyList<NamsMessage> Messages);

    private sealed record SearchEntitiesRequestBody(
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("limit")] int Limit);

    private sealed record SearchEntitiesResponseBody(
        [property: JsonPropertyName("entities")] IReadOnlyList<NamsEntity> Entities,
        [property: JsonPropertyName("searchType")] string? SearchType);
}
