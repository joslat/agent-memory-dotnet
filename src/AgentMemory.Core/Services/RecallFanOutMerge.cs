using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Core.Services;

/// <summary>
/// Which sections a sub-query's affinity retrieves from (Proposal M, 30.10, §5.5).
/// </summary>
/// <remarks>
/// Published as one table rather than scattered <c>switch</c> arms so the mapping can be read, tested
/// and changed in a single place. A type with no live destination in the current mode — Episodic under
/// a zero message budget, say — retrieves nothing and records <c>ItemsRetrieved = 0</c>: a reported
/// no-op, never an inert branch that looks like it ran.
/// </remarks>
internal static class SubQueryAffinityMap
{
    /// <summary>The section keys a given affinity draws from.</summary>
    internal static IReadOnlyList<string> SectionsFor(MemoryTypeAffinity affinity) => affinity switch
    {
        MemoryTypeAffinity.Semantic => ["entities", "facts"],
        MemoryTypeAffinity.Preference => ["preferences"],
        MemoryTypeAffinity.Temporal => ["facts", "messages"],
        MemoryTypeAffinity.Episodic => ["messages"],
        MemoryTypeAffinity.Procedural => ["traces"],
        _ => [],
    };
}

/// <summary>
/// Merges a fan-out leg's scored results into the monolithic ones (Proposal M, 30.10, §5.5).
/// </summary>
/// <remarks>
/// <para>
/// Pure and id-keyed, deliberately separated from the assembler so the rules that decide what reaches
/// the prompt can be tested without a container, a provider, or a database.
/// </para>
/// <para>
/// <b>Budgets are never multiplied.</b> The merged section is re-capped at the same <c>MaxX</c> the
/// monolithic query used. A fan-out that returned more rows because it asked more questions would
/// "improve" recall by spending more of the prompt, which is not the claim being tested.
/// </para>
/// </remarks>
internal static class RecallFanOutMerge
{
    /// <summary>
    /// Unions monolithic and sub-query results by id, keeping the higher score.
    /// </summary>
    /// <param name="monolithic">Results from the single blended query, in their existing order.</param>
    /// <param name="fromSubQueries">Results from every leg targeting this section.</param>
    /// <param name="idOf">The item's stable id — FactId, EntityId, PreferenceId, MessageId, TraceId.</param>
    /// <param name="limit">The section's existing cap. The merged result never exceeds it.</param>
    /// <returns>The merged, re-capped section and the ids the legs contributed that were not already present.</returns>
    /// <remarks>
    /// <b>Monolithic precedence on ties</b> is what makes the off-state and the fired-but-useless state
    /// distinguishable. If a leg re-finds an item the blended query already had, at the same score, the
    /// original ordering survives — so a fan-out that changed nothing produces a byte-identical section
    /// rather than a reshuffled one that merely looks different.
    /// </remarks>
    internal static (IReadOnlyList<T> Merged, IReadOnlyList<string> UniqueIds) Merge<T>(
        IReadOnlyList<(T Item, double Score)> monolithic,
        IReadOnlyList<(T Item, double Score)> fromSubQueries,
        Func<T, string> idOf,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(monolithic);
        ArgumentNullException.ThrowIfNull(fromSubQueries);
        ArgumentNullException.ThrowIfNull(idOf);

        if (limit <= 0) return ([], []);

        // Rank carries the monolithic ordering into the tie-break: same score, earlier rank wins.
        var byId = new Dictionary<string, (T Item, double Score, int Rank)>(StringComparer.Ordinal);
        var order = new List<string>(monolithic.Count + fromSubQueries.Count);

        for (var index = 0; index < monolithic.Count; index++)
        {
            var (item, score) = monolithic[index];
            var id = idOf(item);
            if (byId.ContainsKey(id)) continue;   // the blended query can repeat an id; first wins
            byId[id] = (item, score, index);
            order.Add(id);
        }

        var monolithicCount = monolithic.Count;
        var unique = new List<string>();

        foreach (var (item, score) in fromSubQueries)
        {
            var id = idOf(item);
            if (byId.TryGetValue(id, out var existing))
            {
                // Already present. Keep the higher score; the rank -- and therefore the tie-break --
                // stays with the monolithic position.
                if (score > existing.Score) byId[id] = (existing.Item, score, existing.Rank);
                continue;
            }

            // Ranked after every monolithic row, so an equal score never displaces one.
            byId[id] = (item, score, monolithicCount + unique.Count);
            order.Add(id);
            unique.Add(id);
        }

        var merged = order
            .Select(id => byId[id])
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Rank)
            .Take(limit)
            .Select(entry => entry.Item)
            .ToList();

        // Only the unique ids that SURVIVED the cap count as contributions. An item retrieved and then
        // truncated away never reached the prompt, and reporting it as a contribution is how a
        // mechanism looks valuable while changing nothing a reader ever sees.
        var survivingIds = new HashSet<string>(merged.Select(idOf), StringComparer.Ordinal);
        var survivingUnique = unique.Where(survivingIds.Contains).ToList();

        return (merged, survivingUnique);
    }
}
