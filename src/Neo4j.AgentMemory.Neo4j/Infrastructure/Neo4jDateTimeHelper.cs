using Neo4j.Driver;

namespace Neo4j.AgentMemory.Neo4j.Infrastructure;

internal static class Neo4jDateTimeHelper
{
    internal static DateTimeOffset ReadDateTimeOffset(object? value)
    {
        return value switch
        {
            ZonedDateTime zdt => zdt.ToDateTimeOffset(),
            string s => DateTimeOffset.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind),
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
