using System.Text.RegularExpressions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Core.Memory;

namespace AgentMemory.Core.Extraction;

/// <summary>
/// 36.4. What a correction the extractor marked (<see cref="ExtractedFact.Replaces"/>,
/// <see cref="ExtractedPreference.Replaces"/>) closes: the live memory that stated the replaced value.
/// </summary>
/// <remarks>
/// <para>
/// Supersession by cardinality needs a single-valued relation, and many changes of mind have none: a preference
/// ("Arcade Fire, not Radiohead"), a plan ("the full marathon instead of the half"). The conversation says what is
/// replaced; the extractor writes it down (<c>"replaces": "Radiohead"</c>); this decides which stored memory that
/// names, conservatively, because closing the wrong one hides a true memory.
/// </para>
/// <list type="bullet">
/// <item>A <b>fact</b> of the same subject <i>mentions</i> the value when one of its object and the value contains the
/// other as whole words ("half marathon" for "the half marathon in April", and the other way round). Among the live
/// facts that mention it, those stating the winner's relation are closed. If none does, the one fact that mentions it
/// is closed, and only if it is the only one: "works for a wind energy firm, replaces the shipping company" closes
/// "works at a shipping company" and leaves "left the shipping company" (a true event) alone, while "is running the
/// full marathon, replaces the half marathon" closes the one "is training for half marathon".</item>
/// <item>A <b>preference</b> of the same category is closed when its text contains the value as whole words.</item>
/// </list>
/// </remarks>
internal static class Corrections
{
    /// <summary>The live facts, among <paramref name="candidates"/>, that a correction by <paramref name="winner"/> closes.</summary>
    /// <param name="candidates">The subject's facts.</param>
    /// <param name="winner">The correction.</param>
    /// <param name="replaced">The value it replaces, as said.</param>
    /// <param name="statedNow">
    /// Facts of the same extraction as the correction. Only the same relation's closes one of them: under another
    /// relation it is something else said now ("Oslo now, not Copenhagen; Copenhagen is still my favourite city"),
    /// and the fallback exists for what was stored before under another phrasing.
    /// </param>
    internal static IReadOnlyList<Fact> Closed(
        IEnumerable<Fact> candidates, Fact winner, string replaced, IReadOnlySet<string>? statedNow = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(winner);
        var value = Value(replaced);
        if (value.Length == 0) return [];
        var mentioning = candidates
            .Where(candidate => candidate.InvalidatedAtUtc is null && candidate.FactId != winner.FactId)
            .Where(candidate => Mentions(candidate.Object, value))
            .ToList();
        var relationKeys = MemoryRelationCardinality.ReplacedKeys(winner.Predicate);
        var sameRelation = mentioning
            .Where(candidate => relationKeys.Contains(MemoryTripleCanonicalizer.Canonical(candidate.Predicate), StringComparer.Ordinal))
            .ToList();
        // 38.6 review. A correction names the old value in every form it was stored: the role held at the old employer
        // and the other phrasings of a changed plan close WITH the same-relation match, not only when there is none
        // ("works at | Contoso" and "works as | designer at Contoso" both end when Fabrikam replaces Contoso).
        var free = mentioning.Where(candidate => statedNow?.Contains(candidate.FactId) != true).ToList();
        List<Fact> roles = IsEmployment(winner.Predicate) ? [.. free.Where(candidate => HeldAt(candidate.Object, value))] : [];
        List<Fact> phrasings = IsPlan(winner.Predicate)
            ? [.. free.Where(candidate => IsPlan(candidate.Predicate) && Unambiguous(candidate) && DatesAgree(candidate.Object, value))]
            : [];
        if (AmbiguousDates([.. sameRelation, .. phrasings], value)) phrasings = [];
        if (sameRelation.Count > 0) return [.. sameRelation.Concat(roles).Concat(phrasings).DistinctBy(fact => fact.FactId)];
        // 38.6 (held-out show 11, 2026-10-01). "works as | designer at Contoso" is the job at Contoso: a role held AT the
        // value. A new employer that replaces "Contoso" ends it too; with one word in common it was left as a possible
        // coincidence, and Contoso stayed the employer beside the new one. Narrow on purpose: the correction must be an
        // employment and the stored object must END in "at/for/with <value>". "Google in London" is untouched.
        if (roles.Count > 0) return roles;
        // Another relation only when the mention is unambiguous and not a coincidence of one word: the object IS the
        // value, or the two share at least two words ("half marathon" for "the half marathon in April"), never "6 kg"
        // for "6" nor "London" for "Google in London".
        if (free.Count == 1 && Unambiguous(free[0]))
            return free;
        // 38.6 (K-19, show 04 run 14). One plan stored under two phrasings ("is training for | half marathon", "is running
        // | half marathon in April"): with two mentions the rule above closed neither, and the old plan stayed live beside
        // the new one. A changed plan closes every phrasing of the old one, but only when the correction and each of them
        // is a plan, each names the value unambiguously and no date says they are two plans; anything else ("bought shoes
        // for the half marathon", a half in April and one in October) keeps the conservative answer.
        if (free.Count > 1 && IsPlan(winner.Predicate) &&
            free.All(candidate => IsPlan(candidate.Predicate) && Unambiguous(candidate) && DatesAgree(candidate.Object, value)) &&
            !AmbiguousDates(free, value))
            return free;
        return EllipticalPlan(candidates, winner, value, statedNow);

        bool Unambiguous(Fact candidate) =>
            Value(candidate.Object) == value || Math.Min(WordCount(candidate.Object), WordCount(value)) >= 2;
    }

    private static bool IsEmployment(string? predicate)
    {
        if (MemoryRelationCardinality.Relation(predicate) == "works at") return true;
        var canonical = MemoryTripleCanonicalizer.Canonical(predicate);
        return canonical is "joined" or "started at" or "started working at" or "moved to work at";
    }

    /// <summary>Whether <paramref name="text"/> is a role held at <paramref name="value"/>: "designer at contoso".</summary>
    private static bool HeldAt(string text, string value)
    {
        var canonical = Value(text);
        return canonical != value &&
               (canonical.EndsWith($" at {value}", StringComparison.Ordinal) ||
                canonical.EndsWith($" for {value}", StringComparison.Ordinal) ||
                canonical.EndsWith($" with {value}", StringComparison.Ordinal));
    }

    /// <summary>
    /// 38.6. A changed plan that names the old one by ellipsis: "the full marathon in May instead of <b>the half</b> in
    /// April" for a stored "is training for | half marathon". Two pieces of evidence, both required: every word the
    /// correction names (dates aside) is in the stored plan ("half"), and the stored plan shares a word with the new one
    /// ("marathon"). Plans only, on both sides; "a trip to Lisbon in April" shares nothing with "full marathon" and stays.
    /// </summary>
    private static IReadOnlyList<Fact> EllipticalPlan(
        IEnumerable<Fact> candidates, Fact winner, string value, IReadOnlySet<string>? statedNow)
    {
        if (!IsPlan(winner.Predicate)) return [];
        var named = ContentWords(value).Where(word => !IsDateWord(word)).ToHashSet(StringComparer.Ordinal);
        var winnerWords = ContentWords(winner.Object).Where(word => !IsDateWord(word)).ToHashSet(StringComparer.Ordinal);
        if (named.Count == 0 || winnerWords.Count == 0) return [];
        // 38.6 review: the date the correction names picks the plan ("the April trip" is not the Rome trip in August);
        // with no date named, two plans of different dates are two plans, and neither is closed.
        List<Fact> matched = [.. candidates.Where(candidate =>
            candidate.InvalidatedAtUtc is null && candidate.FactId != winner.FactId &&
            statedNow?.Contains(candidate.FactId) != true && IsPlan(candidate.Predicate) &&
            named.IsSubsetOf(ContentWords(candidate.Object)) &&
            ContentWords(candidate.Object).Overlaps(winnerWords) &&
            DatesAgree(candidate.Object, value))];
        return AmbiguousDates(matched, value) ? [] : matched;
    }

    private static readonly Dictionary<string, string> CalendarNames = new(StringComparer.Ordinal)
    {
        ["january"] = "january", ["jan"] = "january", ["february"] = "february", ["feb"] = "february",
        ["march"] = "march", ["mar"] = "march", ["april"] = "april", ["apr"] = "april", ["may"] = "may",
        ["june"] = "june", ["jun"] = "june", ["july"] = "july", ["jul"] = "july", ["august"] = "august", ["aug"] = "august",
        ["september"] = "september", ["sep"] = "september", ["sept"] = "september", ["october"] = "october",
        ["oct"] = "october", ["november"] = "november", ["nov"] = "november", ["december"] = "december", ["dec"] = "december",
        ["spring"] = "spring", ["summer"] = "summer", ["autumn"] = "autumn", ["fall"] = "autumn", ["winter"] = "winter",
    };

    /// <summary>The months and seasons a text names, by their full name.</summary>
    private static HashSet<string> Months(string text) =>
        ContentWords(text).Select(word => CalendarNames.GetValueOrDefault(word)).OfType<string>().ToHashSet(StringComparer.Ordinal);

    /// <summary>Whether a stored plan can be the one a correction names: no month on either side, or one in common.</summary>
    private static bool DatesAgree(string text, string value)
    {
        var named = Months(value);
        var stored = Months(text);
        return named.Count == 0 || stored.Count == 0 || named.Overlaps(stored);
    }

    /// <summary>Whether, with no month named, the facts carry two different months: two plans, not two phrasings of one.</summary>
    private static bool AmbiguousDates(IEnumerable<Fact> facts, string value) =>
        Months(value).Count == 0 &&
        facts.Select(fact => Months(fact.Object)).Where(months => months.Count > 0)
            .Select(months => string.Join(',', months.Order(StringComparer.Ordinal))).Distinct(StringComparer.Ordinal).Count() > 1;

    private static readonly HashSet<string> DateWords = new(StringComparer.Ordinal)
    {
        "january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november",
        "december", "jan", "feb", "mar", "apr", "jun", "jul", "aug", "sep", "sept", "oct", "nov", "dec",
        "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday",
        "next", "last", "this", "week", "month", "year", "spring", "summer", "autumn", "fall", "winter",
    };

    /// <summary>
    /// 38.6. The event a plan names, without its date: "Half marathon in April 2027" is "Half marathon". Case is kept (facts
    /// are looked up by their exact subject); a leading article goes. Empty when nothing is left.
    /// </summary>
    internal static string Undated(string? text)
    {
        var words = (text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count > 0 && words[0].ToLowerInvariant() is "the" or "a" or "an" or "my" or "our") words.RemoveAt(0);
        while (words.Count > 0 && (IsDateToken(words[^1]) || words[^1].ToLowerInvariant() is "in" or "on" or "at" or "by" or "of"))
            words.RemoveAt(words.Count - 1);
        return string.Join(' ', words);
    }

    /// <summary>
    /// 38.6. Whether a value is only a date: "2027-04", "April 2027", "on 12 May", "2027". A bare number is not a date
    /// ("3000" entries, "21" km): it needs a month, a weekday, a season, a year or a date's separator.
    /// </summary>
    internal static bool IsDateOnly(string? text)
    {
        var words = (text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Trim(',', '.', ';').ToLowerInvariant())
            .Where(word => word.Length > 0 && word is not ("in" or "on" or "at" or "by" or "of" or "the"))
            .ToList();
        return words.Count > 0 && words.All(IsDateToken) && words.Any(word =>
            CalendarNames.ContainsKey(word) || DateWords.Contains(word) && !RelativeWords.Contains(word) ||
            (word.Length == 4 && word.All(char.IsAsciiDigit) && int.Parse(word, System.Globalization.CultureInfo.InvariantCulture) is >= 1900 and <= 2100) ||
            (word.Any(char.IsAsciiDigit) && word.Any(c => c is '-' or '/')));
    }

    private static readonly HashSet<string> RelativeWords = new(StringComparer.Ordinal) { "next", "last", "this", "week", "month", "year" };

    private static bool IsDateToken(string word)
    {
        var bare = word.Trim(',', '.', ';').ToLowerInvariant();
        return bare.Length > 0 && (IsDateWord(bare) || bare.All(c => char.IsAsciiDigit(c) || c is '-' or '/' or ':'));
    }

    private static bool IsDateWord(string word) =>
        DateWords.Contains(word) || (word.Length == 4 && word.All(char.IsAsciiDigit));

    /// <summary>
    /// 38.6. Whether a predicate states an intention or a plan in progress ("plans to run", "is training for", "is
    /// going to", "will"): what a changed plan replaces.
    /// </summary>
    internal static bool IsPlan(string? predicate)
    {
        var words = MemoryTripleCanonicalizer.CanonicalValue(predicate).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 0 && words[0] is "is" or "am" or "are" or "was" or "user") words.RemoveAt(0);
        if (words.Count == 0) return false;
        return words[0] is "plans" or "plan" or "planning" or "planned" or "training" or "trains" or "preparing" or "will"
                   or "intends" or "intending" or "aims" or "aiming" or "registered" or "signed" or "running" or "doing"
               || (words.Count > 1 && words[0] == "going" && words[1] == "to");
    }

    private static int WordCount(string text) =>
        Value(text).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>Whether <paramref name="text"/> and <paramref name="name"/> name the same thing: one holds the other as whole words.</summary>
    internal static bool NamesEither(string text, string name) => Mentions(text, Value(name));

    /// <summary>Whether one of <paramref name="text"/> and <paramref name="value"/> contains the other as whole words.</summary>
    private static bool Mentions(string text, string value)
    {
        var canonical = Value(text);
        return canonical.Length > 0 && (ContainsWords(canonical, value) || ContainsWords(value, canonical));
    }

    private static readonly HashSet<string> FunctionWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "my", "our", "his", "her", "their", "in", "on", "at", "of", "to", "for", "with", "by", "from", "and",
    };

    private static HashSet<string> ContentWords(string text) =>
        Value(text).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => !FunctionWords.Contains(word))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// 36.4. The single-valued relation a preference states, when it states one: "Favourite band is Arcade Fire" states
    /// <c>favourite band</c>, the same relation the <c>favourite</c> prefix makes single-valued for facts. Null otherwise.
    /// </summary>
    internal static string? SingleValuedRelation(string preferenceText)
    {
        var text = MemoryTripleCanonicalizer.CanonicalValue(preferenceText);
        var at = text.IndexOf(" is ", StringComparison.Ordinal);
        return at <= 0 ? null : MemoryRelationCardinality.Relation(text[..at]);
    }

    /// <summary>The value a preference stating a single-valued relation states ("Favourite band is Radiohead": radiohead).</summary>
    internal static string? StatedValue(string preferenceText)
    {
        var text = MemoryTripleCanonicalizer.CanonicalValue(preferenceText);
        var at = text.IndexOf(" is ", StringComparison.Ordinal);
        return at <= 0 || SingleValuedRelation(preferenceText) is null ? null : Value(text[(at + 4)..]);
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> states another value of the single-valued relation <paramref name="winner"/>
    /// states: "Favourite band is Radiohead" for "Favourite band is Arcade Fire". Found in simulated conversations: the
    /// model did not always mark "not Radiohead" as a correction, and both favourites stayed live.
    /// </summary>
    internal static bool Replaces(Preference winner, Preference candidate)
    {
        ArgumentNullException.ThrowIfNull(winner);
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.InvalidatedAtUtc is not null || candidate.PreferenceId == winner.PreferenceId) return false;
        var relation = SingleValuedRelation(winner.PreferenceText);
        return relation is not null &&
               string.Equals(SingleValuedRelation(candidate.PreferenceText), relation, StringComparison.Ordinal) &&
               !string.Equals(Value(candidate.PreferenceText), Value(winner.PreferenceText), StringComparison.Ordinal);
    }

    internal static bool Closes(Preference candidate, Preference winner, string replaced)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(winner);
        if (candidate.InvalidatedAtUtc is not null || candidate.PreferenceId == winner.PreferenceId) return false;
        return Names(candidate.PreferenceText, replaced);
    }

    /// <summary>Whether <paramref name="text"/> names the replaced value as whole words.</summary>
    internal static bool Names(string text, string replaced)
    {
        var value = Value(replaced);
        return value.Length > 0 && ContainsWords(text, value);
    }

    /// <summary>The replaced value as compared: canonical, without a leading article or possessive.</summary>
    internal static string Value(string? replaced)
    {
        var value = MemoryTripleCanonicalizer.CanonicalValue(replaced);
        foreach (var lead in new[] { "the ", "a ", "an ", "my ", "our " })
        {
            if (value.StartsWith(lead, StringComparison.Ordinal) && value.Length > lead.Length)
                return value[lead.Length..];
        }
        return value;
    }

    private static bool ContainsWords(string text, string value) =>
        Regex.IsMatch(
            MemoryTripleCanonicalizer.CanonicalValue(text),
            @"(?<![\p{L}\p{N}])" + Regex.Escape(value) + @"(?![\p{L}\p{N}])",
            RegexOptions.CultureInvariant);
}
