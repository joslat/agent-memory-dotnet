using System.Text.Json;
using AgentMemory.Abstractions.Services;
using Microsoft.Extensions.AI;

namespace AgentMemory.RouterArena.Record;

/// <summary>
/// Strategy PLAN 41.26 (c): the update judge's question asked of the host's own chat model instead of a decision model
/// (JEV), to measure whether storage needs an outside judge at all. The question and its two answers are the gate's, word
/// for word (SystemOneUpdateJudge: the 1.9 question for pairs found by similarity, round 4's "v3" for the closings a writer
/// names); the model answers yes or no per pair, read as a probability of 1 or 0 against the write path's thresholds.
/// Arena only until the measurement passes its registered rule; the library has no such judge.
/// </summary>
internal sealed class HostModelUpdateJudge(IChatClient chat) : IMemoryUpdateJudge
{
    private static readonly (string True, string False) Criteria =
        ("the stored memory stops being true now: it is changed, cancelled or corrected by the new one",
         "both stay true: the stored memory is a past result or earlier event that remains history, or they are about different things");

    private static readonly (string True, string False) NamedCriteria =
        ("the stored memory no longer holds as stored: the new one changes its value (a new job, place, commute, count, time or "
         + "plan), corrects it, states the same thing more precisely (a date, a place, a number), cancels it, or reports that a "
         + "planned or ongoing thing has now happened, ended or been called off",
         "both hold as they are: they are about different things, or the stored memory is a dated past event or result that "
         + "stays true as history beside a separate new one (an earlier race, a trip already taken)");

    public bool IsEnabled => true;

    public async Task<IReadOnlyDictionary<string, double>> JudgeAsync(MemoryUpdateRequest request, CancellationToken cancellationToken = default)
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
        var response = await chat.GetResponseAsync(
            [new ChatMessage(ChatRole.System, system), new ChatMessage(ChatRole.User, user)],
            new ChatOptions { MaxOutputTokens = 4000 }, cancellationToken).ConfigureAwait(false);
        return Parse(response.Text, request.Pairs.Select(p => p.Key));
    }

    /// <summary>Each key's yes as 1 and no as 0; a key the answer leaves out, or an answer without JSON, is left out (nothing closes).</summary>
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
                    var s = v.GetString()!.Trim().ToLowerInvariant();
                    if (s.StartsWith("yes", StringComparison.Ordinal)) result[key] = 1.0;
                    else if (s.StartsWith("no", StringComparison.Ordinal)) result[key] = 0.0;
                }
            }
        }
        catch (JsonException)
        {
        }
        return result;
    }
}
