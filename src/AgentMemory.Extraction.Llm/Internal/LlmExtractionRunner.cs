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
        CancellationToken cancellationToken)
    {
        var chatMessages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, $"{userInstruction}\n\n{conversationText}")
        };
        var chatOptions = BuildChatOptions();

        int maxAttempts = _options.MaxRetries < 0 ? 1 : _options.MaxRetries + 1;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var response = await _chatClient.GetResponseAsync(chatMessages, chatOptions, cancellationToken)
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

        return Array.Empty<T>();
    }

    private ChatOptions BuildChatOptions()
    {
        var opts = new ChatOptions { Temperature = _options.Temperature };
        if (!string.IsNullOrEmpty(_options.ModelId))
            opts.ModelId = _options.ModelId;
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
