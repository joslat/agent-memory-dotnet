using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Nams.Domain;
using AgentMemory.Nams.Observability;

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
    private readonly NamsMetrics _metrics;

    public Neo4jNamsClientAdapter(
        HttpClient httpClient, IOptions<NamsOptions> options, ILogger<Neo4jNamsClientAdapter> logger, NamsMetrics metrics)
    {
        _httpClient = httpClient;
        var namsOptions = options.Value;
        _retryPolicy = new NamsRetryPolicy(namsOptions.MaxRetryAttempts, namsOptions.InitialRetryDelay, logger);
        _apiKeyForRedaction = namsOptions.ApiKey;
        _metrics = metrics;
    }

    public Task<NamsConversation> CreateConversationAsync(
        string? userId, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken) =>
        InvokeAsync(
            "resolve_conversation",
            () => BuildJsonRequest(HttpMethod.Post, "conversations", new CreateConversationRequestBody(userId, metadata)),
            isIdempotent: false,
            DeserializeAsync<NamsConversation>,
            cancellationToken);

    public Task<NamsContext> GetContextAsync(string conversationId, CancellationToken cancellationToken) =>
        InvokeAsync(
            "get_context",
            () => new HttpRequestMessage(HttpMethod.Get, $"conversations/{Uri.EscapeDataString(conversationId)}/context"),
            isIdempotent: true,
            DeserializeAsync<NamsContext>,
            cancellationToken);

    public async Task<IReadOnlyList<NamsMessage>> AddMessagesAsync(
        string conversationId, IReadOnlyList<NamsMessageInput> messages, CancellationToken cancellationToken)
    {
        var path = $"conversations/{Uri.EscapeDataString(conversationId)}/messages/bulk";
        var response = await InvokeAsync(
            "store_turn",
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
            "search_entities",
            () => BuildJsonRequest(HttpMethod.Post, "entities/search", new SearchEntitiesRequestBody(query, type, limit)),
            isIdempotent: true,
            DeserializeAsync<SearchEntitiesResponseBody>,
            cancellationToken).ConfigureAwait(false);
        return response.Entities;
    }

    public async Task<IReadOnlyList<NamsEntity>> ListEntitiesAsync(int limit, CancellationToken cancellationToken)
    {
        var response = await InvokeAsync(
            "list_entities",
            () => new HttpRequestMessage(HttpMethod.Get, $"entities?limit={limit}"),
            isIdempotent: true,
            DeserializeAsync<ListEntitiesResponseBody>,
            cancellationToken).ConfigureAwait(false);
        return response.Entities;
    }

    public async Task<IReadOnlyList<NamsMessage>> SearchMessagesAsync(
        string conversationId, string query, int limit, CancellationToken cancellationToken)
    {
        // A POST verb, but a pure read-only query with no server-side side effects -- same idempotent-for-
        // retry-purposes treatment as SearchEntitiesAsync above.
        var path = $"conversations/{Uri.EscapeDataString(conversationId)}/search";
        var response = await InvokeAsync(
            "search_messages",
            () => BuildJsonRequest(HttpMethod.Post, path, new SearchMessagesRequestBody(query, limit)),
            isIdempotent: true,
            DeserializeAsync<SearchMessagesResponseBody>,
            cancellationToken).ConfigureAwait(false);
        return response.Messages;
    }

    public Task DeleteConversationAsync(string conversationId, CancellationToken cancellationToken) =>
        InvokeAsync(
            "delete_conversation",
            () => new HttpRequestMessage(HttpMethod.Delete, $"conversations/{Uri.EscapeDataString(conversationId)}"),
            // Confirmed live: deleting an already-deleted (or otherwise nonexistent) conversation still
            // returns 200, not 404 -- genuinely idempotent, safe to retry like any other read.
            isIdempotent: true,
            DeserializeAsync<DeleteConversationResponseBody>,
            cancellationToken);

    public async Task<IReadOnlyList<NamsConversationSummary>> ListConversationsAsync(
        int limit, CancellationToken cancellationToken)
    {
        var response = await InvokeAsync(
            "list_conversations",
            () => new HttpRequestMessage(HttpMethod.Get, $"conversations?limit={limit}"),
            isIdempotent: true,
            DeserializeAsync<ListConversationsResponseBody>,
            cancellationToken).ConfigureAwait(false);
        return response.Conversations;
    }

    public async Task<IReadOnlyList<NamsObservation>> GetObservationsAsync(
        string conversationId, int limit, CancellationToken cancellationToken)
    {
        var path = $"conversations/{Uri.EscapeDataString(conversationId)}/observations?limit={limit}";
        var response = await InvokeAsync(
            "get_observations",
            () => new HttpRequestMessage(HttpMethod.Get, path),
            isIdempotent: true,
            DeserializeAsync<GetObservationsResponseBody>,
            cancellationToken).ConfigureAwait(false);
        return response.Observations;
    }

    public Task<NamsEntityFeedbackResult> SetEntityFeedbackAsync(
        string entityId, double? userScore, bool? confirmed, CancellationToken cancellationToken) =>
        InvokeAsync(
            "set_entity_feedback",
            () => BuildJsonRequest(
                HttpMethod.Put, $"entities/{Uri.EscapeDataString(entityId)}/feedback",
                new EntityFeedbackRequestBody(userScore, confirmed)),
            // A PUT full-value replacement, not a POST create -- resending the same body produces the same end
            // state, none of the duplicate-row risk NamsRetryPolicy's writes-don't-retry default guards
            // against. Genuinely idempotent, safe to retry like DeleteConversationAsync above.
            isIdempotent: true,
            DeserializeAsync<NamsEntityFeedbackResult>,
            cancellationToken);

    public Task<NamsEntityGraph> GetEntityGraphAsync(CancellationToken cancellationToken) =>
        InvokeAsync(
            "get_entity_graph",
            () => new HttpRequestMessage(HttpMethod.Get, "entities/graph"),
            isIdempotent: true,
            DeserializeAsync<NamsEntityGraph>,
            cancellationToken);

    public Task<NamsGraphExpansion> ExpandGraphAsync(
        string nodeId, IReadOnlyList<string> loadedIds, CancellationToken cancellationToken) =>
        InvokeAsync(
            "expand_graph",
            // POST verb, but read-only (no server-side side effects) -- same idempotent-for-retry treatment
            // as SearchEntitiesAsync/SearchMessagesAsync.
            () => BuildJsonRequest(HttpMethod.Post, "graph/expand", new ExpandGraphRequestBody(nodeId, loadedIds)),
            isIdempotent: true,
            DeserializeAsync<NamsGraphExpansion>,
            cancellationToken);

    private async Task<T> InvokeAsync<T>(
        string operationName,
        Func<HttpRequestMessage> requestFactory,
        bool isIdempotent,
        Func<Stream, CancellationToken, Task<T>> deserialize,
        CancellationToken cancellationToken)
    {
        using var activity = NamsActivitySource.Instance.StartActivity($"agentmemory.nams.{operationName}");
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await _retryPolicy.ExecuteAsync(
                requestFactory, _httpClient, isIdempotent, cancellationToken,
                onRetry: () => _metrics.BackendRetries.Add(1, NamsMetricTags.Operation(operationName)))
                .ConfigureAwait(false);
            var result = await NamsClientExceptionMapper.MapResponseAsync(response, deserialize, _apiKeyForRedaction, cancellationToken)
                .ConfigureAwait(false);
            if (result.IsSuccess)
            {
                RecordOutcome(operationName, stopwatch.Elapsed, activity, status: "succeeded");
                return result.Value!;
            }

            var failure = NamsClientExceptionMapper.ToException(result);
            RecordFailure(operationName, stopwatch.Elapsed, activity, failure.FailureKind);
            throw failure;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation is not counted as a service failure (Phase 9's own test list).
            RecordOutcome(operationName, stopwatch.Elapsed, activity, status: "cancelled");
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            var failure = NamsClientExceptionMapper.FromTransportException(ex, _apiKeyForRedaction);
            RecordFailure(operationName, stopwatch.Elapsed, activity, failure.FailureKind);
            throw failure;
        }
    }

    private void RecordOutcome(string operationName, TimeSpan elapsed, Activity? activity, string status)
    {
        _metrics.BackendOperations.Add(1, NamsMetricTags.OperationStatus(operationName, status));
        _metrics.BackendDurationMs.Record(elapsed.TotalMilliseconds, NamsMetricTags.Operation(operationName));
        activity?.SetStatus(status == "succeeded" || status == "cancelled" ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
    }

    private void RecordFailure(string operationName, TimeSpan elapsed, Activity? activity, NamsFailureKind failureKind)
    {
        RecordOutcome(operationName, elapsed, activity, status: "failed");
        _metrics.BackendFailures.Add(1, NamsMetricTags.OperationFailureKind(operationName, failureKind));
        if (failureKind == NamsFailureKind.RateLimited)
            _metrics.BackendRateLimited.Add(1, NamsMetricTags.Operation(operationName));
        activity?.SetStatus(ActivityStatusCode.Error, failureKind.ToString());
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

    private sealed record ListEntitiesResponseBody(
        [property: JsonPropertyName("entities")] IReadOnlyList<NamsEntity> Entities);

    private sealed record SearchMessagesRequestBody(
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("limit")] int Limit);

    private sealed record SearchMessagesResponseBody(
        [property: JsonPropertyName("messages")] IReadOnlyList<NamsMessage> Messages,
        [property: JsonPropertyName("searchType")] string? SearchType);

    // Confirmed live: the real value is "deleted", not the "success" the plan's own OpenAPI guess assumed.
    // Not exposed publicly -- callers only care whether DeleteConversationAsync threw.
    private sealed record DeleteConversationResponseBody(
        [property: JsonPropertyName("status")] string? Status);

    private sealed record ListConversationsResponseBody(
        [property: JsonPropertyName("conversations")] IReadOnlyList<NamsConversationSummary> Conversations);

    private sealed record GetObservationsResponseBody(
        [property: JsonPropertyName("observations")] IReadOnlyList<NamsObservation> Observations);

    private sealed record EntityFeedbackRequestBody(
        [property: JsonPropertyName("userScore")] double? UserScore,
        [property: JsonPropertyName("confirmed")] bool? Confirmed);

    private sealed record ExpandGraphRequestBody(
        [property: JsonPropertyName("nodeId")] string NodeId,
        [property: JsonPropertyName("loadedIds")] IReadOnlyList<string> LoadedIds);
}
