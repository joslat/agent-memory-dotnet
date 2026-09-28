using System.Globalization;
using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Core.Services;

/// <summary>
/// 36.1. How a fact's validity dates read in a prompt, one rule for every surface that renders facts (the MAF
/// mapper, the Core formatter, the profile block), so the same fact never carries a date on one and not on
/// another, or a day on one and a month on another.
/// </summary>
/// <remarks>
/// <para>
/// A date prints at the precision it was stated (<see cref="DatePrecision"/>): "since March 2024" stays
/// <c>2024-03</c>, never <c>2024-03-01</c>, a day nobody said. An unrecorded precision prints as a day, which is
/// how due and expiring facts have always rendered.
/// </para>
/// <para>
/// Measured before (simulated conversations): the dates were extracted and stored, but relevant facts rendered
/// as <c>subject predicate object</c> only, so "When did I move to Lyon?" was answered "I don't have the date"
/// with <c>valid_from 2024-03</c> on the fact.
/// </para>
/// </remarks>
internal static class FactDates
{
    /// <summary>
    /// <c>" (since 2024-03)"</c>, <c>" (until 2027-06)"</c>, <c>" (2024-03 to 2027-06)"</c>, <c>" (on 2026-09-26)"</c>
    /// when both ends name the same period, or an empty string for a fact without dates.
    /// </summary>
    internal static string Suffix(
        DateTimeOffset? validFrom, DatePrecision fromPrecision, DateTimeOffset? validUntil, DatePrecision untilPrecision,
        DateTimeOffset? occurredOn = null, DatePrecision occurredOnPrecision = DatePrecision.Unspecified)
    {
        // An event reads as the day it happened, whatever else it carries.
        if (occurredOn is { } on) return $" (on {Format(on, occurredOnPrecision)})";
        var from = validFrom is { } f ? Format(f, fromPrecision) : null;
        var until = validUntil is { } u && u != DateTimeOffset.MaxValue ? Format(u, untilPrecision) : null;
        return (from, until) switch
        {
            (null, null) => string.Empty,
            ({ } a, null) => $" (since {a})",
            (null, { } b) => $" (until {b})",
            ({ } a, { } b) when a == b => $" (on {a})",
            ({ } a, { } b) => $" ({a} to {b})",
        };
    }

    /// <inheritdoc cref="Suffix(DateTimeOffset?, DatePrecision, DateTimeOffset?, DatePrecision, DateTimeOffset?, DatePrecision)"/>
    internal static string Suffix(Fact fact) =>
        Suffix(fact.ValidFrom, fact.ValidFromPrecision, fact.ValidUntil, fact.ValidUntilPrecision, fact.OccurredOn, fact.OccurredOnPrecision);

    /// <summary>One date at its precision: <c>2024</c>, <c>2024-03</c>, <c>2024-03-12</c>, <c>2024-03-12 17:00 UTC</c>.</summary>
    internal static string Format(DateTimeOffset at, DatePrecision precision)
    {
        var utc = at.UtcDateTime;
        return precision switch
        {
            DatePrecision.Year => utc.ToString("yyyy", CultureInfo.InvariantCulture),
            DatePrecision.Month => utc.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            DatePrecision.Instant => utc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC",
            _ => utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
    }

    /// <summary>The day a message was said, for a recalled turn from another session: <c>[2026-09-26] </c>.</summary>
    internal static string MessagePrefix(DateTimeOffset timestamp) =>
        "[" + timestamp.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "] ";
}
