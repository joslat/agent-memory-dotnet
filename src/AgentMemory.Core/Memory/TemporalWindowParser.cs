using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentMemory.Core.Memory;

/// <summary>What a question's time window is: two instants, or an event to anchor one on.</summary>
/// <remarks>
/// Either <see cref="Start"/> and <see cref="End"/> are set, or <see cref="Anchor"/> is: "between December 7 and 13"
/// names its window, "go-live week" names an event whose date the store has to supply.
/// </remarks>
internal sealed record TemporalWindowQuery
{
    /// <summary>The window's first instant (inclusive), when the question names it.</summary>
    public DateTimeOffset? Start { get; init; }

    /// <summary>The window's last instant (inclusive), when the question names it.</summary>
    public DateTimeOffset? End { get; init; }

    /// <summary>The event the window is anchored on ("go-live" in "go-live week"), when it is.</summary>
    public string? Anchor { get; init; }

    /// <summary>How much time around the anchor the window covers.</summary>
    public TemporalWindowSpan Span { get; init; } = TemporalWindowSpan.Exact;

    /// <summary>The window around <paramref name="anchorDate"/> that <see cref="Span"/> asks for.</summary>
    public (DateTimeOffset Start, DateTimeOffset End) Around(DateTimeOffset anchorDate) => Span switch
    {
        TemporalWindowSpan.Day => TemporalWindowParser.Day(anchorDate),
        _ => TemporalWindowParser.Week(anchorDate),
    };
}

/// <summary>How much time an anchored window covers around its event.</summary>
internal enum TemporalWindowSpan
{
    /// <summary>The window is given by its two instants.</summary>
    Exact,

    /// <summary>The event's day.</summary>
    Day,

    /// <summary>The Monday-to-Sunday week that holds the event.</summary>
    Week,
}

/// <summary>
/// Finds the period of time a question is about, for questions that name a window rather than a past instant:
/// "between December 7 and December 13", "next week", "the next 10 days", "on December 11", "go-live week".
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TemporalQueryParser"/> resolves a question about the past to one instant and recalls what was true then.
/// A question about a period, and above all a period still ahead ("what do I have between December 7 and 13?"), is a
/// different question: it asks for everything dated inside the period, recalled against now. That is what this parser
/// returns; the recall that answers it is <c>RecallOptions.TimeWindows</c>.
/// </para>
/// <para>
/// <b>The same asymmetry as its sibling.</b> A missed window costs nothing: the question recalls exactly as it does
/// today. A wrong one adds dated facts the person did not ask about. Every pattern needs an explicit marker: a range
/// says "between" or "from", a single day says "on", a relative week says "this" or "next", and an anchored window
/// needs a word directly before "week" or "day" that is not a time word ("go-live week", never "this week" or
/// "work week").
/// </para>
/// <para>
/// No model call and no culture-dependent parsing. A month and day without a year mean the occurrence nearest to now.
/// </para>
/// </remarks>
internal static class TemporalWindowParser
{
    private const RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;

    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);

    private const string Months =
        "january|february|march|april|may|june|july|august|september|october|november|december|"
        + "jan|feb|mar|apr|jun|jul|aug|sep|sept|oct|nov|dec";

    private const string DayNumber = @"(?<{0}>[0-3]?\d)(?:st|nd|rd|th)?";

    /// <summary>
    /// "between December 7 and (December) 13", "from Dec 7 to/until/through 13", with an optional year after the second
    /// day; also "between 7 and 13 December".
    /// </summary>
    private static readonly Regex MonthFirstRange = new(
        @"\b(?:between|from)\s+(?<m1>" + Months + @")\.?\s+" + string.Format(CultureInfo.InvariantCulture, DayNumber, "d1")
        + @"\s+(?:and|to|until|till|through|thru|-)\s+(?:(?<m2>" + Months + @")\.?\s+)?"
        + string.Format(CultureInfo.InvariantCulture, DayNumber, "d2")
        + @"(?:,?\s+(?<year>(?:19|20)\d{2}))?\b",
        Options, Timeout);

    private static readonly Regex DayFirstRange = new(
        @"\b(?:between|from)\s+(?:the\s+)?" + string.Format(CultureInfo.InvariantCulture, DayNumber, "d1")
        + @"(?:\s+(?:of\s+)?(?<m1>" + Months + @")\.?)?"
        + @"\s+(?:and|to|until|till|through|thru|-)\s+(?:the\s+)?" + string.Format(CultureInfo.InvariantCulture, DayNumber, "d2")
        + @"(?:\s+of)?\s+(?<m2>" + Months + @")\.?(?:,?\s+(?<year>(?:19|20)\d{2}))?\b",
        Options, Timeout);

    /// <summary>"on December 11", "on Dec 11, 2026", "on the 11th of December", "on 11 December".</summary>
    private static readonly Regex OnDay = new(
        @"\bon\s+(?:(?:mon|tues|wednes|thurs|fri|satur|sun)day,?\s+)?"
        + @"(?:(?<m1>" + Months + @")\.?\s+" + string.Format(CultureInfo.InvariantCulture, DayNumber, "d1")
        + @"|(?:the\s+)?" + string.Format(CultureInfo.InvariantCulture, DayNumber, "d2") + @"(?:\s+of)?\s+(?<m2>" + Months + @")\.?)"
        + @"(?:,?\s+(?<year>(?:19|20)\d{2}))?\b",
        Options, Timeout);

    /// <summary>"this week", "next week", "this month", "next month", "this weekend", "next weekend".</summary>
    private static readonly Regex RelativePeriod = new(
        @"\b(?<which>this|next|the\s+coming)\s+(?<unit>weekend|week|month)\b",
        Options, Timeout);

    /// <summary>"the next 10 days", "in the next two weeks", "over the coming 5 days".</summary>
    private static readonly Regex NextUnits = new(
        @"\b(?:next|coming)\s+(?<n>\d{1,3}|two|three|four|five|six|seven|eight|nine|ten|fourteen|thirty)\s+(?<unit>days?|weeks?)\b",
        Options, Timeout);

    private static readonly Regex TodayOrTomorrow = new(@"\b(?<which>today|tonight|tomorrow)\b", Options, Timeout);

    /// <summary>"go-live week", "launch day", "the week of the go-live", "the day of the wedding".</summary>
    /// <remarks>
    /// One word names the anchor. "Plan my prep for go-live week" must anchor on "go-live", not on "prep for go-live";
    /// a fact about "the go-live of the billing system" still contains the word, so one word finds it.
    /// </remarks>
    private static readonly Regex AnchorBefore = new(
        @"(?<anchor>\p{L}[\p{L}\p{N}'’\-]*)\s+(?<span>week|day)\b(?!\s*-)",
        Options, Timeout);

    /// <remarks>
    /// "The last day of the sprint" is the day a sprint ends on, not the sprint's own day: an ordinal before "week" or
    /// "day" makes it an ordinary period.
    /// </remarks>
    private static readonly Regex AnchorAfter = new(
        @"(?<!\b(?:last|first|final|next|same|every|each|second|third|whole|entire|this|that)\s+)\b(?<span>week|day)\s+of\s+(?:the\s+|my\s+|our\s+|their\s+|her\s+|his\s+)?(?<anchor>\p{L}[\p{L}\p{N}'’\-]*)",
        Options, Timeout);

    /// <summary>Words before "week" or "day" that make it an ordinary period, not an event's.</summary>
    private static readonly HashSet<string> NotAnAnchor = new(StringComparer.OrdinalIgnoreCase)
    {
        "this", "next", "last", "that", "the", "a", "an", "every", "each", "per", "one", "same", "whole", "entire",
        "any", "some", "which", "what", "first", "second", "third", "final", "previous", "following", "coming",
        "work", "working", "business", "school", "week", "day", "full", "half", "long", "short", "busy", "good",
        "bad", "rest", "two", "three", "four", "five", "six", "seven", "my", "our", "your", "his", "her", "their",
        "today", "yesterday", "tomorrow", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday",
        "sunday", "per-day", "all", "mid", "end", "start", "beginning", "early", "late",
    };

    /// <summary>
    /// The window this question asks about, or <see langword="null"/> when it names none.
    /// </summary>
    /// <param name="query">The user's turn.</param>
    /// <param name="now">The reference instant; relative windows and yearless dates resolve against it.</param>
    internal static TemporalWindowQuery? Resolve(string? query, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        if (MonthFirstRange.Match(query) is { Success: true } range)
        {
            var month1 = MonthNumber(range.Groups["m1"].Value);
            var month2 = range.Groups["m2"].Success ? MonthNumber(range.Groups["m2"].Value) : month1;
            return Range(range, month1, month2, now);
        }

        if (DayFirstRange.Match(query) is { Success: true } dayFirst)
        {
            var month2 = MonthNumber(dayFirst.Groups["m2"].Value);
            var month1 = dayFirst.Groups["m1"].Success ? MonthNumber(dayFirst.Groups["m1"].Value) : month2;
            return Range(dayFirst, month1, month2, now);
        }

        if (OnDay.Match(query) is { Success: true } on)
        {
            var month = MonthNumber(on.Groups[on.Groups["m1"].Success ? "m1" : "m2"].Value);
            var day = int.Parse(on.Groups[on.Groups["d1"].Success ? "d1" : "d2"].Value, CultureInfo.InvariantCulture);
            var date = Date(on, month, day, now);
            if (date is null) return null;
            var (start, end) = Day(date.Value);
            return new TemporalWindowQuery { Start = start, End = end };
        }

        if (RelativePeriod.Match(query) is { Success: true } relative)
        {
            var next = relative.Groups["which"].Value.StartsWith("next", StringComparison.OrdinalIgnoreCase)
                       || relative.Groups["which"].Value.EndsWith("coming", StringComparison.OrdinalIgnoreCase);
            var thisOrNext = next ? 1 : 0;
            switch (relative.Groups["unit"].Value.ToLowerInvariant())
            {
                case "week":
                {
                    var (start, end) = Week(now.AddDays(7 * thisOrNext));
                    return new TemporalWindowQuery { Start = thisOrNext == 0 ? Max(start, StartOfDay(now)) : start, End = end };
                }
                case "weekend":
                {
                    var (_, sunday) = Week(now.AddDays(7 * thisOrNext));
                    var saturday = StartOfDay(sunday).AddDays(-1);
                    return new TemporalWindowQuery { Start = Max(saturday, StartOfDay(now)), End = sunday };
                }
                default:
                {
                    var first = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset).AddMonths(thisOrNext);
                    var last = first.AddMonths(1).AddTicks(-1);
                    return new TemporalWindowQuery { Start = thisOrNext == 0 ? StartOfDay(now) : first, End = last };
                }
            }
        }

        if (NextUnits.Match(query) is { Success: true } nextUnits)
        {
            var count = Count(nextUnits.Groups["n"].Value);
            var days = nextUnits.Groups["unit"].Value.StartsWith("week", StringComparison.OrdinalIgnoreCase) ? 7 * count : count;
            if (days is <= 0 or > 366) return null;
            return new TemporalWindowQuery { Start = StartOfDay(now), End = StartOfDay(now).AddDays(days + 1).AddTicks(-1) };
        }

        if (TodayOrTomorrow.Match(query) is { Success: true } today)
        {
            var date = today.Groups["which"].Value.Equals("tomorrow", StringComparison.OrdinalIgnoreCase) ? now.AddDays(1) : now;
            var (start, end) = Day(date);
            return new TemporalWindowQuery { Start = start, End = end };
        }

        foreach (var anchor in new[] { AnchorAfter.Match(query), AnchorBefore.Match(query) })
        {
            for (var match = anchor; match.Success; match = match.NextMatch())
            {
                var word = match.Groups["anchor"].Value.TrimEnd('\'', '’', '-');
                if (word.Length < 3 || NotAnAnchor.Contains(word) || MonthNumber(word) != 0) continue;
                var span = match.Groups["span"].Value.Equals("day", StringComparison.OrdinalIgnoreCase)
                    ? TemporalWindowSpan.Day
                    : TemporalWindowSpan.Week;
                return new TemporalWindowQuery { Anchor = word, Span = span };
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a question looks ahead: it plans or prepares, schedules, asks when something is, names what is coming,
    /// or names a window still to come. <c>RecallOptions.Upcoming</c> fires only for such a question by default.
    /// </summary>
    internal static bool LooksAhead(string? query) =>
        !string.IsNullOrWhiteSpace(query) && AheadQuestion.IsMatch(query);

    private static readonly Regex AheadQuestion = new(
        @"\b(plan|plans|planning|prep|prepare|preparing|schedule|scheduled|calendar|agenda|upcoming|coming\s+up|ahead|"
        + @"soon|next|tomorrow|tonight|this\s+(?:week|weekend|month)|will|going\s+to|booked|due|remind|reminders?|"
        + @"when\s+(?:is|are|do|does|will)|when['\u2019]s|what\s+day)\b",
        Options, Timeout);

    /// <summary>The Monday-to-Sunday week that holds <paramref name="instant"/>, in its own offset.</summary>
    internal static (DateTimeOffset Start, DateTimeOffset End) Week(DateTimeOffset instant)
    {
        var day = StartOfDay(instant);
        var sinceMonday = ((int)day.DayOfWeek + 6) % 7;
        var monday = day.AddDays(-sinceMonday);
        return (monday, monday.AddDays(7).AddTicks(-1));
    }

    /// <summary>The day that holds <paramref name="instant"/>, in its own offset.</summary>
    internal static (DateTimeOffset Start, DateTimeOffset End) Day(DateTimeOffset instant)
    {
        var day = StartOfDay(instant);
        return (day, day.AddDays(1).AddTicks(-1));
    }

    private static TemporalWindowQuery? Range(Match match, int month1, int month2, DateTimeOffset now)
    {
        if (month1 == 0 || month2 == 0) return null;
        var day1 = int.Parse(match.Groups["d1"].Value, CultureInfo.InvariantCulture);
        var day2 = int.Parse(match.Groups["d2"].Value, CultureInfo.InvariantCulture);
        var crossesNewYear = month2 < month1 ? 1 : 0;

        // A year written after the range is its END's: "from December 28 to January 3, 2027" starts in December 2026.
        DateTimeOffset? start;
        if (match.Groups["year"].Success)
        {
            var startYear = int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture) - crossesNewYear;
            start = Valid(startYear, month1, day1) ? new DateTimeOffset(startYear, month1, day1, 0, 0, 0, now.Offset) : null;
        }
        else
        {
            start = Date(match, month1, day1, now);
        }

        if (start is null) return null;

        // The second date is the first one on or after the start: "December 28 to January 3" crosses the year.
        var year = start.Value.Year + crossesNewYear;
        if (!Valid(year, month2, day2)) return null;
        var end = new DateTimeOffset(year, month2, day2, 0, 0, 0, now.Offset);
        if (end < start.Value) return null;
        return new TemporalWindowQuery { Start = start.Value, End = end.AddDays(1).AddTicks(-1) };
    }

    /// <summary>A month and day, in the year the match names or, without one, in the year nearest to now.</summary>
    private static DateTimeOffset? Date(Match match, int month, int day, DateTimeOffset now)
    {
        if (month == 0) return null;
        if (match.Groups["year"].Success)
        {
            var named = int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture);
            return Valid(named, month, day) ? new DateTimeOffset(named, month, day, 0, 0, 0, now.Offset) : null;
        }

        DateTimeOffset? nearest = null;
        foreach (var year in new[] { now.Year - 1, now.Year, now.Year + 1 })
        {
            if (!Valid(year, month, day)) continue;
            var candidate = new DateTimeOffset(year, month, day, 0, 0, 0, now.Offset);
            if (nearest is null || (candidate - now).Duration() < (nearest.Value - now).Duration()) nearest = candidate;
        }

        return nearest;
    }

    private static bool Valid(int year, int month, int day) =>
        year is >= 1900 and <= 2199 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month);

    private static DateTimeOffset StartOfDay(DateTimeOffset instant) =>
        new(instant.Year, instant.Month, instant.Day, 0, 0, 0, instant.Offset);

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static int Count(string value) => value.ToLowerInvariant() switch
    {
        "two" => 2, "three" => 3, "four" => 4, "five" => 5, "six" => 6, "seven" => 7, "eight" => 8, "nine" => 9,
        "ten" => 10, "fourteen" => 14, "thirty" => 30,
        _ => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0,
    };

    private static int MonthNumber(string name) => name.ToLowerInvariant() switch
    {
        "january" or "jan" => 1, "february" or "feb" => 2, "march" or "mar" => 3, "april" or "apr" => 4,
        "may" => 5, "june" or "jun" => 6, "july" or "jul" => 7, "august" or "aug" => 8,
        "september" or "sep" or "sept" => 9, "october" or "oct" => 10, "november" or "nov" => 11,
        "december" or "dec" => 12,
        _ => 0,
    };
}
