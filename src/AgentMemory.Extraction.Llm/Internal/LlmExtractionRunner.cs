using System.Diagnostics;
using System.Text.Json;
using AgentMemory.Abstractions.Diagnostics;
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
        const int BaseMessages = 2;
        var chatOptions = BuildChatOptions(responseFormat);

        int maxAttempts = _options.MaxRetries < 0 ? 1 : _options.MaxRetries + 1;
        string? lastError = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var span = AgentMemoryDiagnostics.Source.StartActivity(AttemptSpanName);
            span?.SetTag("memory.extract.attempt", attempt);

            var response = await GetResponseWithTransportRetryAsync(
                    chatMessages, chatOptions, cancellationToken)
                .ConfigureAwait(false);
            var raw = response.Text;
            span?.SetTag("memory.extract.finish_reason", response.FinishReason?.Value);
            span?.SetTag("memory.extract.output_tokens", response.Usage?.OutputTokenCount);

            // A refusal is not a formatting problem: asking again gets the same refusal.
            if (response.FinishReason == ChatFinishReason.ContentFilter)
            {
                lastError = "the provider's content filter stopped the reply";
                Record(span, "filtered", lastError);
                _logger.LogWarning("LLM extraction stopped by the content filter (attempt {Attempt}); not retrying.", attempt);
                if (failOnParseExhaustion)
                    throw new ContentFilteredException($"LLM extraction produced no usable JSON: {lastError}.");
                return Array.Empty<T>();
            }

            var parsed = Parse(raw);
            if (parsed.IsUsable)
            {
                span?.SetTag("memory.extract.items.kept", parsed.KeptItems);
                span?.SetTag("memory.extract.items.dropped", parsed.Dropped.Count);
                Record(span, parsed.Status == ParseStatus.Ok ? "ok" : "salvaged",
                    parsed.Dropped.Count > 0 ? parsed.Error : null);
                if (parsed.Dropped.Count > 0)
                {
                    _logger.LogWarning(
                        "LLM extraction kept {Kept} item(s) and dropped {Dropped} unreadable one(s): {Error}",
                        parsed.KeptItems, parsed.Dropped.Count, parsed.Error);
                }
                return project(parsed.Response!);
            }

            bool truncated = response.FinishReason == ChatFinishReason.Length;
            lastError = truncated ? "the reply was cut off before the JSON was complete" : parsed.Error;
            Record(span, truncated ? "truncated" : parsed.Status == ParseStatus.Salvaged ? "schema_error" : "syntax_error",
                lastError);
            _logger.LogWarning(
                "LLM extraction reply unusable (attempt {Attempt}/{MaxAttempts}, finish {Finish}, length {Length}): {Error}",
                attempt, maxAttempts, response.FinishReason?.Value, raw?.Length ?? 0, lastError);
            if (_options.LogRawResponseOnFailure && raw is not null)
            {
                _logger.LogWarning("Unusable extraction reply (first 2,000 chars): {Raw}",
                    raw.Length <= 2000 ? raw : raw[..2000]);
            }

            if (attempt < maxAttempts)
            {
                // One repair message, replaced (never accumulated) each attempt, naming the actual problem.
                // The bad reply is NOT echoed back: it grew every retry by the size of the failure, and a
                // truncated or schema-broken document gives the model nothing useful to copy.
                chatMessages.RemoveRange(BaseMessages, chatMessages.Count - BaseMessages);
                if (truncated && ((int?)response.Usage?.OutputTokenCount ?? chatOptions.MaxOutputTokens) is { } used && used > 0)
                {
                    // Room to finish: double what the cut-off reply used. When neither the usage nor a cap
                    // is known, no limit is set -- guessing one could hand the retry LESS room than the
                    // provider default that cut the first reply off.
                    chatOptions.MaxOutputTokens = Math.Min(MaxOutputTokenCeiling,
                        Math.Max(chatOptions.MaxOutputTokens ?? 0, used) * 2);
                    span?.SetTag("memory.extract.next_max_output_tokens", chatOptions.MaxOutputTokens);
                }
                chatMessages.Add(new(ChatRole.User, RepairInstruction(lastError!, truncated)));
            }
        }
        if (failOnParseExhaustion)
            throw new FormatException($"LLM extraction exhausted its attempts without usable JSON: {lastError}.");

        return Array.Empty<T>();
    }

    /// <summary>The span wrapped around each model call.</summary>
    internal const string AttemptSpanName = "memory.extract.attempt";

    /// <summary>
    /// The activity an extraction call belongs to: <paramref name="current"/> with any attempt span above it
    /// skipped. Meters that read the extractor's span (its tags, its call count) from inside the chat client
    /// must go through this, because the chat call now runs one level deeper, inside its attempt span.
    /// </summary>
    internal static Activity? CallerActivity(Activity? current)
    {
        while (current?.OperationName == AttemptSpanName) current = current.Parent;
        return current;
    }

    /// <summary>Output-token ceiling for a truncation retry (the retry doubles what the cut-off reply used).</summary>
    internal const int MaxOutputTokenCeiling = 32_768;

    /// <summary>The fixed opening of every repair message, so meters can recognise a parse retry.</summary>
    internal const string RepairLead = "Your previous reply could not be used:";

    internal static string RepairInstruction(string error, bool truncated) =>
        truncated
            ? $"{RepairLead} {error}. Reply again with ONLY the JSON object — no markdown fences, no prose — "
              + "and keep it compact so it fits."
            : $"{RepairLead} {error}. Reply again with ONLY the JSON object — no markdown fences, no prose.";

    /// <summary>True when a chat message is a repair instruction this runner wrote (a parse retry).</summary>
    internal static bool IsRepairInstruction(ChatMessage message) =>
        message.Role == ChatRole.User &&
        (message.Text?.StartsWith(RepairLead, StringComparison.Ordinal) ?? false);

    private static void Record(Activity? span, string outcome, string? error)
    {
        if (span is null) return;
        span.SetTag("memory.extract.outcome", outcome);
        if (error is not null) span.SetTag("memory.extract.error", error);
        if (outcome is "syntax_error" or "schema_error" or "truncated" or "filtered")
            span.SetStatus(ActivityStatusCode.Error, outcome);
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
        // Sent only when configured, so the default request is byte-identical to before this existed.
        if (_options.Seed is { } seed) opts.Seed = seed;
        if (!string.IsNullOrEmpty(_options.ModelId))
            opts.ModelId = _options.ModelId;
        if (_options.UseJsonResponseFormat)
            opts.ResponseFormat = responseFormat ?? ChatResponseFormat.Json;
        return opts;
    }

    /// <summary>Attempts to parse a (possibly fenced/prose-wrapped) model response into the shared DTO.</summary>
    /// <remarks>True exactly when the runner would use the reply (<see cref="ParseResult.IsUsable"/>).</remarks>
    internal static bool TryParse(string? raw, out LlmExtractionResponse? dto)
    {
        var parsed = Parse(raw);
        dto = parsed.Response;
        return parsed.IsUsable;
    }

    internal enum ParseStatus
    {
        /// <summary>The whole document deserialised.</summary>
        Ok,

        /// <summary>The document was well-formed JSON but some items were not; the readable items were kept.</summary>
        Salvaged,

        /// <summary>No JSON object could be read at all.</summary>
        Unusable,
    }

    internal sealed record ParseResult(
        ParseStatus Status,
        LlmExtractionResponse? Response,
        IReadOnlyList<string> Dropped,
        string? Error)
    {
        // Null-safe: the serializer leaves a list null when the model writes "facts": null.
        public int KeptItems => Response is null
            ? 0
            : (Response.Entities?.Count ?? 0) + (Response.Facts?.Count ?? 0) +
              (Response.Preferences?.Count ?? 0) + (Response.Relations?.Count ?? 0);

        /// <summary>
        /// Worth projecting: a clean parse (even an empty one — "nothing to extract" is an answer), or a
        /// salvage that kept something. A salvage that dropped every item is a failure to repair.
        /// </summary>
        public bool IsUsable => Status == ParseStatus.Ok ||
                                (Status == ParseStatus.Salvaged && (KeptItems > 0 || Dropped.Count == 0));
    }

    /// <summary>
    /// Parses a model response, telling a <b>syntax</b> failure (no readable JSON) from a <b>schema</b>
    /// failure (well-formed JSON with an item the DTO cannot hold).
    /// </summary>
    /// <remarks>
    /// Before this, one unreadable field — a date the model wrote as <c>"2026-08"</c>, a confidence
    /// written as <c>"high"</c> — failed the whole typed deserialisation, was reported as "not valid
    /// JSON", and discarded every other entity, fact and preference in the reply. Now a schema failure
    /// is salvaged item by item: each array element is read on its own, the readable ones are kept,
    /// and each dropped one is named by its path so the repair message (if one is needed) can say
    /// exactly what was wrong.
    /// </remarks>
    internal static ParseResult Parse(string? raw)
    {
        var json = ExtractJson(raw);
        if (json is null)
            return new(ParseStatus.Unusable, null, [], "the reply contained no JSON object");

        try
        {
            var dto = JsonSerializer.Deserialize<LlmExtractionResponse>(json, JsonOptions);
            return dto is null
                ? new(ParseStatus.Unusable, null, [], "the reply was JSON null")
                : new(ParseStatus.Ok, dto, [], null);
        }
        catch (Exception typed) when (IsReadFailure(typed))
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json);
            }
            catch (JsonException syntax)
            {
                return new(ParseStatus.Unusable, null, [], $"the reply is not valid JSON ({Describe(syntax)})");
            }

            using (document)
                return Salvage(document.RootElement, typed);
        }
    }

    private static ParseResult Salvage(JsonElement root, Exception typed)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return new(ParseStatus.Unusable, null, [],
                $"expected a JSON object but the reply is a JSON {root.ValueKind.ToString().ToLowerInvariant()}");

        var response = new LlmExtractionResponse();
        var dropped = new List<string>();
        foreach (var property in root.EnumerateObject())
        {
            // Case-insensitive, like the serializer's own property matching.
            switch (property.Name.ToLowerInvariant())
            {
                case "entities": ReadItems(property, response.Entities, dropped); break;
                case "facts": ReadItems(property, response.Facts, dropped); break;
                case "preferences": ReadItems(property, response.Preferences, dropped); break;
                case "relations": ReadItems(property, response.Relations, dropped); break;
                case "processed_source_sessions":
                    // A control field, not an item: the batch extractor validates its acknowledgement
                    // against it, so an unreadable one is a reply to repair, not one to half-accept.
                    try
                    {
                        response.ProcessedSourceSessions = property.Value.Deserialize<List<string>>(JsonOptions);
                    }
                    catch (Exception ex) when (IsReadFailure(ex))
                    {
                        return new(ParseStatus.Unusable, null, [], $"{property.Name}: {Describe(ex)}");
                    }
                    break;
            }
        }

        var error = dropped.Count > 0
            ? string.Join("; ", dropped.Take(3)) + (dropped.Count > 3 ? $"; and {dropped.Count - 3} more" : string.Empty)
            : Describe(typed);
        return new(ParseStatus.Salvaged, response, dropped, error);
    }

    private static void ReadItems<T>(JsonProperty property, List<T> into, List<string> dropped)
    {
        if (property.Value.ValueKind == JsonValueKind.Null)
            return;
        if (property.Value.ValueKind != JsonValueKind.Array)
        {
            dropped.Add($"{property.Name}: expected an array");
            return;
        }

        int index = 0;
        foreach (var element in property.Value.EnumerateArray())
        {
            try
            {
                if (element.Deserialize<T>(JsonOptions) is { } item)
                    into.Add(item);
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                var at = ex is JsonException { Path: { Length: > 1 } path } ? path[1..] : string.Empty;   // "$.valid_from" -> ".valid_from"
                dropped.Add($"{property.Name}[{index}]{at}: {Describe(ex)}");
            }
            index++;
        }
    }

    /// <summary>
    /// What reading a value can throw: the serializer's own JsonException, and what a converter or a
    /// number parse may raise inside it. Anything else (out of memory, a bug) still propagates.
    /// </summary>
    private static bool IsReadFailure(Exception ex) =>
        ex is JsonException or FormatException or OverflowException or InvalidOperationException or ArgumentException;

    /// <summary>The serializer's message without its trailing "Path: ... | LineNumber: ..." noise.</summary>
    private static string Describe(Exception ex)
    {
        var message = ex.Message;
        var cut = message.IndexOf(" Path:", StringComparison.Ordinal);
        if (cut < 0) cut = message.IndexOf(" LineNumber:", StringComparison.Ordinal);
        return (cut > 0 ? message[..cut] : message).TrimEnd('.', ' ');
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
