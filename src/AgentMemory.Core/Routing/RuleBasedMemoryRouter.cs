using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Core.Routing;

/// <summary>
/// The core memory router (PLAN 40.56), from rules that are data: a statement reads nothing; a question or a request
/// reads facts, and each rule whose pattern matches adds its kind. Deterministic, no model, a few regular expressions per
/// turn. Tuned on the dev split of the frozen routing set (40.61) only.
/// </summary>
/// <remarks>
/// Fails open: a rule that times out chooses its kind (reading more is the safe error for a restrict-only router).
/// </remarks>
public sealed class RuleBasedMemoryRouter : IMemoryRouter
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);

    /// <summary>The kinds always read when anything is: most answers are facts, and shared knowledge is stored as facts.</summary>
    private const string Always = "facts-always";

    /// <summary>
    /// A request with no question mark ("Suggest a dinner for tonight", "tell me about my brother"). Anything else without
    /// a question mark is a statement: a telling or a correction, which extraction handles and the reply does not recall.
    /// </summary>
    private static readonly Regex Request = new(
        @"^\W*(?:please\s+)?(?:tell|remind|suggest|recommend|give|show|list|find|help|plan|describe|explain|summari[sz]e)\b"
        + @"|\b(?:can|could|would|will)\s+you\b|\bany\s+(?:ideas|suggestions|tips|recommendations)\b|\bwhat\s+(?:should|would|could)\s+i\b",
        Options, Timeout);

    /// <summary>The rules that ship with the library.</summary>
    public static IReadOnlyList<MemoryRoutingRule> DefaultRules { get; } =
    [
        // People and how they connect: a relation of the speaker, a "who", someone named, a possessive.
        new(MemoryRoute.Graph, "graph-relation",
            @"\bmy\s+(?:\w+\s+)?(?:brother|sister|siblings?|mum|mom|mother|dad|father|parents?|partner|wife|husband|boyfriend|girlfriend|fianc[eé]e?|sons?|daughters?|kids?|children|child|baby|manager|boss|colleagues?|co-?workers?|team|friends?|neighbou?rs?|teacher|tutor|doctor|dentist|landlord|dog|cat|pets?|family|grand\w+|uncle|aunt|cousins?|nephew|niece)\b"),
        new(MemoryRoute.Graph, "graph-who", @"\bwho(?:m|se)?\b"),
        new(MemoryRoute.Graph, "graph-about-me", @"\b(?:remember|know)\s+about\s+(?:me|my)\b|\btell\s+me\s+about\b"),
        // A name: a capitalised word that does not open the sentence and is not a month, a day or "I". Case-sensitive.
        new(MemoryRoute.Graph, "graph-name",
            @"(?-i)(?<![.!?]\s)(?<!^)(?<!^\W*)\b(?!(?:I|January|February|March|April|May|June|July|August|September|October|November|December|Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday)\b)[A-Z][a-z]+\b"),
        new(MemoryRoute.Graph, "graph-possessive",
            @"(?-i)\b(?!(?:What|Who|Where|When|How|That|It|Let|There|Here|She|He)'s\b)[A-Z][a-z]+'s\b"),

        // Tastes and requests a taste answers.
        new(MemoryRoute.Preferences, "preferences-taste",
            @"\b(?:favou?rite|prefer\w*|likes?|like\s+to|love|enjoy|taste|recommend\w*|suggest\w*|ideas?|offer|dinner|lunch|breakfast|eat|drink|coffee|tea|music|listen|film|movie|watch)\b"),
        new(MemoryRoute.Preferences, "preferences-plan", @"\b(?:free\s+(?:day|evening|night|weekend|saturday|sunday)|weekend\s+plans?|plan\s+ideas)\b"),
        new(MemoryRoute.Preferences, "preferences-style", @"\b(?:answers?|replies|responses?)\s+(?:to\s+\w+\s+)?(?:be\s+)?(?:written|formatted|phrased)\b|\bhow\s+should\s+(?:you|answers?)\b"),
        new(MemoryRoute.Preferences, "preferences-about-me", @"\b(?:remember|know)\s+about\s+me\b"),

        // An episode of this conversation.
        new(MemoryRoute.Messages, "messages-episode",
            @"\b(?:last\s+time|happened|turn(?:ed)?\s+out|how\s+did\s+.{1,40}\s+go|we\s+(?:were\s+)?(?:planning|talking|discussing|saying)|we\s+talked|you\s+(?:said|told|suggested)|i\s+(?:told|said|mentioned)|earlier|across\s+our\s+chats|with\s+me\s+(?:in|at|on))\b"),
    ];

    private readonly (MemoryRoutingRule Rule, Regex Pattern)[] _rules;

    /// <summary>
    /// Added rules, compiled once per list (a module set holds one list for its life, so a turn compiles nothing). Weakly
    /// keyed: a replaced module set's list is collected with it.
    /// </summary>
    private readonly ConditionalWeakTable<IReadOnlyList<MemoryRoutingRule>, (MemoryRoutingRule Rule, Regex Pattern)[]> _added = new();

    /// <summary>Builds the router from <paramref name="options"/>: the default rules unless turned off, then the added ones.</summary>
    public RuleBasedMemoryRouter(MemoryRoutingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _rules = [.. (options.UseDefaultRules ? DefaultRules : []).Concat(options.Rules)
            .Select(rule => (rule, new Regex(rule.Pattern, Options, Timeout)))];
    }

    /// <inheritdoc />
    public MemoryRoute Route(string question) => Route(question, []);

    /// <inheritdoc />
    public MemoryRoute Route(string question, IReadOnlyList<MemoryRoutingRule> additionalRules)
    {
        ArgumentNullException.ThrowIfNull(additionalRules);
        var added = additionalRules.Count == 0 ? [] : _added.GetValue(additionalRules, Compile);
        var text = question?.Trim() ?? string.Empty;
        if (text.Length == 0) return Skip("an empty turn");
        if (!text.Contains('?', StringComparison.Ordinal) && !Matches(Request, text))
            return Skip("a statement: no question and no request");

        var chosen = new Dictionary<string, string>(StringComparer.Ordinal) { [MemoryRoute.Facts] = Always };
        foreach (var (rule, pattern) in _rules.Concat(added))
        {
            if (!chosen.ContainsKey(rule.Kind) && Matches(pattern, text)) chosen[rule.Kind] = rule.Name;
        }
        return new MemoryRoute
        {
            Recall = true,
            Kinds = [.. MemoryRoute.CoreKinds.Where(chosen.ContainsKey),
                     .. chosen.Keys.Where(k => !MemoryRoute.CoreKinds.Contains(k)).Order(StringComparer.Ordinal)],
            ChosenBy = chosen,
        };
    }

    private static MemoryRoute Skip(string reason) => new() { Recall = false, SkipReason = reason };

    /// <inheritdoc />
    public IReadOnlyList<string> Check(IReadOnlyList<MemoryRoutingRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var problems = new List<string>();
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Kind) || string.IsNullOrWhiteSpace(rule.Name))
            {
                problems.Add($"routing rule '{rule.Name}': a rule needs a kind and a name");
                continue;
            }
            try
            {
                _ = Untrusted(rule.Pattern);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                problems.Add($"routing rule '{rule.Name}': {ex.Message}");
            }
        }
        return problems;
    }

    /// <summary>
    /// Added rules come from outside the host's configuration, so they run without backtracking (spec 0.8 §6.2: a
    /// repeating group over untrusted text is a review-blocking defect). Constructs that need backtracking (look-arounds,
    /// back-references) do not compile, and <see cref="Check"/> names the rule.
    /// </summary>
    private static Regex Untrusted(string pattern) => new(pattern, Options | RegexOptions.NonBacktracking, Timeout);

    private static (MemoryRoutingRule Rule, Regex Pattern)[] Compile(IReadOnlyList<MemoryRoutingRule> rules) =>
        [.. rules.Select(rule => (rule, Untrusted(rule.Pattern)))];

    private static bool Matches(Regex pattern, string text)
    {
        try
        {
            return pattern.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }
}
