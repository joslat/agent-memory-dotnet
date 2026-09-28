using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Core.Memory;

/// <summary>
/// J-6 (b, c). A plan that has begun takes over from the value it was planned to replace. "I'm moving to Oslo next
/// month" is stored as a plan (its start is ahead), so it replaces nothing when said; once that start has passed, the
/// value it was planned to replace is no longer the current one. Applied where a recall's facts are chosen, at the
/// recall's instant (now, or the as-of time), so nothing is rewritten: a plan that is later cancelled simply stops being
/// a live fact, and the old value is current again.
/// </summary>
/// <remarks>
/// Only single-valued relations (the ones supersession acts on: where someone lives, works, their age), per owner and
/// subject. Among a group's values that have begun, the one that began last is current, and the others that began
/// before it are dropped; values still ahead (plans) stay, shown with their start. Starts are compared at the precision
/// they were said (J-6 c): "in August" is the whole of August, so it is not before "on 20 August"; two starts that
/// overlap are ordered by when they were said.
/// </remarks>
internal static class ValueHandOver
{
    internal static IReadOnlyList<Fact> Current(IReadOnlyList<Fact> facts, DateTimeOffset at)
    {
        if (facts.Count < 2) return facts;
        HashSet<string>? dropped = null;
        foreach (var group in facts
                     .Where(f => MemoryRelationCardinality.IsSingleValued(f.Predicate))
                     .GroupBy(f => (f.OwnerId, Subject: MemoryTripleCanonicalizer.CanonicalValue(f.Subject),
                         Relation: MemoryRelationCardinality.Relation(f.Predicate))))
        {
            var begun = group.Where(f => Start(f).From <= at).ToList();
            if (begun.Count < 2) continue;
            var current = begun.Aggregate((latest, next) => Before(latest, next) ? next : latest);
            foreach (var earlier in begun.Where(f => f.FactId != current.FactId && Before(f, current)))
                (dropped ??= new HashSet<string>(StringComparer.Ordinal)).Add(earlier.FactId);
        }
        return dropped is null ? facts : [.. facts.Where(f => !dropped.Contains(f.FactId))];
    }

    /// <summary>Whether <paramref name="a"/> began before <paramref name="b"/>: at their precision, else in the order said.</summary>
    internal static bool Before(Fact a, Fact b)
    {
        var (aFrom, aTo) = Start(a);
        var (bFrom, bTo) = Start(b);
        if (aTo < bFrom) return true;
        if (bTo < aFrom) return false;
        return a.CreatedAtUtc < b.CreatedAtUtc;   // the spans overlap: what was said later is the newer value
    }

    /// <summary>
    /// When a value began, as the span its precision allows: a day, a month, a year. No start: the moment it was said.
    /// </summary>
    internal static (DateTimeOffset From, DateTimeOffset To) Start(Fact fact)
    {
        if (fact.ValidFrom is { } from) return Span(from, fact.ValidFromPrecision);
        if (fact.OccurredOn is { } on) return Span(on, fact.OccurredOnPrecision);
        return (fact.CreatedAtUtc, fact.CreatedAtUtc);
    }

    internal static (DateTimeOffset From, DateTimeOffset To) Span(DateTimeOffset at, DatePrecision precision)
    {
        var utc = at.ToUniversalTime();
        if (precision == DatePrecision.Instant) return (utc, utc);
        var start = precision switch
        {
            DatePrecision.Year => new DateTimeOffset(utc.Year, 1, 1, 0, 0, 0, TimeSpan.Zero),
            DatePrecision.Month => new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero),
            _ => new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero),
        };
        var end = precision switch
        {
            DatePrecision.Year => start.AddYears(1),
            DatePrecision.Month => start.AddMonths(1),
            _ => start.AddDays(1),
        };
        return (start, end.AddTicks(-1));
    }
}
