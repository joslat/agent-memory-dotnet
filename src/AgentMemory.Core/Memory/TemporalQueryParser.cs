using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentMemory.Core.Memory;

/// <summary>
/// Finds the point in time a question is asking about, so bitemporal recall can be reached from a
/// natural-language turn (R4).
/// </summary>
/// <remarks>
/// <para>
/// <c>RecallAsOfAsync(validAsOf, systemAsOf)</c> already exists and nothing in an ordinary conversation
/// can reach it: "what did I think back in March?" recalls against <i>now</i>, exactly like every other
/// question. Without this the bitemporal read path is a capability nothing can ask for.
/// </para>
/// <para>
/// <b>Precision over recall, deliberately and asymmetrically.</b> A missed expression costs nothing —
/// the turn recalls against now, which is today's behaviour. A false positive silently filters memory
/// to a window the user never asked about, and the answer that comes back looks perfectly ordinary.
/// Every pattern here therefore requires an explicit temporal marker rather than inferring one:
/// <c>"in March"</c> is temporal, a bare <c>"March"</c> may be a surname; <c>"last week"</c> is
/// temporal, <c>"the last item"</c> is not.
/// </para>
/// <para>
/// No model call and no culture-dependent parsing: this runs on every turn when enabled, and a
/// resolver that cost a completion would be more expensive than the recall it modifies.
/// </para>
/// </remarks>
internal static class TemporalQueryParser
{
    private const RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;

    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// <c>last|past N unit(s) ago</c> and the bare <c>last week/month/quarter/year</c>.
    /// </summary>
    /// <remarks>
    /// "last" must be followed by a time unit. Alone it is far more often ordinal — "the last item",
    /// "last one wins" — and treating those as temporal would rewrite the recall window of ordinary
    /// questions.
    /// </remarks>
    private static readonly Regex RelativeUnits = new(
        @"\b(?:(?<n>\d{1,3})\s+)?(?<unit>day|days|week|weeks|month|months|quarter|quarters|year|years)\s+ago\b"
        + @"|\blast\s+(?<lunit>week|month|quarter|year)\b"
        + @"|\bpast\s+(?<n2>\d{1,3})\s+(?<punit>day|days|week|weeks|month|months|year|years)\b",
        Options, Timeout);

    /// <summary>A month name behind a preposition, optionally with a year.</summary>
    /// <remarks>
    /// The preposition is required. "March" is a common surname and "may" is a verb in nearly every
    /// sentence that contains it; "in May" is unambiguous where "may" is not.
    /// </remarks>
    private static readonly Regex MonthName = new(
        @"\b(?:in|back\s+in|during|around)\s+(?<month>january|february|march|april|may|june|july|august|"
        + @"september|october|november|december)(?:\s+(?<year>(?:19|20)\d{2}))?\b",
        Options, Timeout);

    /// <summary>A four-digit year behind a preposition.</summary>
    /// <remarks>
    /// Bounded to 19xx/20xx and requiring a preposition, so "in 2024" resolves and "in 2024 units of
    /// stock" — where the number is a quantity — does not accidentally become a date.
    /// </remarks>
    private static readonly Regex Year = new(
        @"\b(?:in|back\s+in|during)\s+(?<year>(?:19|20)\d{2})\b(?!\s*(?:units?|items?|records?|rows?))",
        Options, Timeout);

    private static readonly Regex Yesterday = new(@"\byesterday\b", Options, Timeout);

    /// <summary>
    /// The instant this question is asking about, or <see langword="null"/> when it asks about now.
    /// </summary>
    /// <param name="query">The user's turn.</param>
    /// <param name="now">The reference instant; relative expressions resolve against it.</param>
    /// <remarks>
    /// <para>
    /// Null is the overwhelmingly common answer and the safe one: it means "recall as the system
    /// behaves today". Only an explicit past reference produces an instant.
    /// </para>
    /// <para>
    /// <b>A future reference returns null</b> rather than a future instant. As-of recall reconstructs
    /// what was known at a past moment; asking it about next month would return everything, which is
    /// indistinguishable from ordinary recall except that it silently ignored the user's question.
    /// </para>
    /// </remarks>
    internal static DateTimeOffset? Resolve(string? query, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        var resolved = ResolveCore(query, now);

        // Never ahead of the reference instant. A parse that lands in the future is either a
        // misreading or a question about the future, and neither should narrow a recall window.
        return resolved is { } instant && instant < now ? instant : null;
    }

    private static DateTimeOffset? ResolveCore(string query, DateTimeOffset now)
    {
        if (Yesterday.IsMatch(query)) return now.AddDays(-1);

        var relative = RelativeUnits.Match(query);
        if (relative.Success)
        {
            var count = relative.Groups["n"].Success
                ? int.Parse(relative.Groups["n"].Value, CultureInfo.InvariantCulture)
                : relative.Groups["n2"].Success
                    ? int.Parse(relative.Groups["n2"].Value, CultureInfo.InvariantCulture)
                    : 1;

            var unit = relative.Groups["unit"].Success ? relative.Groups["unit"].Value
                : relative.Groups["lunit"].Success ? relative.Groups["lunit"].Value
                : relative.Groups["punit"].Value;

            return unit.ToLowerInvariant().TrimEnd('s') switch
            {
                "day" => now.AddDays(-count),
                "week" => now.AddDays(-7 * count),
                "month" => now.AddMonths(-count),
                "quarter" => now.AddMonths(-3 * count),
                "year" => now.AddYears(-count),
                _ => null,
            };
        }

        var month = MonthName.Match(query);
        if (month.Success)
        {
            var monthNumber = MonthNumber(month.Groups["month"].Value);
            if (monthNumber == 0) return null;

            var year = month.Groups["year"].Success
                ? int.Parse(month.Groups["year"].Value, CultureInfo.InvariantCulture)
                : now.Month >= monthNumber ? now.Year : now.Year - 1;

            // The END of the named month: "what did I think back in March" means anything known by
            // the close of March, not by its first instant -- which would exclude the whole month.
            return EndOfMonth(year, monthNumber, now.Offset);
        }

        var year4 = Year.Match(query);
        if (year4.Success)
        {
            var value = int.Parse(year4.Groups["year"].Value, CultureInfo.InvariantCulture);
            return EndOfMonth(value, 12, now.Offset);
        }

        return null;
    }

    private static DateTimeOffset EndOfMonth(int year, int month, TimeSpan offset) =>
        new DateTimeOffset(year, month, DateTime.DaysInMonth(year, month), 23, 59, 59, offset);

    private static int MonthNumber(string name) => name.ToLowerInvariant() switch
    {
        "january" => 1, "february" => 2, "march" => 3, "april" => 4,
        "may" => 5, "june" => 6, "july" => 7, "august" => 8,
        "september" => 9, "october" => 10, "november" => 11, "december" => 12,
        _ => 0,
    };
}
