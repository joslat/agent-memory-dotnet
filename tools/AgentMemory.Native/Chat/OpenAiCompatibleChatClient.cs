using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace AgentMemory.Native.Chat;

/// <summary>Where the writer's model answers: an OpenAI-compatible <c>/v1/chat/completions</c> endpoint.</summary>
internal sealed record ChatEndpoint(Uri Url, string Model, string? ApiKey, TimeSpan Timeout);

/// <summary>
/// A minimal <see cref="IChatClient"/> for an OpenAI-compatible chat completions endpoint (Ollama, llama.cpp, vLLM,
/// OpenAI itself), with generated JSON contracts: no SDK, no reflection, so it publishes cleanly with Native AOT. It
/// carries exactly what the extraction runner sends (messages as text, temperature, seed, model, output-token cap and a
/// JSON or JSON-schema reply format) and reads back the reply text, finish reason and token counts.
/// </summary>
/// <remarks>
/// Failures keep the meaning the extraction runner's retry policy reads (bug B5): an answer with an error status throws
/// <see cref="HttpRequestException"/> carrying that status (408, 429 and 5xx are retried, the rest are not), and a call
/// the endpoint did not answer in time throws one without a status (retried as a transport failure). Only the caller's
/// own cancellation surfaces as <see cref="OperationCanceledException"/>. No error text from the endpoint is copied into
/// an exception: it can echo the prompt, which holds what the user said.
/// </remarks>
internal sealed class OpenAiCompatibleChatClient : IChatClient
{
    private readonly ChatEndpoint _endpoint;
    private readonly HttpClient _http;

    public OpenAiCompatibleChatClient(ChatEndpoint endpoint)
    {
        _endpoint = endpoint;
        // The timeout is applied per call below, so a slow answer reads as a transport failure, not as cancellation.
        _http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        if (endpoint.ApiKey is not null)
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var model = string.IsNullOrEmpty(options?.ModelId) ? _endpoint.Model : options.ModelId;
        var body = JsonSerializer.SerializeToUtf8Bytes(BuildRequest(model, messages, options), ChatJsonContext.Default.ChatCompletionRequest);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_endpoint.Timeout);
        try
        {
            using var content = new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await _http.PostAsync(_endpoint.Url, content, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"The chat endpoint answered {(int)response.StatusCode} {response.ReasonPhrase}.", null, response.StatusCode);

            var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var parsed = await JsonSerializer.DeserializeAsync(stream, ChatJsonContext.Default.ChatCompletionResponse, deadline.Token)
                    .ConfigureAwait(false);
                return ToResponse(parsed, model);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException(
                $"The chat endpoint did not answer within {(int)_endpoint.Timeout.TotalMilliseconds} ms.");
        }
        catch (JsonException exception)
        {
            // A body that is not a chat completion is the endpoint's fault, like a 502: worth one more try.
            throw new HttpRequestException("The chat endpoint's answer is not a chat completion.", exception);
        }
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The native chat client answers whole replies only; extraction never streams.");

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() => _http.Dispose();

    internal static ChatCompletionRequest BuildRequest(string model, IEnumerable<ChatMessage> messages, ChatOptions? options) => new()
    {
        Model = model,
        Messages = [.. messages.Select(m => new ChatCompletionMessage { Role = m.Role.Value, Content = m.Text })],
        Temperature = options?.Temperature,
        Seed = options?.Seed,
        MaxTokens = options?.MaxOutputTokens,
        ResponseFormat = options?.ResponseFormat switch
        {
            ChatResponseFormatJson { Schema: { } schema } json => new ResponseFormat
            {
                Type = "json_schema",
                JsonSchema = new JsonSchemaFormat
                {
                    Name = string.IsNullOrEmpty(json.SchemaName) ? "reply" : json.SchemaName,
                    Description = json.SchemaDescription,
                    Schema = schema,
                },
            },
            ChatResponseFormatJson => new ResponseFormat { Type = "json_object" },
            _ => null,
        },
    };

    internal static ChatResponse ToResponse(ChatCompletionResponse? parsed, string model)
    {
        var choice = parsed?.Choices is { Count: > 0 } choices ? choices[0] : null;
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, choice?.Message?.Content ?? string.Empty))
        {
            ModelId = parsed?.Model ?? model,
            FinishReason = choice?.FinishReason switch
            {
                null => null,
                "stop" => ChatFinishReason.Stop,
                "length" => ChatFinishReason.Length,
                "content_filter" => ChatFinishReason.ContentFilter,
                "tool_calls" or "function_call" => ChatFinishReason.ToolCalls,
                var other => new ChatFinishReason(other),
            },
        };
        if (parsed?.Usage is { } usage)
        {
            response.Usage = new UsageDetails
            {
                InputTokenCount = usage.PromptTokens,
                OutputTokenCount = usage.CompletionTokens,
                TotalTokenCount = usage.TotalTokens,
            };
        }
        return response;
    }
}

internal sealed class ChatCompletionRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("messages")]
    public required List<ChatCompletionMessage> Messages { get; init; }

    [JsonPropertyName("temperature")]
    public float? Temperature { get; init; }

    [JsonPropertyName("seed")]
    public long? Seed { get; init; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; init; }

    [JsonPropertyName("response_format")]
    public ResponseFormat? ResponseFormat { get; init; }

    [JsonPropertyName("stream")]
    public bool Stream { get; init; }
}

internal sealed class ChatCompletionMessage
{
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("content")]
    public required string Content { get; init; }
}

internal sealed class ResponseFormat
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("json_schema")]
    public JsonSchemaFormat? JsonSchema { get; init; }
}

internal sealed class JsonSchemaFormat
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("schema")]
    public JsonElement Schema { get; init; }
}

internal sealed class ChatCompletionResponse
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("choices")]
    public List<ChatCompletionChoice>? Choices { get; set; }

    [JsonPropertyName("usage")]
    public ChatCompletionUsage? Usage { get; set; }
}

internal sealed class ChatCompletionChoice
{
    [JsonPropertyName("message")]
    public ChatCompletionReplyMessage? Message { get; set; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

internal sealed class ChatCompletionReplyMessage
{
    [JsonPropertyName("content")]
    public string? Content { get; set; }
}

internal sealed class ChatCompletionUsage
{
    [JsonPropertyName("prompt_tokens")]
    public long? PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public long? CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public long? TotalTokens { get; set; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ChatCompletionRequest))]
[JsonSerializable(typeof(ChatCompletionResponse))]
internal sealed partial class ChatJsonContext : JsonSerializerContext
{
}
