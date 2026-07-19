using System.Diagnostics;
using System.Globalization;
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
            retryEligibility: NamsRetryEligibility.NonIdempotent,
            DeserializeAsync<NamsConversation>,
            cancellationToken);

    public Task<NamsContext> GetContextAsync(string conversationId, CancellationToken cancellationToken) =>
        InvokeAsync(
            "get_context",
            () => new HttpRequestMessage(HttpMethod.Get, $"conversations/{Uri.EscapeDataString(conversationId)}/context"),
            retryEligibility: NamsRetryEligibility.Idempotent,
            DeserializeAsync<NamsContext>,
            cancellationToken);

    public async Task<IReadOnlyList<NamsMessage>> AddMessagesAsync(
        string conversationId, IReadOnlyList<NamsMessageInput> messages, CancellationToken cancellationToken)
    {
        var path = $"conversations/{Uri.EscapeDataString(conversationId)}/messages/bulk";
        var response = await InvokeAsync(
            "store_turn",
            () => BuildJsonRequest(HttpMethod.Post, path, new AddMessagesBulkRequestBody(messages)),
            retryEligibility: NamsRetryEligibility.NonIdempotent,
            DeserializeAsync<AddMessagesBatchResponseBody>,
            cancellationToken).ConfigureAwait(false);
        return response.Messages;
    }

    public async Task<IReadOnlyList<NamsEntity>> SearchEntitiesAsync(
        string query, string? type, int limit, CancellationToken cancellationToken)
    {
        // A POST verb, but a pure read-only query with no server-side side effects -- the engineering plan's
        // retry matrix lists "Search" as always-retryable regardless of the verb it happens to use.
        var response = await InvokeAsync(
            "search_entities",
            () => BuildJsonRequest(HttpMethod.Post, "entities/search", new SearchEntitiesRequestBody(query, type, limit)),
            retryEligibility: NamsRetryEligibility.Idempotent,
            DeserializeAsync<SearchEntitiesResponseBody>,
            cancellationToken).ConfigureAwait(false);
        return response.Entities;
    }

    public async Task<IReadOnlyList<NamsEntity>> ListEntitiesAsync(int limit, CancellationToken cancellationToken)
    {
        var response = await InvokeAsync(
            "list_entities",
            () => new HttpRequestMessage(HttpMethod.Get, $"entities?limit={limit.ToString(CultureInfo.InvariantCulture)}"),
            retryEligibility: NamsRetryEligibility.Idempotent,
            DeserializeAsync<ListEntitiesResponseBody>,
            cancellationToken).ConfigureAwait(false);
        return response.Entities;
    }

    public async Task<IReadOnlyList<NamsMessage>> SearchMessagesAsync(
        string conversationId, string query, int limit, CancellationToken cancellationToken)
    {
        // A POST verb, but a pure read-only query with no server-side side effects, same as SearchEntitiesAsync.
        var path = $"conversations/{Uri.EscapeDataString(conversationId)}/search";
        var response = await InvokeAsync(
            "search_messages",
            () => BuildJsonRequest(HttpMethod.Post, path, new SearchMessagesRequestBody(query, limit)),
            retryEligibility: NamsRetryEligibility.Idempotent,
            DeserializeAsync<SearchMessagesResponseBody>,
            cancellationToken).ConfigureAwait(false);
        return response.Messages;
    }

    public Task DeleteConversationAsync(string conversationId, CancellationToken cancellationToken) =>
        InvokeAsync(
            "delete_conversation",
            () => new HttpRequestMessage(HttpMethod.Delete, $"conversations/{Uri.EscapeDataString(conversationId)}"),
            // Confirmed live: deleting an already-deleted (or otherwise nonexistent) conversation still
            // returns 200, not 404 -- genuinely idempotent.
            retryEligibility: NamsRetryEligibility.Idempotent,
            DeserializeAsync<DeleteConversationResponseBody>,
            cancellationToken);

    public async Task<IReadOnlyList<NamsConversationSummary>> ListConversationsAsync(
        int limit, CancellationToken cancellationToken)
    {
        var response = await InvokeAsync(
            "list_conversations",
            () => new HttpRequestMessage(HttpMethod.Get, $"conversations?limit={limit.ToString(CultureInfo.InvariantCulture)}"),
            retryEligibility: NamsRetryEligibility.Idempotent,
            DeserializeAsync<ListConversationsResponseBody>,
            cancellationToken).ConfigureAwait(false);
        return response.Conversations;
    }

    public async Task<IReadOnlyList<NamsObservation>> GetObservationsAsync(
        string conversationId, int limit, CancellationToken cancellationToken)
    {
        var path = $"conversations/{Uri.EscapeDataString(conversationId)}/observations?limit={limit.ToString(CultureInfo.InvariantCulture)}";
        var response = await InvokeAsync(
            "get_observations",
            () => new HttpRequestMessage(HttpMethod.Get, path),
            retryEligibility: NamsRetryEligibility.Idempotent,
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
            // state, none of the duplicate-row risk a non-idempotent classification guards against.
            retryEligibility: NamsRetryEligibility.Idempotent,
            DeserializeAsync<NamsEntityFeedbackResult>,
            cancellationToken);

    public Task<NamsEntityGraph> GetEntityGraphAsync(CancellationToken cancellationToken) =>
        InvokeAsync(
            "get_entity_graph",
            () => new HttpRequestMessage(HttpMethod.Get, "entities/graph"),
            retryEligibility: NamsRetryEligibility.Idempotent,
            DeserializeAsync<NamsEntityGraph>,
            cancellationToken);

    public Task<NamsGraphExpansion> ExpandGraphAsync(
        string nodeId, IReadOnlyList<string> loadedIds, CancellationToken cancellationToken) =>
        InvokeAsync(
            "expand_graph",
            // POST verb, but read-only (no server-side side effects), same as SearchEntitiesAsync/SearchMessagesAsync.
            () => BuildJsonRequest(HttpMethod.Post, "graph/expand", new ExpandGraphRequestBody(nodeId, loadedIds)),
            retryEligibility: NamsRetryEligibility.Idempotent,
            DeserializeAsync<NamsGraphExpansion>,
            cancellationToken);

    public Task<NamsReasoningStep> RecordReasoningStepAsync(
        string conversationId, string reasoning, string actionTaken, string? result, CancellationToken cancellationToken) =>
        InvokeAsync(
            "record_reasoning_step",
            () => BuildJsonRequest(
                HttpMethod.Post, "reasoning/steps",
                new RecordReasoningStepRequestBody(conversationId, reasoning, actionTaken, result)),
            retryEligibility: NamsRetryEligibility.NonIdempotent,
            DeserializeAsync<NamsReasoningStep>,
            cancellationToken);

    public async Task<IReadOnlyList<NamsReasoningStep>> ListReasoningStepsAsync(
        string conversationId, CancellationToken cancellationToken)
    {
        var path = $"reasoning/steps?conversation_id={Uri.EscapeDataString(conversationId)}";
        var response = await InvokeAsync(
            "list_reasoning_steps",
            () => new HttpRequestMessage(HttpMethod.Get, path),
            retryEligibility: NamsRetryEligibility.Idempotent,
            DeserializeAsync<ListReasoningStepsResponseBody>,
            cancellationToken).ConfigureAwait(false);
        return response.Steps;
    }

    public Task<NamsToolCall> RecordToolCallAsync(
        string? stepId, string toolName, string input, string? output, string? status, int? durationMs,
        CancellationToken cancellationToken) =>
        InvokeAsync(
            "record_tool_call",
            () => BuildJsonRequest(
                HttpMethod.Post, "reasoning/tool-calls",
                new RecordToolCallRequestBody(stepId, toolName, input, output, status, durationMs)),
            retryEligibility: NamsRetryEligibility.NonIdempotent,
            DeserializeAsync<NamsToolCall>,
            cancellationToken);

    public Task<NamsReasoningTrace> GetReasoningTraceAsync(string conversationId, CancellationToken cancellationToken) =>
        InvokeAsync(
            "get_reasoning_trace",
            () => new HttpRequestMessage(HttpMethod.Get, $"reasoning/trace/{Uri.EscapeDataString(conversationId)}"),
            retryEligibility: NamsRetryEligibility.Idempotent,
            DeserializeAsync<NamsReasoningTrace>,
            cancellationToken);

    public Task<NamsEntityProvenance> GetEntityProvenanceAsync(string entityId, CancellationToken cancellationToken) =>
        InvokeAsync(
            "get_entity_provenance",
            () => new HttpRequestMessage(HttpMethod.Get, $"reasoning/provenance/{Uri.EscapeDataString(entityId)}"),
            retryEligibility: NamsRetryEligibility.Idempotent,
            DeserializeAsync<NamsEntityProvenance>,
            cancellationToken);

    public Task<NamsQueryResult> ExecuteCypherQueryAsync(
        string cypher, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken) =>
        InvokeAsync(
            "execute_cypher_query",
            // Read-only by NAMS's own server-enforced contract (confirmed live), same as ExpandGraphAsync/
            // SearchEntitiesAsync despite the POST verb.
            () => BuildJsonRequest(HttpMethod.Post, "query", new ExecuteCypherQueryRequestBody(cypher, parameters)),
            retryEligibility: NamsRetryEligibility.Idempotent,
            DeserializeAsync<NamsQueryResult>,
            cancellationToken);

    public Task<NamsCreateEntityResult> CreateEntityAsync(
        string name, string type, string? description, CancellationToken cancellationToken) =>
        InvokeAsync(
            "create_entity",
            () => BuildJsonRequest(HttpMethod.Post, "entities", new CreateEntityRequestBody(name, type, description)),
            retryEligibility: NamsRetryEligibility.NonIdempotent,
            DeserializeAsync<NamsCreateEntityResult>,
            cancellationToken);

    private async Task<T> InvokeAsync<T>(
        string operationName,
        Func<HttpRequestMessage> requestFactory,
        NamsRetryEligibility retryEligibility,
        Func<Stream, CancellationToken, Task<T>> deserialize,
        CancellationToken cancellationToken)
    {
        using var activity = NamsActivitySource.Instance.StartActivity($"agentmemory.nams.{operationName}");
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await _retryPolicy.ExecuteAsync(
                requestFactory, _httpClient, retryEligibility, cancellationToken,
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

    // WhenWritingNull on both properties: INamsClient.SetEntityFeedbackAsync's doc comment promises "pass null
    // to leave either unset" -- without this, default JsonSerializerOptions would serialize a null argument as
    // an explicit `"userScore":null` in the PUT body rather than omitting the key, which could plausibly read
    // to NAMS as "clear this field" instead of "don't touch it" (a real self-review finding: this diff's own
    // live probe never exercised the partial-null case).
    private sealed record EntityFeedbackRequestBody(
        [property: JsonPropertyName("userScore"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? UserScore,
        [property: JsonPropertyName("confirmed"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Confirmed);

    private sealed record ExpandGraphRequestBody(
        [property: JsonPropertyName("nodeId")] string NodeId,
        [property: JsonPropertyName("loadedIds")] IReadOnlyList<string> LoadedIds);

    // WhenWritingNull on this record's optional field (and on RecordToolCallRequestBody's below), same
    // reasoning as EntityFeedbackRequestBody above: without it, passing null serializes an explicit JSON null
    // in the POST body rather than omitting the key, which risks NAMS's request schema rejecting the call if
    // it models these as "may be absent" without also being "may be null" (a real, review-caught
    // inconsistency -- these two request bodies were the only ones in this push missing the annotation their
    // siblings already carry).
    private sealed record RecordReasoningStepRequestBody(
        [property: JsonPropertyName("conversationId")] string ConversationId,
        [property: JsonPropertyName("reasoning")] string Reasoning,
        [property: JsonPropertyName("actionTaken")] string ActionTaken,
        [property: JsonPropertyName("result"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Result);

    private sealed record ListReasoningStepsResponseBody(
        [property: JsonPropertyName("steps")] IReadOnlyList<NamsReasoningStep> Steps);

    // See RecordReasoningStepRequestBody's comment above for why these are WhenWritingNull.
    private sealed record RecordToolCallRequestBody(
        [property: JsonPropertyName("stepId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StepId,
        [property: JsonPropertyName("toolName")] string ToolName,
        [property: JsonPropertyName("input")] string Input,
        [property: JsonPropertyName("output"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Output,
        [property: JsonPropertyName("status"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Status,
        [property: JsonPropertyName("durationMs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? DurationMs);

    private sealed record ExecuteCypherQueryRequestBody(
        [property: JsonPropertyName("cypher")] string Cypher,
        [property: JsonPropertyName("params"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, object?>? Params);

    private sealed record CreateEntityRequestBody(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description);
}
