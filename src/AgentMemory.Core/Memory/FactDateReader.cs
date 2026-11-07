using System.Globalization;
using System.Text.RegularExpressions;
using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Core.Memory;

/// <summary>
/// Reads the dates a fact's own words state ("on Friday, December 11", "from December 9 to December 13", "starting
/// December 4") into its date fields, keeping the words as they are. For <c>LlmExtractionOptions.DatesFromText</c>.
/// </summary>
/// <remarks>
/// <para>
/// The store-aware writer keeps a date in the fact's text, as it was said ("Bruna | is leading the go-live of | Acme's
/// billing system on Friday, December 11"). That reads well in a prompt and is found by meaning, but nothing that
/// selects by date can see it: an upcoming section, a question about a period, an overlap. This reader gives the fact
/// the same date as a field too, so it is both said and dated.
/// </para>
/// <para>
/// <b>Only what is stated, and only when it is unambiguous.</b> An explicit month and day (or an ISO date), or a range
/// of them; never "tomorrow" or "next week", never a bare weekday. A fact that names two dates without saying they are a
/// range is left alone. A yearless date is the occurrence nearest to when it was said (a weekday named with it decides
/// between years). Fields the writer already set are never overwritten. No model call.
/// </para>
/// </remarks>
internal static class FactDateReader
{
    private const RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;

    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);

    private const string Month =
        "january|february|march|april|may|june|july|august|september|october|november|december|"
        + "jan|feb|mar|apr|jun|jul|aug|sep|sept|oct|nov|dec";

    private const string Weekday = @"(?:(?<wd>monday|tuesday|wednesday|thursday|friday|saturday|sunday),?\s+)?";

    /// <summary>One explicit date: "December 11", "Friday, December 11, 2026", "11 December", "the 11th of December", "2026-12-11".</summary>
    private static readonly Regex OneDate = new(
        @"\b" + Weekday + @"(?:(?<m>" + Month + @")\.?\s+(?<d>[0-3]?\d)(?:st|nd|rd|th)?(?![\d:])(?:,?\s+(?<y>(?:19|20)\d{2}))?"
        + @"|(?:the\s+)?(?<d>[0-3]?\d)(?:st|nd|rd|th)?\s+(?:of\s+)?(?<m>" + Month + @")\.?(?:,?\s+(?<y>(?:19|20)\d{2}))?"
        + @"|(?<iy>(?:19|20)\d{2})-(?<im>[01]\d)-(?<id>[0-3]\d))\b",
        Options, Timeout);

    /// <summary>A range: "from X to Y", "between X and Y", "X to Y", "X through Y", "X-Y" with Y possibly only a day.</summary>
    private static readonly Regex Range = new(
        @"\b(?:from\s+|(?<between>between)\s+)?" + Weekday + @"(?<m1>" + Month + @")\.?\s+(?<d1>[0-3]?\d)(?:st|nd|rd|th)?"
        + @"\s*(?<sep>to|until|till|through|thru|and|-|–)\s*(?:" + Weekday.Replace("wd", "wd2", StringComparison.Ordinal) + @"(?<m2>" + Month + @")\.?\s+)?"
        + @"(?<d2>[0-3]?\d)(?:st|nd|rd|th)?(?![\d:])(?:,?\s+(?<y>(?:19|20)\d{2}))?\b",
        Options, Timeout);

    /// <summary>"starting December 4", "from December 4" (with no end), "as of", "since", "beginning".</summary>
    private static readonly Regex Starts = new(
        @"\b(?:starting|starts|start|from|as\s+of|since|beginning|begins)\s+(?:on\s+)?$", Options, Timeout);

    /// <summary>"until December 13", "till", "through", "ending", "ends". Not "by": a deadline is a day, not an end.</summary>
    private static readonly Regex Ends = new(
        @"\b(?:until|till|through|ending|ends)\s+(?:on\s+)?$", Options, Timeout);

    /// <summary>The fact with the dates its words state added to the fields still empty.</summary>
    /// <param name="fact">The fact as the writer produced it.</param>
    /// <param name="said">When it was said: a yearless date resolves to the occurrence nearest to it.</param>
    internal static ExtractedFact Apply(ExtractedFact fact, DateTimeOffset said)
    {
        if (fact.OccurredOn is not null || fact.ValidFrom is not null) return fact;
        var text = $"{fact.Subject} {fact.Predicate} {fact.Object}";
        var read = Read(text, said);
        if (read is null) return fact;

        var (occurred, from, until) = read.Value;
        return fact with
        {
            OccurredOn = occurred,
            OccurredOnPrecision = occurred is null ? fact.OccurredOnPrecision : DatePrecision.Day,
            ValidFrom = from,
            ValidFromPrecision = from is null ? fact.ValidFromPrecision : DatePrecision.Day,
            ValidUntil = fact.ValidUntil ?? until,
            ValidUntilPrecision = fact.ValidUntil is null && until is not null ? DatePrecision.Day : fact.ValidUntilPrecision,
        };
    }

    /// <summary>
    /// The dates <paramref name="text"/> states: an event's day, or a stay's start and end; null when it states none,
    /// or more than one without saying they are a range.
    /// </summary>
    internal static (DateTimeOffset? OccurredOn, DateTimeOffset? ValidFrom, DateTimeOffset? ValidUntil)? Read(string text, DateTimeOffset said)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // "and" joins a range only after "between": "the training on December 3 and the freeze on December 4" is two dates.
        if (Range.Match(text) is { Success: true } range
            && (!range.Groups["sep"].Value.Equals("and", StringComparison.OrdinalIgnoreCase) || range.Groups["between"].Success))
        {
            var m1 = MonthNumber(range.Groups["m1"].Value);
            var m2 = range.Groups["m2"].Success ? MonthNumber(range.Groups["m2"].Value) : m1;
            var d1 = int.Parse(range.Groups["d1"].Value, CultureInfo.InvariantCulture);
            var d2 = int.Parse(range.Groups["d2"].Value, CultureInfo.InvariantCulture);
            int? year = range.Groups["y"].Success ? int.Parse(range.Groups["y"].Value, CultureInfo.InvariantCulture) : null;
            var start = Resolve(m1, d1, year, range.Groups["wd"].Success ? range.Groups["wd"].Value : null, said);
            if (start is not null && Valid(start.Value.Year + (m2 < m1 ? 1 : 0), m2, d2))
            {
                var end = new DateTimeOffset(start.Value.Year + (m2 < m1 ? 1 : 0), m2, d2, 23, 59, 59, TimeSpan.Zero);
                if (end > start.Value) return (null, start, end);
            }
        }

        var dates = OneDate.Matches(text);
        if (dates.Count != 1) return null;
        var match = dates[0];
        DateTimeOffset? day;
        if (match.Groups["iy"].Success)
        {
            var y = int.Parse(match.Groups["iy"].Value, CultureInfo.InvariantCulture);
            var mo = int.Parse(match.Groups["im"].Value, CultureInfo.InvariantCulture);
            var d = int.Parse(match.Groups["id"].Value, CultureInfo.InvariantCulture);
            day = Valid(y, mo, d) ? new DateTimeOffset(y, mo, d, 0, 0, 0, TimeSpan.Zero) : null;
        }
        else
        {
            int? year = match.Groups["y"].Success ? int.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture) : null;
            day = Resolve(MonthNumber(match.Groups["m"].Value), int.Parse(match.Groups["d"].Value, CultureInfo.InvariantCulture), year,
                match.Groups["wd"].Success ? match.Groups["wd"].Value : null, said);
        }

        if (day is null) return null;
        var before = text[..match.Index];
        if (Starts.IsMatch(before)) return (null, day, null);
        if (Ends.IsMatch(before)) return (null, null, day.Value.AddDays(1).AddSeconds(-1));
        return (day, null, null);
    }

    /// <summary>A month and day in the year given, or the one nearest to <paramref name="said"/> (matching a named weekday).</summary>
    private static DateTimeOffset? Resolve(int month, int day, int? year, string? weekday, DateTimeOffset said)
    {
        if (month == 0) return null;
        if (year is { } named)
            return Valid(named, month, day) ? new DateTimeOffset(named, month, day, 0, 0, 0, TimeSpan.Zero) : null;

        DateTimeOffset? nearest = null;
        foreach (var candidate in new[] { said.Year - 1, said.Year, said.Year + 1 })
        {
            if (!Valid(candidate, month, day)) continue;
            var date = new DateTimeOffset(candidate, month, day, 0, 0, 0, TimeSpan.Zero);
            if (weekday is not null && !date.DayOfWeek.ToString().Equals(weekday, StringComparison.OrdinalIgnoreCase)) continue;
            if (nearest is null || (date - said).Duration() < (nearest.Value - said).Duration()) nearest = date;
        }

        return nearest;
    }

    private static bool Valid(int year, int month, int day) =>
        year is >= 1900 and <= 2199 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month);

    private static int MonthNumber(string name) => name.ToLowerInvariant() switch
    {
        "january" or "jan" => 1, "february" or "feb" => 2, "march" or "mar" => 3, "april" or "apr" => 4,
        "may" => 5, "june" or "jun" => 6, "july" or "jul" => 7, "august" or "aug" => 8,
        "september" or "sep" or "sept" => 9, "october" or "oct" => 10, "november" or "nov" => 11,
        "december" or "dec" => 12,
        _ => 0,
    };
}
