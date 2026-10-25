using System.Globalization;
using System.Text;
using System.Text.Json;
using AgentMemory.Abstractions.Services;
using Microsoft.Extensions.AI;

namespace AgentMemory.Gate;

/// <summary>
/// The retrieval gate on the host's chat model, for a host with no System One judge (R-chat-judge): every memory a
/// recall found, judged in one call with the gate's own question.
/// </summary>
/// <remarks>
/// <para>
/// A candidate under measurement, registered by no preset. The question and its two answers are
/// <see cref="GatePrompt.Question"/>'s, word for word, and the memory types read as <see cref="GatePrompt.Meanings"/>
/// unless a caller supplies its own wording. The model answers the likelihood of yes, 0 to 100, per memory.
/// </para>
/// <para>
/// Like every gate it returns a whole decision or throws: a reply that leaves out a memory, or gives one an answer outside
/// 0 to 100, is refused, and the caller takes the similarity floor (<see cref="GatedMemoryContextAssembler"/>).
/// </para>
/// </remarks>
internal sealed class ChatModelMemoryGate(IChatClient chat, IReadOnlyDictionary<string, string>? meanings = null,
    ReasoningEffort? effort = null) : IMemoryGate
{
    // Built from GatePrompt.Question so the two wordings cannot drift apart.
    internal static readonly string Instructions =
        "You decide which of a person's memories their personal assistant sees before it replies. You are shown today's "
        + "date, the conversation so far, the user's turn, and every memory a search found, grouped by type.\n"
        + "For each memory mN, answer this question: "
        + string.Format(CultureInfo.InvariantCulture, GatePrompt.Question.Instructions, "mN") + "\n"
        + GatePrompt.Question.True + "\n" + GatePrompt.Question.False + "\n"
        + "Give the likelihood of yes as a whole number from 0 (certainly no) to 100 (certainly yes).\n"
        + "Answer with JSON only: {\"m1\": 90, \"m2\": 5, ...} with one entry for every memory, keyed as given.";

    public async Task<MemoryGateDecision> DecideAsync(MemoryGateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Candidates.Count == 0) return new MemoryGateDecision(new Dictionary<string, double>(StringComparer.Ordinal), "chat");
        var shortKeys = request.Candidates.Select((_, i) => "m" + (i + 1).ToString(CultureInfo.InvariantCulture)).ToList();
        var options = new ChatOptions { Temperature = 0, MaxOutputTokens = Math.Min(8000, 600 + 24 * request.Candidates.Count) };
        // Sent only when chosen, so the default request leaves reasoning to the provider.
        if (effort is { } chosen) options.Reasoning = new ReasoningOptions { Effort = chosen };
        var response = await chat.GetResponseAsync(Messages(request, shortKeys, meanings), options, cancellationToken).ConfigureAwait(false);
        var scores = Parse(response.Text, shortKeys);
        var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
        for (var i = 0; i < shortKeys.Count; i++) probabilities[request.Candidates[i].Key] = scores[shortKeys[i]];
        return new MemoryGateDecision(probabilities, "chat:" + (response.ModelId ?? "model"));
    }

    /// <summary>The system instructions and the turn, with each memory under its short key, grouped by type in order.</summary>
    internal static IReadOnlyList<ChatMessage> Messages(MemoryGateRequest request, IReadOnlyList<string> shortKeys,
        IReadOnlyDictionary<string, string>? meanings = null)
    {
        var text = new StringBuilder();
        text.Append("Today: ").Append(GatePrompt.Day(request.Now)).Append('\n');
        if (request.Conversation.Count > 0)
        {
            text.Append("\nEarlier in this conversation:\n");
            foreach (var turn in request.Conversation) text.Append(turn.Role).Append(": ").Append(turn.Text).Append('\n');
        }
        text.Append("\nThe user's turn: ").Append(request.Turn).Append("\n\nMemories found:\n");
        foreach (var group in request.Candidates.Select((c, i) => (c, Key: shortKeys[i])).GroupBy(x => x.c.MemoryType))
        {
            var meaning = (meanings ?? GatePrompt.Meanings).TryGetValue(group.Key, out var m) ? m : group.Key;
            text.Append("\nType ").Append(group.Key).Append(": ").Append(meaning).Append('\n');
            foreach (var (candidate, key) in group) text.Append(key).Append(": ").Append(candidate.Text).Append('\n');
        }
        return [new ChatMessage(ChatRole.System, Instructions), new ChatMessage(ChatRole.User, text.ToString())];
    }

    /// <summary>Every key's likelihood as a probability; refuses a reply that misses a key or answers outside 0 to 100.</summary>
    /// <exception cref="InvalidOperationException">The reply is not a whole decision.</exception>
    internal static IReadOnlyDictionary<string, double> Parse(string? text, IReadOnlyList<string> keys)
    {
        var start = text?.IndexOf('{') ?? -1;
        var end = text?.LastIndexOf('}') ?? -1;
        if (text is null || start < 0 || end <= start) throw new InvalidOperationException("The chat gate's reply holds no JSON object.");
        using var doc = Parse(text[start..(end + 1)]);
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (!doc.RootElement.TryGetProperty(key, out var value)) throw new InvalidOperationException($"The chat gate left out {key}.");
            var number = value.ValueKind switch
            {
                JsonValueKind.Number => value.GetDouble(),
                JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) => n,
                _ => double.NaN,
            };
            if (!double.IsFinite(number) || number is < 0 or > 100) throw new InvalidOperationException($"The chat gate answered {key} outside 0 to 100.");
            result[key] = number / 100.0;
        }
        return result;
    }

    private static JsonDocument Parse(string json)
    {
        try { return JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new InvalidOperationException("The chat gate's reply is not valid JSON.", ex); }
    }
}
