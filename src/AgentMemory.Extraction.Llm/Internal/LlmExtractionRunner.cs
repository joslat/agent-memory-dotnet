using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentMemory.Extraction.Llm.Internal;

/// <summary>
/// Shared execution core for the LLM extractors. Centralises the chat call, tolerant JSON
/// parsing (code-fence stripping + first-container location), and parse-failure re-prompting up
/// to <see cref="LlmExtractionOptions.MaxRetries"/>. Each extractor supplies only its system
/// prompt, a one-line instruction, and a projection from the shared response DTO to its results.
/// </summary>
internal sealed class LlmExtractionRunner
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private readonly IChatClient _chatClient;
    private readonly LlmExtractionOptions _options;
    private readonly ILogger _logger;

    internal LlmExtractionRunner(IChatClient chatClient, LlmExtractionOptions options, ILogger logger)
    {
        _chatClient = chatClient;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Calls the chat client with the given prompts, parses the response tolerantly, and projects
    /// it. On a parse failure the model is re-prompted (up to <see cref="LlmExtractionOptions.MaxRetries"/>
    /// additional times). Returns an empty list when every attempt fails to yield parseable JSON.
    /// Transport/client exceptions are intentionally not caught here — they propagate to
    /// <c>ExtractorBase</c>, which logs and returns empty.
    /// </summary>
    internal async Task<IReadOnlyList<T>> RunAsync<T>(
        string systemPrompt,
        string userInstruction,
        string conversationText,
        Func<LlmExtractionResponse, IReadOnlyList<T>> project,
        CancellationToken cancellationToken,
        bool failOnParseExhaustion = false,
        ChatResponseFormat? responseFormat = null)
    {
        var chatMessages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, $"{userInstruction}\n\n{conversationText}")
        };
        var chatOptions = BuildChatOptions(responseFormat);

        int maxAttempts = _options.MaxRetries < 0 ? 1 : _options.MaxRetries + 1;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var response = await GetResponseWithTransportRetryAsync(
                    chatMessages, chatOptions, cancellationToken)
                .ConfigureAwait(false);
            var raw = response.Text;

            if (TryParse(raw, out var dto))
                return project(dto!);

            _logger.LogWarning(
                "LLM extraction returned unparseable JSON (attempt {Attempt}/{MaxAttempts}); raw length {Length}.",
                attempt, maxAttempts, raw?.Length ?? 0);

            if (attempt < maxAttempts)
            {
                // Re-prompt: echo the bad response and ask explicitly for strict JSON.
                chatMessages.Add(new(ChatRole.Assistant, raw ?? string.Empty));
                chatMessages.Add(new(ChatRole.User,
                    "That response was not valid JSON. Reply with ONLY the JSON object — no markdown fences, no prose."));
            }
        }
        if (failOnParseExhaustion)
            throw new FormatException("LLM extraction exhausted its parse retries without valid JSON.");


        return Array.Empty<T>();
    }


    /// <summary>
    /// Calls the provider, retrying transport failures with backoff.
    /// </summary>
    /// <remarks>
    /// Separate from the parse-retry loop above, and for a different failure. That loop re-prompts a
    /// model that answered with unparseable JSON; this one re-sends an identical request that never
    /// got an answer at all. Before this existed there was no transport retry anywhere in the
    /// extraction path, and two 614-call preparations died mid-run on a single transient, at 37 and
    /// 26 minutes each.
    /// <para>
    /// A <see cref="FormatException"/> is deliberately not retried: it is caused by the request's own
    /// shape, so re-sending it unchanged cannot help. That mirrors the batch splitter, which splits
    /// on exactly that set and nothing else. Cancellation is never retried — retrying it would make
    /// the preparation watchdog's timeout unenforceable.
    /// </para>
    /// </remarks>

    /// <summary>
    /// Whether a provider failure is worth re-sending an identical request for.
    /// </summary>
    /// <remarks>
    /// Retrying a permanent failure is not merely useless, it is expensive: an n=50 preparation spent
    /// its 60-minute budget re-sending requests the provider had already rejected with
    /// <b>HTTP 400</b>, and the watchdog fired with 7 failures and 544 of 614 calls done. A 400 says
    /// the request is wrong — most often too large — and the same request will be just as wrong the
    /// third time.
    /// <para>
    /// Retryable: 408, 429, and 5xx, plus transport-level exceptions that never reached the service
    /// and so carry no status. Everything else is permanent. An oversized request is separately
    /// recoverable by splitting the batch, which is a different mechanism and the right one.
    /// </para>
    /// </remarks>
    internal static bool IsTransient(Exception exception)
    {
        var status = TryGetStatus(exception);
        if (status is null)
            return true;   // never reached the service: a connection reset, a DNS failure, a timeout
        return status is 408 or 429 || status >= 500;
    }

    /// <summary>
    /// The HTTP status behind a provider exception, or null when the call never got one.
    /// </summary>
    /// <remarks>
    /// Read reflectively rather than by referencing System.ClientModel: the status lives on
    /// <c>ClientResultException.Status</c> for Azure/OpenAI clients and on
    /// <c>HttpRequestException.StatusCode</c> for raw HTTP, and this library should not take a
    /// package dependency to classify an error.
    /// </remarks>
    internal static int? TryGetStatus(Exception exception)
    {
        if (exception is HttpRequestException { StatusCode: { } code })
            return (int)code;

        var property = exception.GetType().GetProperty("Status");
        if (property?.GetValue(exception) is int status && status > 0)
            return status;

        return exception.InnerException is null ? null : TryGetStatus(exception.InnerException);
    }

    private async Task<ChatResponse> GetResponseWithTransportRetryAsync(
        List<ChatMessage> chatMessages,
        ChatOptions chatOptions,
        CancellationToken cancellationToken)
    {
        int maxAttempts = _options.MaxRetries < 0 ? 1 : _options.MaxRetries + 1;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await _chatClient
                    .GetResponseAsync(chatMessages, chatOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (FormatException)
            {
                throw;
            }
            catch (Exception exception) when (attempt < maxAttempts && IsTransient(exception))
            {
                _logger.LogWarning(
                    exception,
                    "LLM extraction transport failure (attempt {Attempt}/{MaxAttempts}); retrying.",
                    attempt, maxAttempts);
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private ChatOptions BuildChatOptions(ChatResponseFormat? responseFormat)
    {
        var opts = new ChatOptions { Temperature = _options.Temperature };
        if (!string.IsNullOrEmpty(_options.ModelId))
            opts.ModelId = _options.ModelId;
        if (_options.UseJsonResponseFormat)
            opts.ResponseFormat = responseFormat ?? ChatResponseFormat.Json;
        return opts;
    }

    /// <summary>Attempts to parse a (possibly fenced/prose-wrapped) model response into the shared DTO.</summary>
    internal static bool TryParse(string? raw, out LlmExtractionResponse? dto)
    {
        dto = null;
        var json = ExtractJson(raw);
        if (json is null) return false;

        try
        {
            dto = JsonSerializer.Deserialize<LlmExtractionResponse>(json, JsonOptions);
            return dto is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Extracts a JSON payload from a raw model response: strips a surrounding markdown code fence,
    /// then returns the substring spanning the first JSON container (object preferred, else array)
    /// to its <em>matching</em> close — found by a depth scan that ignores braces/brackets inside
    /// string literals, so trailing prose or a brace inside a string value cannot over-capture.
    /// Returns null when no balanced container is present.
    /// </summary>
    internal static string? ExtractJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim();

        // Strip a leading ``` / ```json fence and its matching closing fence. The closing fence is
        // only recognized on a line boundary (or at the very end) so a ``` embedded inside JSON
        // string content is never mistaken for it. (JSON strings cannot contain a literal newline,
        // so "\n```" cannot occur inside a value.)
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            int firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0)
                text = text[(firstNewline + 1)..];

            int closingFence = text.LastIndexOf("\n```", StringComparison.Ordinal);
            if (closingFence >= 0)
                text = text[..closingFence];
            else if (text.EndsWith("```", StringComparison.Ordinal))
                text = text[..^3];

            text = text.Trim();
        }

        int objStart = text.IndexOf('{');
        int arrStart = text.IndexOf('[');

        // Prefer an object (the DTO shape); fall back to an array.
        int start = objStart >= 0 ? objStart : arrStart;
        if (start < 0) return null;

        int end = FindMatchingClose(text, start);
        return end > start ? text[start..(end + 1)] : null;
    }

    /// <summary>
    /// Returns the index of the brace/bracket that closes the container opening at
    /// <paramref name="start"/>, tracking nesting depth while ignoring delimiters inside JSON string
    /// literals (honoring backslash escapes). Returns -1 if the container is never balanced.
    /// </summary>
    private static int FindMatchingClose(string text, int start)
    {
        int depth = 0;
        bool inString = false;
        bool escaped = false;

        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];

            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"': inString = true; break;
                case '{':
                case '[': depth++; break;
                case '}':
                case ']':
                    depth--;
                    if (depth == 0) return i;
                    break;
            }
        }

        return -1;
    }
}
