using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AgentMemory.Decisions;

/// <summary>The one answer prompt every system shares: the question, the retrieved sources by id, an answer that cites them.</summary>
internal sealed class DecisionAnswerer(IChatClient chat, string today)
{
    internal const string Instructions =
        "You answer questions about the decisions recorded in the sources you are given. Each source has an id in brackets. "
        + "Answer only from the sources; a source's date and status tell you when it was decided and whether a later decision replaced it. "
        + "Return JSON: {\"abstain\": boolean, \"answer\": string, \"decision_sources\": [ids]}. "
        + "decision_sources are the id(s) of the source(s) that state THE decision that answers the question: for what is in force, "
        + "the decision in force at the time asked (today, unless the question names a date); for what replaced something, the "
        + "replacing decision; for why, the source that gives the reason for the decision in force. Do not list sources you only "
        + "mention for context. If the sources do not decide the question, set abstain to true and decision_sources to []. "
        + "Keep the answer to at most two sentences.";

    internal async Task<(bool Abstained, IReadOnlyList<string> Cited, string Answer)> AnswerAsync(
        string question, IReadOnlyList<DecisionChunk> sources, CancellationToken cancellationToken)
    {
        var user = new StringBuilder().Append("Today is ").Append(today).Append(".\nQuestion: ").Append(question).Append("\n\nSources:\n");
        foreach (var source in sources) user.Append('[').Append(source.Id).Append("] ").Append(source.Text).Append("\n\n");
        var response = await chat.GetResponseAsync(
            [new ChatMessage(ChatRole.System, Instructions), new ChatMessage(ChatRole.User, user.ToString())],
            // Stage 2: at the provider's default reasoning one call thought for 19,510 tokens (267 s). Low effort and a ceiling,
            // the same for every system; a truncated reply parses as an abstention, which is graded, not hidden.
            new ChatOptions
            {
                ResponseFormat = ChatResponseFormat.Json,
                Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low },
                MaxOutputTokens = 4096,
            }, cancellationToken).ConfigureAwait(false);
        return Parse(response.Text, sources.Select(source => source.Id).ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>The model's JSON; a citation of an id it was not shown is dropped, and unreadable JSON is an abstention.</summary>
    internal static (bool Abstained, IReadOnlyList<string> Cited, string Answer) Parse(string? text, IReadOnlySet<string> shown)
    {
        try
        {
            var start = text?.IndexOf('{') ?? -1;
            var end = text?.LastIndexOf('}') ?? -1;
            if (start < 0 || end <= start) return (true, [], text ?? string.Empty);
            using var json = JsonDocument.Parse(text![start..(end + 1)]);
            var root = json.RootElement;
            var abstained = root.TryGetProperty("abstain", out var a) && a.ValueKind == JsonValueKind.True;
            var answer = root.TryGetProperty("answer", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
            IReadOnlyList<string> cited = root.TryGetProperty("decision_sources", out var s) && s.ValueKind == JsonValueKind.Array
                ? [.. s.EnumerateArray().Select(e => e.ToString().Trim('[', ']', ' ')).Where(shown.Contains).Distinct(StringComparer.Ordinal)]
                : [];
            return (abstained, cited, answer);
        }
        catch (JsonException)
        {
            return (true, [], text ?? string.Empty);
        }
    }
}
