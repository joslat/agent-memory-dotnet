using AgentMemory.Abstractions.Options;

namespace AgentMemory.Core.Services;

/// <summary>
/// Decides whether a query is worth fanning out per memory type (Proposal M, 30.10).
/// </summary>
/// <remarks>
/// <para>
/// <b>Token scan only — no regular expressions.</b> This follows the <c>TrivialTurnDetector</c>
/// precedent rather than convenience: a regex needs a timeout, a timed-out regex is a second source
/// of nondeterminism, and a gate that fires differently on a slow machine voids every run measured
/// through it.
/// </para>
/// <para>
/// <b>The rules are OR-only, and that shape is deliberate.</b> A false fire is raise-only: it costs
/// one derivation and at most <see cref="RecallFanOutOptions.MaxSubQueries"/> embeddings, and it can
/// never remove a row from recall. So the gate is tuned to admit rather than to be precise, because
/// the two error directions have very different prices.
/// </para>
/// <para>
/// <b>Known confound, recorded rather than hidden.</b> The MAF provider joins every user message into
/// one <c>Query</c>, so on a long live thread this gate sees a paragraph rather than a question and
/// over-fires relative to the single-question corpus it was tuned on. That is a raise-only cost, but
/// a measurement taken through that provider is not comparable to one taken through a direct caller.
/// </para>
/// </remarks>
internal static class RecallFanOutPlanner
{
    /// <summary>Interrogative lemmas for rule C1. Multi-word forms are matched as phrases.</summary>
    private static readonly string[] MultiWordWhLemmas =
        ["how many", "how much", "how often", "how long"];

    private static readonly HashSet<string> SingleWordWhLemmas =
        new(StringComparer.OrdinalIgnoreCase) { "what", "when", "where", "who", "which" };

    /// <summary>Coordinating joiners for rule C2, matched with their surrounding spacing.</summary>
    private static readonly string[] Joiners = [" and ", " as well as ", " & ", "; "];

    private static readonly string[] TemporalWords =
        [
            "january", "february", "march", "april", "may", "june", "july", "august",
            "september", "october", "november", "december",
        ];

    /// <summary>Relative date phrases for rule D4. "previous" is deliberately absent — see remarks.</summary>
    private static readonly string[] RelativeDatePhrases =
        ["last week", "last month", "last quarter", "last year"];

    private static readonly char[] WordSeparators =
        [' ', '\t', '\r', '\n', '.', ',', '!', '?', ':', ';'];

    /// <summary>Leading words that disqualify a capitalised run from counting as an entity mention.</summary>
    /// <remarks>
    /// A sentence-initial "The" or "What" is capitalised by grammar, not because it names anything, and
    /// counting those would let ordinary prose satisfy the entity rules.
    /// </remarks>
    private static readonly HashSet<string> CapitalisedStopwords =
        new(StringComparer.Ordinal)
        {
            "The", "A", "An", "What", "When", "Where", "Who", "Which", "How", "Why",
            "Did", "Do", "Does", "Is", "Are", "Was", "Were", "I", "My", "In", "On", "At", "And",
        };

    /// <summary>Evaluates the deterministic pre-retrieval rules.</summary>
    /// <returns>Whether to fan out, and which named rules fired.</returns>
    internal static (bool Fired, string[] Rules) EvaluateGate(string? query, RecallFanOutOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(query)) return (false, []);

        var rules = new List<string>(4);
        var lowered = query.ToLowerInvariant();

        if (CountDistinctWhLemmas(lowered) >= 2) rules.Add("C1");
        if (HasConjoinedEntities(query, lowered)) rules.Add("C2");

        var entityMentions = CountDistinctEntityMentions(query);
        if (entityMentions >= options.MinDistinctEntityMentions) rules.Add("E3");

        if (HasDateMix(lowered)) rules.Add("D4");

        return (rules.Count > 0, [.. rules]);
    }

    /// <summary>
    /// Rule C1 — two or more distinct interrogatives.
    /// </summary>
    /// <remarks>
    /// Never <c>?</c>-counting. Question-mark counting was measured wrong on 3 of 5 cases: a paper
    /// title ending in "?" is not a question, and two clauses joined by "and" carry one mark between
    /// them.
    /// </remarks>
    private static int CountDistinctWhLemmas(string lowered)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);

        foreach (var phrase in MultiWordWhLemmas)
        {
            if (lowered.Contains(phrase, StringComparison.Ordinal)) found.Add(phrase);
        }

        foreach (var token in lowered.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (SingleWordWhLemmas.Contains(token)) found.Add(token);
        }

        // "how many" already counted as a phrase; a bare "how" is not itself an interrogative here.
        return found.Count;
    }

    /// <summary>Rule C2 — a coordinating joiner with a distinct entity mention on each side.</summary>
    private static bool HasConjoinedEntities(string query, string lowered)
    {
        foreach (var joiner in Joiners)
        {
            var index = lowered.IndexOf(joiner, StringComparison.Ordinal);
            if (index <= 0) continue;

            var left = query[..index];
            var right = query[(index + joiner.Length)..];
            if (CountDistinctEntityMentions(left) >= 1 && CountDistinctEntityMentions(right) >= 1)
                return true;
        }

        return false;
    }

    /// <summary>Punctuation that ends an entity run. Whitespace continues one; these do not.</summary>
    /// <remarks>
    /// Splitting on punctuation FIRST is load-bearing. Treating a comma as an ordinary separator makes
    /// "Acme Corp, Initech" a single four-token run and therefore one mention rather than two, which
    /// under-counts exactly the enumerations rule E3 exists to catch. This was a real defect, found by
    /// the E3 fixture rather than by reading the code.
    /// </remarks>
    private static readonly char[] RunBreakingPunctuation = ['.', ',', '!', '?', ':', ';'];

    /// <summary>Rule E3 — distinct capitalised multi-character token runs, stopword-leading excluded.</summary>
    /// <remarks>
    /// A known and accepted imprecision: a sentence-initial imperative verb ("Compare Acme Corp…") is
    /// capitalised and not a stopword, so it merges into the first mention. Chasing that with an
    /// ever-growing verb list would trade a bounded, explainable heuristic for an unbounded one, and
    /// the error is raise-only — it can make E3 fire slightly early, never suppress it.
    /// </remarks>
    private static int CountDistinctEntityMentions(string text)
    {
        var mentions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var segment in text.Split(RunBreakingPunctuation, StringSplitOptions.RemoveEmptyEntries))
        {
            var run = new List<string>();
            foreach (var token in segment.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                var capitalised = token.Length > 1 && char.IsUpper(token[0]);
                if (capitalised && !CapitalisedStopwords.Contains(token))
                {
                    run.Add(token);
                    continue;
                }

                if (run.Count > 0) { mentions.Add(string.Join(' ', run)); run.Clear(); }
            }

            if (run.Count > 0) mentions.Add(string.Join(' ', run));
        }

        return mentions.Count;
    }

    /// <summary>
    /// Rule D4 — a date-bearing token together with a non-temporal interrogative.
    /// </summary>
    /// <remarks>
    /// Never keyed on "previous". That word was measured as an 82.1% false-positive surface for
    /// episodic queries: "my previous answer" is about conversation, not about time.
    /// </remarks>
    private static bool HasDateMix(string lowered)
    {
        var hasDate = false;

        foreach (var month in TemporalWords)
        {
            if (lowered.Contains(month, StringComparison.Ordinal)) { hasDate = true; break; }
        }

        if (!hasDate)
        {
            foreach (var phrase in RelativeDatePhrases)
            {
                if (lowered.Contains(phrase, StringComparison.Ordinal)) { hasDate = true; break; }
            }
        }

        if (!hasDate) hasDate = HasFourDigitYear(lowered);
        if (!hasDate) return false;

        // A non-temporal interrogative: "when" alone is a purely temporal ask and does not mix.
        foreach (var token in lowered.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (SingleWordWhLemmas.Contains(token) &&
                !string.Equals(token, "when", StringComparison.Ordinal))
            {
                return true;
            }
        }

        foreach (var phrase in MultiWordWhLemmas)
        {
            if (lowered.Contains(phrase, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    private static bool HasFourDigitYear(string lowered)
    {
        foreach (var token in lowered.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length != 4) continue;

            var allDigits = true;
            foreach (var character in token)
            {
                if (!char.IsAsciiDigit(character)) { allDigits = false; break; }
            }

            // 1000-2999: a four-digit run that is plausibly a year rather than a quantity.
            if (allDigits && (token[0] == '1' || token[0] == '2')) return true;
        }

        return false;
    }
}
