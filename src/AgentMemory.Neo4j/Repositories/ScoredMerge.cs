namespace AgentMemory.Neo4j.Repositories;

/// <summary>Two scored result lists merged into one: each item once at its best score, best first, at most a limit.</summary>
internal static class ScoredMerge
{
    internal static List<(T, double)> ByScore<T>(IEnumerable<(T, double)> a, IEnumerable<(T, double)> b, Func<T, string> id, int limit) =>
        a.Concat(b)
            .GroupBy(r => id(r.Item1), StringComparer.Ordinal)
            .Select(g => g.MaxBy(r => r.Item2))
            .OrderByDescending(r => r.Item2)
            .Take(limit)
            .ToList();
}
