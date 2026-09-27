using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Neo4j.Infrastructure;

/// <summary>
/// 36.1. How a <see cref="DatePrecision"/> is stored on a node: a lower-case word beside the date it qualifies
/// (<c>valid_from_precision: "month"</c>), or nothing when unspecified, so a node written before precision was
/// recorded reads back as <see cref="DatePrecision.Unspecified"/>, exactly as it renders today.
/// </summary>
internal static class DatePrecisionProperty
{
    internal static string? ToStored(DatePrecision precision) => precision switch
    {
        DatePrecision.Year => "year",
        DatePrecision.Month => "month",
        DatePrecision.Day => "day",
        DatePrecision.Instant => "instant",
        _ => null,
    };

    internal static DatePrecision FromStored(string? stored) => stored switch
    {
        "year" => DatePrecision.Year,
        "month" => DatePrecision.Month,
        "day" => DatePrecision.Day,
        "instant" => DatePrecision.Instant,
        _ => DatePrecision.Unspecified,
    };

    internal static DatePrecision Read(IReadOnlyDictionary<string, object> properties, string key) =>
        properties.TryGetValue(key, out var value) ? FromStored(value as string) : DatePrecision.Unspecified;
}
