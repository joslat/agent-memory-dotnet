using System.Globalization;
using Neo4j.Driver;

namespace AgentMemory.Neo4j.Infrastructure;

internal static class Neo4jDateTimeHelper
{
    internal static DateTimeOffset ReadDateTimeOffset(object? value)
    {
        return value switch
        {
            ZonedDateTime zdt => zdt.ToDateTimeOffset(),
            // A null culture provider falls back to CurrentCulture, not invariant -- under a non-Gregorian
            // host culture (e.g. th-TH) this either throws FormatException or misparses the year against the
            // wrong calendar system for every string-shaped timestamp the driver ever hands back.
            string s => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            null => DateTimeOffset.UtcNow,
            _ => throw new InvalidOperationException($"Unexpected datetime type: {value.GetType()}")
        };
    }

    internal static DateTimeOffset? ReadNullableDateTimeOffset(object? value)
    {
        if (value is null) return null;
        return ReadDateTimeOffset(value);
    }
}
