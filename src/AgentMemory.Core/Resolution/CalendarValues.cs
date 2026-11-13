using System.Globalization;
using System.Text.RegularExpressions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Core.Services;

namespace AgentMemory.Core.Resolution;

/// <summary>
/// Recognises complete calendar literals before name resolution. Recognition also protects invalid,
/// partial and unsupported temporal values; it never repairs one or supplies a year from the clock.
/// </summary>
internal static class CalendarValues
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);
    private const RegexOptions PatternOptions = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
    private const string Word = @"[a-z\u00c0-\u024f]+";
    private static readonly Regex Iso = new(@"^(?<year>[0-9]{4})-(?<month>[0-9]{2})(?:-(?<day>[0-9]{2}))?$", PatternOptions, MatchTimeout);
    private static readonly Regex Numeric = new(@"^[0-9]{1,4}[-/.][0-9]{1,2}(?:[-/.][0-9]{1,4})?(?:[t ][0-9].*)?$", PatternOptions, MatchTimeout);
    private static readonly Regex MonthFirst = new($@"^(?<month>{Word})\s+(?<day>[0-9]{{1,2}})(?:,?\s+(?<year>[0-9]{{4}}))?$", PatternOptions, MatchTimeout);
    private static readonly Regex DayFirst = new($@"^(?<day>[0-9]{{1,2}})\s+(?:de\s+)?(?<month>{Word})(?:\s+(?:de\s+)?(?<year>[0-9]{{4}}))?$", PatternOptions, MatchTimeout);
    private static readonly Regex MonthYear = new($@"^(?<month>{Word})\s+(?<year>[0-9]{{4}})$", PatternOptions, MatchTimeout);
    private static readonly Regex[] NamedPatterns = [MonthFirst, DayFirst, MonthYear];
    private static readonly Regex WeekdayPrefix = new($@"^(?<weekday>{Word}),?\s+(?<rest>.+)$", PatternOptions, MatchTimeout);
    private const string RangeSeparator = @"(?:[-–—]|to|through|until|and|a|al|hasta|y)";
    private static readonly Regex MonthFirstRange = new($@"^(?:(?:from|between|del|entre)\s+)?(?<month>{Word})\s+(?<start>[0-9]{{1,2}})\s*{RangeSeparator}\s*(?<end>[0-9]{{1,2}})(?:,?\s+(?<year>[0-9]{{4}}))?$", PatternOptions, MatchTimeout);
    private static readonly Regex DayFirstRange = new($@"^(?:(?:from|between|del|entre)\s+)?(?<start>[0-9]{{1,2}})\s*{RangeSeparator}\s*(?<end>[0-9]{{1,2}})\s+(?:de\s+)?(?<month>{Word})(?:\s+(?:de\s+)?(?<year>[0-9]{{4}}))?$", PatternOptions, MatchTimeout);
    private static readonly Regex FullRange = new(@"^(?:(?:from|between|del|entre)\s+)?(?<left>.+?)(?:\s*(?:/|[–—])\s*|\s+-\s+|\s+(?:to|through|until|and|a|al|hasta|y)\s+)(?<right>.+)$", PatternOptions, MatchTimeout);
    private static readonly IReadOnlyDictionary<string, int> Months = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["January"] = 1, ["enero"] = 1, ["February"] = 2, ["febrero"] = 2,
        ["March"] = 3, ["marzo"] = 3, ["April"] = 4, ["abril"] = 4,
        ["May"] = 5, ["mayo"] = 5, ["June"] = 6, ["junio"] = 6,
        ["July"] = 7, ["julio"] = 7, ["August"] = 8, ["agosto"] = 8,
        ["September"] = 9, ["septiembre"] = 9, ["October"] = 10, ["octubre"] = 10,
        ["November"] = 11, ["noviembre"] = 11, ["December"] = 12, ["diciembre"] = 12,
    };
    private static readonly IReadOnlyDictionary<string, DayOfWeek> Weekdays = new Dictionary<string, DayOfWeek>(StringComparer.OrdinalIgnoreCase)
    {
        ["Monday"] = DayOfWeek.Monday, ["lunes"] = DayOfWeek.Monday,
        ["Tuesday"] = DayOfWeek.Tuesday, ["martes"] = DayOfWeek.Tuesday,
        ["Wednesday"] = DayOfWeek.Wednesday, ["miércoles"] = DayOfWeek.Wednesday,
        ["Thursday"] = DayOfWeek.Thursday, ["jueves"] = DayOfWeek.Thursday,
        ["Friday"] = DayOfWeek.Friday, ["viernes"] = DayOfWeek.Friday,
        ["Saturday"] = DayOfWeek.Saturday, ["sábado"] = DayOfWeek.Saturday,
        ["Sunday"] = DayOfWeek.Sunday, ["domingo"] = DayOfWeek.Sunday,
    };

    internal sealed record CalendarValue(int? Year, int Month, int? Day, CalendarValue? End = null)
    {
        public bool IsComplete => Year.HasValue && Day.HasValue && (End is null || End.IsComplete);

        public string CanonicalKey
        {
            get
            {
                if (!IsComplete) throw new InvalidOperationException("An incomplete calendar value has no concrete identity key.");
                var start = FormattableString.Invariant($"{Year:0000}-{Month:00}-{Day:00}");
                return End is null ? "gregorian:" + start : "gregorian-range:" + start + "/"
                    + FormattableString.Invariant($"{End.Year:0000}-{End.Month:00}-{End.Day:00}");
            }
        }
    }

    /// <summary>True means this is not eligible for approximate name identity; null means unresolved.</summary>
    internal static bool TryRead(string? text, string? type, out CalendarValue? value)
        => TryReadCore(text, type, allowRange: true, out value);

    private static bool TryReadCore(string? text, string? type, bool allowRange, out CalendarValue? value)
    {
        value = null;
        var explicitDate = IsExplicitTemporalType(type);
        if (!explicitDate && !string.Equals(type, "OBJECT", StringComparison.OrdinalIgnoreCase)) return false;
        if (text is null) return explicitDate;
        var trimmed = text.AsSpan().Trim();
        if (trimmed.IsEmpty || trimmed.Length > 256) return explicitDate;
        var surface = trimmed.ToString();
        DayOfWeek? weekday = null;
        var prefix = WeekdayPrefix.Match(surface);
        if (prefix.Success && Weekdays.TryGetValue(prefix.Groups["weekday"].Value, out var knownDay))
        {
            weekday = knownDay;
            surface = prefix.Groups["rest"].Value;
        }

        var iso = Iso.Match(surface);
        if (iso.Success)
        {
            value = Create(Number(iso, "year"), Number(iso, "month")!.Value, Number(iso, "day"), weekday);
            return true;
        }
        // This grammar deliberately does not assign a culture to slash dates or treat an instant
        // as a calendar date. They remain protected temporal inputs, not approximate entity names.
        if (Numeric.IsMatch(surface)) return true;

        foreach (var pattern in NamedPatterns)
        {
            var match = pattern.Match(surface);
            if (!match.Success || !Months.TryGetValue(match.Groups["month"].Value, out var month)) continue;
            value = Create(Number(match, "year"), month, Number(match, "day"), weekday);
            return true;
        }
        if (allowRange && TryReadRange(surface, weekday, out value)) return true;
        if (explicitDate && Months.TryGetValue(surface, out var onlyMonth))
            value = new CalendarValue(null, onlyMonth, null);
        return explicitDate;
    }

    private static bool TryReadRange(string surface, DayOfWeek? weekday, out CalendarValue? value)
    {
        value = null;
        foreach (var pattern in new[] { MonthFirstRange, DayFirstRange })
        {
            var match = pattern.Match(surface);
            if (!match.Success || !Months.TryGetValue(match.Groups["month"].Value, out var month)) continue;
            var start = Create(Number(match, "year"), month, Number(match, "start"), weekday);
            var end = Create(Number(match, "year"), month, Number(match, "end"), null);
            value = CreateRange(start, end);
            return true;
        }
        var full = FullRange.Match(surface);
        if (!full.Success || !TryReadCore(full.Groups["left"].Value, "OBJECT", allowRange: false, out var left)
            || !TryReadCore(full.Groups["right"].Value, "OBJECT", allowRange: false, out var right)) return false;
        value = CreateRange(left, right);
        if (value is not null && weekday.HasValue && value.Year.HasValue && value.Day.HasValue &&
            new DateOnly(value.Year.Value, value.Month, value.Day.Value).DayOfWeek != weekday.Value) value = null;
        return true;
    }

    private static CalendarValue? CreateRange(CalendarValue? start, CalendarValue? end)
    {
        if (start is null || end is null || !start.Day.HasValue || !end.Day.HasValue) return null;
        if (start.IsComplete && end.IsComplete &&
            new DateOnly(start.Year!.Value, start.Month, start.Day.Value) > new DateOnly(end.Year!.Value, end.Month, end.Day.Value)) return null;
        if (start.Year == end.Year && start.Month == end.Month && start.Day > end.Day) return null;
        return start with { End = end };
    }

    internal static bool IsExplicitTemporalType(string? type) =>
        string.Equals(type, "DATE", StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, "CALENDAR_DATE", StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, "DATETIME", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "TIME", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "DATE_RANGE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "CALENDAR_INTERVAL", StringComparison.OrdinalIgnoreCase);

    private static int? Number(Match match, string group) => match.Groups[group].Success
        ? int.Parse(match.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture) : null;

    private static CalendarValue? Create(int? year, int month, int? day, DayOfWeek? weekday)
    {
        if (year is < 1 or > 9999 || month is < 1 or > 12 || day is < 1) return null;
        if (day.HasValue && day > DateTime.DaysInMonth(year ?? 2000, month)) return null;
        if (weekday.HasValue && year.HasValue && day.HasValue &&
            new DateOnly(year.Value, month, day.Value).DayOfWeek != weekday.Value) return null;
        return new CalendarValue(year, month, day);
    }

    internal static bool IsCalendar(ExtractedEntity entity) => TryRead(entity.Name, entity.Type, out _);

    internal static bool IsCalendar(Entity entity) => TryRead(entity.Name, entity.Type, out _)
        || (entity.CanonicalName is not null && TryRead(entity.CanonicalName, entity.Type, out _));

    internal static IReadOnlyList<Entity> NameCandidates(IReadOnlyList<Entity> entities) =>
        entities.Where(entity => !IsCalendar(entity)).ToList();

    internal static IReadOnlyList<Entity> EqualValues(
        CalendarValue value, IReadOnlyList<Entity> entities, MemoryScope? scope)
    {
        if (!value.IsComplete) return [];
        return entities.Where(entity => Visible(entity, scope) && HasValue(entity, value))
            .DistinctBy(entity => entity.EntityId).ToList();
    }

    internal static bool HasValue(Entity entity, CalendarValue value)
    {
        if (!TryRead(entity.Name, entity.Type, out var named) || named != value) return false;
        // Aliases never establish a literal's value. A contradictory primary canonical value also
        // makes the record unsuitable until reviewed; do not repair historical records on a read.
        return entity.CanonicalName is null ||
            !TryRead(entity.CanonicalName, entity.Type, out var canonical) || canonical == value;
    }

    // Fact text has no trustworthy entity-type discriminator. Protect a whole literal even when
    // an external resolver returned an incompatible reference type for it.
    internal static bool TryReadLiteralSurface(string surface, string resolvedType, out CalendarValue? value) =>
        TryRead(surface, "OBJECT", out value) || TryRead(surface, resolvedType, out value);

    internal static bool CanLinkSurface(string surface, Entity entity)
    {
        if (!TryReadLiteralSurface(surface, entity.Type, out var value)) return true;
        if (!TryRead(entity.Name, entity.Type, out var primary)) return false;
        if (value?.IsComplete == true) return HasValue(entity, value);
        // Missing precision and opaque/invalid temporal values cannot acquire identity from aliases.
        // An exact primary surface can still represent that same unresolved observation.
        return string.Equals(surface.Trim(), entity.Name.Trim(), StringComparison.OrdinalIgnoreCase)
            && value == primary
            && (entity.CanonicalName is null || string.Equals(entity.CanonicalName.Trim(), entity.Name.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static bool Visible(Entity entity, MemoryScope? scope) => scope?.OwnerId is null ||
        (SharedScopes.IsSharedOnly(scope) ? entity.OwnerId is null :
            string.Equals(entity.OwnerId, scope.OwnerId, StringComparison.Ordinal) ||
            (scope.IncludeShared && entity.OwnerId is null));
}
