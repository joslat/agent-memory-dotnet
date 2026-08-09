using System.Text.RegularExpressions;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.AgentFramework.Recall;

/// <summary>
/// A lightweight, deterministic <see cref="IAutomaticRecallPolicy"/> that adapts automatic recall to the
/// shape of the current turn without any additional model call (#88):
/// <list type="bullet">
/// <item>Skips recall entirely for an empty or greeting/acknowledgement-only turn.</item>
/// <item>Favours <see cref="RankingIntent.Latest"/> for recency-oriented phrasing ("latest", "current").</item>
/// <item>Favours <see cref="RankingIntent.Analog"/> and includes reasoning traces for precedent-oriented
/// phrasing ("similar case", "how did we solve this before").</item>
/// <item>Includes reasoning traces for task/troubleshooting-oriented phrasing.</item>
/// <item>Otherwise falls back to <see cref="AutomaticRecallCategories.Default"/> with no intent override.</item>
/// </list>
/// GraphRAG is never excluded by this policy -- it only ever reasons about reasoning traces/intent, so a
/// host's independently configured <c>EnableGraphRag</c>/<c>MaxGraphRagItems</c>/<c>BlendMode</c> continue
/// to govern GraphRAG exactly as before, unaffected by opting into this policy.
/// Not required reading for a host that only wants the default behavior -- opt in explicitly via
/// <c>services.AddScoped&lt;IAutomaticRecallPolicy, HeuristicAutomaticRecallPolicy&gt;()</c>.
/// </summary>
public sealed class HeuristicAutomaticRecallPolicy : IAutomaticRecallPolicy
{
    // Baseline always retains GraphRag (unlike AutomaticRecallCategories.Default) -- this policy has no
    // rule for GraphRAG at all, so it must never silently disable a host's independently configured
    // GraphRAG retrieval.
    private const AutomaticRecallCategories BaselineCategories =
        AutomaticRecallCategories.Default | AutomaticRecallCategories.GraphRag;

    private static readonly HashSet<string> GreetingPhrases = new(StringComparer.OrdinalIgnoreCase)
    {
        "hi", "hello", "hey", "thanks", "thank you", "ok", "okay", "sure", "got it",
        "yes", "no", "yep", "nope", "sounds good", "great", "cool", "noted", "understood"
    };

    private static readonly char[] WordSeparators = { ' ', '\t', '\r', '\n', '.', ',', '!', '?' };

    private static readonly Regex RecencyOriented = new(
        @"\b(latest|current|most recent|right now|these days|nowadays)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PrecedentOriented = new(
        @"\b(similar (case|incident|problem|issue)|previous (incident|issue|case)|precedent|find a similar|how did we (solve|handle|fix) this before)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TaskOriented = new(
        @"\b(debug|troubleshoot|workflow|steps to|how do i|walk me through|implement|error|bug|incident|root cause)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Questions that need a relation returned whole rather than its top-K most similar members.
    /// </summary>
    /// <remarks>
    /// Both of the failures this track spent the most effort on open with "how many", which is what
    /// makes a lexical route sufficient here instead of a model call. Compiled and anchored on word
    /// boundaries; no repeating group, so it cannot backtrack catastrophically the way an earlier
    /// greeting pattern in this file did.
    /// </remarks>
    private static readonly Regex AggregationOriented = new(
        @"\b(how many|how much|count|total|list all|all the|every|number of)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <inheritdoc/>
    public ValueTask<AutomaticRecallDecision> DecideAsync(
        AutomaticRecallContext context, CancellationToken cancellationToken = default)
    {
        var query = string.Join(" ", context.Messages.Select(m => m.Text ?? string.Empty)).Trim();

        if (string.IsNullOrWhiteSpace(query) || IsGreetingOrAcknowledgementOnly(query))
            return ValueTask.FromResult(AutomaticRecallDecision.Skip);

        var categories = BaselineCategories;
        RankingIntent? intent = null;

        if (PrecedentOriented.IsMatch(query))
        {
            intent = RankingIntent.Analog;
            categories |= AutomaticRecallCategories.ReasoningTraces;
        }
        else if (RecencyOriented.IsMatch(query))
        {
            intent = RankingIntent.Latest;
        }

        if (TaskOriented.IsMatch(query))
            categories |= AutomaticRecallCategories.ReasoningTraces;

        return ValueTask.FromResult(new AutomaticRecallDecision
        {
            ShouldRecall = true,
            Categories = categories,
            Intent = intent,
            // G5's aggregation route. Deterministic and free, so it can run on every turn, and it
            // catches the shape top-K structurally cannot answer.
            RequiresRelationCompleteness = AggregationOriented.IsMatch(query)
        });
    }

    /// <summary>
    /// Whether every word/short-phrase in <paramref name="text"/> is a greeting or acknowledgement, e.g.
    /// "hi", "ok, got it.", "sounds good". Deliberately NOT regex-based: an earlier version used a single
    /// repeating-group pattern (<c>^(\s*(hi|...)\s*)+$</c>) that exhibited catastrophic backtracking --
    /// verified experimentally, a string of ~30 repeated short fragments blocked the calling thread for
    /// multiple seconds. Plain tokenization is linear time and has no such failure mode.
    /// </summary>
    private static bool IsGreetingOrAcknowledgementOnly(string text)
    {
        var words = text.ToLowerInvariant().Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return true;

        var i = 0;
        while (i < words.Length)
        {
            if (i + 1 < words.Length && GreetingPhrases.Contains($"{words[i]} {words[i + 1]}"))
            {
                i += 2;
                continue;
            }

            if (!GreetingPhrases.Contains(words[i]))
                return false;

            i++;
        }

        return true;
    }
}
