using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AgentMemory.Extraction.Llm.Internal;

/// <summary>
/// Reads a model-written validity date into the instant a period <b>starts</b>:
/// <c>"2026"</c> → 2026-01-01T00:00Z, <c>"2026-08"</c> → 2026-08-01T00:00Z.
/// </summary>
internal sealed class PeriodStartDateConverter() : PeriodDateConverter(PeriodEdge.Start, "valid_from");

/// <summary>
/// Reads a model-written validity date into the last instant of the period it names:
/// <c>"2026"</c> → 2026-12-31T23:59:59.9999999Z, <c>"2026-08"</c> → 2026-08-31T23:59:59.9999999Z,
/// <c>"2026-08-15"</c> → 2026-08-15T23:59:59.9999999Z ("valid until Friday" includes Friday).
/// </summary>
internal sealed class PeriodEndDateConverter() : PeriodDateConverter(PeriodEdge.End, "valid_until");

internal enum PeriodEdge
{
    Start,
    End,
}

/// <summary>
/// Lenient reader for the validity dates an extraction model writes, replacing System.Text.Json's
/// native <see cref="DateTimeOffset"/> parsing for exactly these two fields.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not the native parser.</b> System.Text.Json accepts only the extended ISO 8601-1:2019
/// profile, and the shortest form it takes is a full date. The extraction prompt asks for "ISO-8601",
/// and a model that knows only the month ("she started in August 2026") correctly writes the
/// reduced-precision form <c>"2026-08"</c>. The native parser threw on it, the throw was reported as
/// "not valid JSON", and the runner discarded every entity, fact and preference in the response
/// because of one date. That is strict by design upstream, so the fix is ours to make.
/// </para>
/// <para>
/// <b>Why two converters.</b> <c>"2026-08"</c> is not an instant, it is a period. Only the field says
/// which edge is meant: a fact valid FROM August starts on the 1st; a fact valid UNTIL August lasts
/// through the 31st. No general-purpose parser can know that.
/// </para>
/// <para>
/// <b>Always UTC.</b> The native parser read a date-only <c>"2026-08-01"</c> at the machine's LOCAL
/// midnight, so the same extraction stored different instants on machines in different time zones.
/// A value without an offset is now that calendar date/time in UTC; a value with an offset keeps its
/// instant and is normalized to offset zero.
/// </para>
/// <para>
/// <b>Never throws.</b> An unreadable value drops that one date (null = unbounded, which is the
/// meaning the prompt gives an omitted field) and keeps the fact. The drop is recorded as a
/// <c>memory.extract.date_dropped</c> event on the current activity so it stays diagnosable.
/// </para>
/// </remarks>
internal abstract partial class PeriodDateConverter(PeriodEdge edge, string field) : JsonConverter<DateTimeOffset?>
{
    public override bool HandleNull => true;

    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
                return Parse(reader.GetString(), edge, field);
            case JsonTokenType.Number when reader.TryGetInt32(out var year):
                // A bare year written as a number ("valid_from": 2024).
                return Parse(year.ToString(CultureInfo.InvariantCulture), edge, field);
            default:
                reader.Skip();
                Dropped(field, "unsupported_token");
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value is { } v)
            writer.WriteStringValue(v);
        else
            writer.WriteNullValue();
    }

    /// <summary>Parses one validity value; null when the value is absent, a sentinel, or unreadable. Never throws.</summary>
    internal static DateTimeOffset? Parse(string? raw, PeriodEdge edge, string field = "date")
    {
        try
        {
            return ParseCore(raw, edge, field);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            // Belt and braces: one odd date must never cost the reply it sits in.
            Dropped(field, "unparseable");
            return null;
        }
    }

    private static DateTimeOffset? ParseCore(string? raw, PeriodEdge edge, string field)
    {
        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text) || IsSentinel(text))
            return null;

        var calendar = CalendarDate().Match(text);
        if (calendar.Success)
        {
            // ASCII digits only (the pattern is [0-9], not \d, which also matches fullwidth and Arabic-Indic
            // digits that int.Parse rejects), so these parses cannot fail.
            var year = int.Parse(calendar.Groups["y"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
            int? month = calendar.Groups["m"].Success
                ? int.Parse(calendar.Groups["m"].Value, NumberStyles.None, CultureInfo.InvariantCulture)
                : null;
            int? day = calendar.Groups["d"].Success
                ? int.Parse(calendar.Groups["d"].Value, NumberStyles.None, CultureInfo.InvariantCulture)
                : null;

            if (year < 1 || month is < 1 or > 12 ||
                (day is { } d && (d < 1 || d > DateTime.DaysInMonth(year, month!.Value))))
            {
                Dropped(field, "out_of_range");
                return null;
            }

            var start = new DateTimeOffset(year, month ?? 1, day ?? 1, 0, 0, 0, TimeSpan.Zero);
            if (edge == PeriodEdge.Start)
                return start;

            // Last instant of the period: the next period's start, one tick back. The last period
            // of year 9999 has no successor, so it ends at MaxValue.
            try
            {
                var next = day is not null ? start.AddDays(1)
                    : month is not null ? start.AddMonths(1)
                    : start.AddYears(1);
                return next.AddTicks(-1);
            }
            catch (ArgumentOutOfRangeException)
            {
                return DateTimeOffset.MaxValue;
            }
        }

        // Anything else goes through the general parser, but only when it names its year: the parser fills
        // a missing date or year from the machine clock ("17:00" = today, "August 15" = this year), which
        // would make the stored instant depend on when extraction ran. AssumeUniversal: no offset means
        // UTC, never the machine's local zone.
        if (!ExplicitYear().IsMatch(text))
        {
            Dropped(field, "no_year");
            return null;
        }
        // "03/04/2026" is 3 April to half the world and March 4 to the other half; the parser would
        // silently pick month-first. Unbounded is safer than the wrong month.
        if (AmbiguousNumericDate().IsMatch(text))
        {
            Dropped(field, "ambiguous_order");
            return null;
        }
        if (DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces,
                out var instant) && instant.Year > 1)
        {
            // Without a time of day, free text does not say how long a period it names: "August 2026"
            // and "1 August 2026" parse to the same instant. For a START that instant is right either
            // way. For an END it would expire a month-long fact on its first day, and a fact that wrongly
            // expires is worse than one left unbounded (live recall filters on valid_until). So an end
            // is taken from free text only when it carries a time.
            if (edge == PeriodEdge.End && !text.Contains(':', StringComparison.Ordinal))
            {
                Dropped(field, "ambiguous_precision");
                return null;
            }
            return instant.ToUniversalTime();
        }

        Dropped(field, "unparseable");
        return null;
    }

    private static bool IsSentinel(string text) =>
        text.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("null", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("none", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("n/a", StringComparison.OrdinalIgnoreCase);

    private static void Dropped(string field, string reason)
    {
        if (Pending.Value is { } buffer)
        {
            buffer.Add((field, reason));
            return;
        }
        Record(field, reason);
    }

    private static void Record(string field, string reason) =>
        Activity.Current?.AddEvent(new ActivityEvent(
            "memory.extract.date_dropped",
            tags: new ActivityTagsCollection
            {
                ["memory.extract.field"] = field,
                ["memory.extract.reason"] = reason,
            }));

    // The parser reads a reply twice when the whole-document read fails (once whole, then item by item to
    // salvage what it can), so drops seen in the first read are held and recorded only if that read is the
    // one that counts; otherwise the salvage pass records each drop once.
    private static readonly AsyncLocal<List<(string Field, string Reason)>?> Pending = new();

    /// <summary>Holds date drops until <see cref="PendingDrops.Commit"/> (the read counted) or disposal (it did not).</summary>
    internal static PendingDrops HoldDrops()
    {
        var previous = Pending.Value;
        Pending.Value = [];
        return new PendingDrops(previous);
    }

    internal sealed class PendingDrops(List<(string Field, string Reason)>? previous) : IDisposable
    {
        private readonly List<(string Field, string Reason)> _held = Pending.Value!;

        public void Commit()
        {
            Pending.Value = previous;
            foreach (var (field, reason) in _held) Dropped(field, reason);
            _held.Clear();
        }

        public void Dispose() => Pending.Value = previous;
    }

    // YYYY, YYYY-MM or YYYY-MM-DD; month and day may be written without a leading zero. ASCII digits.
    [GeneratedRegex(@"^(?<y>[0-9]{4})(?:-(?<m>[0-9]{1,2})(?:-(?<d>[0-9]{1,2}))?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex CalendarDate();

    // Day and month both written as numbers before the year: 03/04/2026, 3.4.26.
    [GeneratedRegex(@"(?<![0-9])[0-9]{1,2}[/.][0-9]{1,2}[/.][0-9]{2,4}(?![0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex AmbiguousNumericDate();

    // A four-digit year standing on its own somewhere in free text ("1 August 2026 18:00").
    [GeneratedRegex(@"(?<![0-9])[0-9]{4}(?![0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitYear();
}
