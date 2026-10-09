using System.Text.Json;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extraction.Llm.Internal;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentMemory.Extraction.Llm;

/// <summary>
/// The update judge's second tier (AMWRITE001, <see cref="LlmExtractionOptions.ChatModelUpdateJudge"/>): the closings a
/// store-aware writer names, confirmed by the host's chat model when no outside judge answers. The question and its two
/// answers are the gate's (SystemOneUpdateJudge), word for word; the model answers yes or no per pair, read as a probability
/// of 1 or 0 against the write path's threshold. An answer without JSON confirms nothing.
/// </summary>
internal sealed class ChatModelUpdateJudge : IMemoryUpdateJudgeFallback
{
    internal static readonly (string True, string False) Criteria =
        ("the stored memory stops being true now: it is changed, cancelled or corrected by the new one",
         "both stay true: the stored memory is a past result or earlier event that remains history, or they are about different things");

    internal static readonly (string True, string False) NamedCriteria =
        ("the stored memory no longer holds as stored: the new one changes its value (a new job, place, commute, count, time or "
         + "plan), corrects it, states the same thing more precisely (a date, a place, a number), cancels it, or reports that a "
         + "planned or ongoing thing has now happened, ended or been called off",
         "both hold as they are: they are about different things, or the stored memory is a dated past event or result that "
         + "stays true as history beside a separate new one (an earlier race, a trip already taken)");

    private readonly LlmExtractionOptions _options;
    private readonly LlmExtractionRunner _runner;

    public ChatModelUpdateJudge(IChatClient chatClient, IOptions<LlmExtractionOptions> options, ILogger<ChatModelUpdateJudge> logger)
    {
        _options = options.Value;
        _runner = new LlmExtractionRunner(chatClient, _options, logger);
    }

#pragma warning disable AMWRITE001
    public bool IsEnabled => _options.ChatModelUpdateJudge;
#pragma warning restore AMWRITE001

    public async Task<IReadOnlyDictionary<string, double>> JudgeAsync(MemoryUpdateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = await _runner.CompleteAsync(Messages(request), 4000, json: false, cancellationToken).ConfigureAwait(false);
        return Parse(response.Text, request.Pairs.Select(p => p.Key));
    }

    internal static IReadOnlyList<ChatMessage> Messages(MemoryUpdateRequest request)
    {
        var (yes, no) = request.Named ? NamedCriteria : Criteria;
        var system =
            "You keep a person's long-term memory for their personal assistant. For each numbered pair, decide whether the new memory "
            + "replaces the stored one.\n"
            + $"Answer yes when {yes}.\nAnswer no when {no}.\n"
            + "Answer with JSON only: {\"p1\": \"yes\", \"p2\": \"no\", ...} with one entry for every pair, keyed as given.";
        var lines = string.Join("\n", request.Pairs.Select(p =>
            $"{p.Key}: Does the new memory \"{p.NewMemory}\" replace this stored one: \"{p.StoredMemory}\"?"));
        var user = $"Today: {request.Now:dddd d MMMM yyyy}\n"
            + (request.Said is { Length: > 0 } said ? $"What the person said: {said}\n" : "")
            + $"\n{lines}";
        return [new ChatMessage(ChatRole.System, system), new ChatMessage(ChatRole.User, user)];
    }

    /// <summary>Each key's yes as 1 and no as 0; a key the answer leaves out, or an answer without JSON, is left out.</summary>
    internal static IReadOnlyDictionary<string, double> Parse(string? text, IEnumerable<string> keys)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return result;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return result;
        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            foreach (var key in keys)
            {
                if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                {
                    var answer = v.GetString()!.Trim();
                    if (answer.StartsWith("yes", StringComparison.OrdinalIgnoreCase)) result[key] = 1.0;
                    else if (answer.StartsWith("no", StringComparison.OrdinalIgnoreCase)) result[key] = 0.0;
                }
            }
        }
        catch (JsonException)
        {
        }
        return result;
    }
}
