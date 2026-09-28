namespace AgentMemory.Core.Extraction;

/// <summary>
/// 36.4. Which value one extraction leaves current, decided once, before anything is written, from what the
/// conversation said and in which order.
/// </summary>
/// <remarks>
/// <para>
/// Earlier rules decided this piecemeal at write time, from whatever happened to be stored already: an order guard for
/// the batch path, a set of corrected-away values, a favourite rule with its own order guard. Each fixed one sequence
/// and broke another (two homes in one turn closed each other; a correction lost to its old value restated after it;
/// chained corrections left no home), because the batch and item write paths see an extraction's own statements at
/// different moments. Decided here, the answer is the same on both.
/// </para>
/// <list type="bullet">
/// <item>A statement a correction of the same extraction names as the value it replaces is <b>old</b>.</item>
/// <item>Of the statements of one single-valued relation of one subject, the <b>current</b> one is the last said that
/// is not old (the last said, when all are).</item>
/// <item>Every other statement of that relation, and an old statement of any other relation, is replaced: it supersedes
/// nothing, and the current one closes it once everything is written. A correction that is itself replaced passes
/// what it replaces on, so "Oslo now, not Copenhagen, where I'd moved from Berlin" leaves Oslo.</item>
/// </list>
/// </remarks>
internal static class CurrentValues
{
    /// <summary>The statements this extraction replaces, each mapped to the key of the one that stays current.</summary>
    /// <param name="items">The statements, in the order they were said.</param>
    /// <param name="key">A statement's key: one key said twice is one statement (one stored node).</param>
    /// <param name="relation">The single-valued relation of one subject a statement states, or null.</param>
    /// <param name="replaces">Whether <c>correction</c> is a correction naming <c>statement</c> as what it replaces.</param>
    /// <param name="since">
    /// When a statement's value began: of two dated values the later one is current, however they were said ("I moved
    /// to Oslo last June; back in 2020 I had moved to Copenhagen"). The caller gives an undated value the moment it was
    /// said (its clock's now); without <paramref name="since"/> the order said decides alone. Ties go by the order said.
    /// </param>
    internal static IReadOnlyDictionary<string, string> Replaced<T>(
        IReadOnlyList<T> items, Func<T, string> key, Func<T, string?> relation, Func<T, T, bool> replaces,
        Func<T, DateTimeOffset>? since = null) =>
        Decide(items, key, relation, replaces, since).Replaced;

    /// <summary>What <see cref="Replaced"/> decides, with the statements a correction named (never a stand-in).</summary>
    internal sealed record Decision(IReadOnlyDictionary<string, string> Replaced, IReadOnlySet<string> Old);

    /// <inheritdoc cref="Replaced"/>
    internal static Decision Decide<T>(
        IReadOnlyList<T> items, Func<T, string> key, Func<T, string?> relation, Func<T, T, bool> replaces,
        Func<T, DateTimeOffset>? since = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        var keys = items.Select(key).ToList();

        // Old: named by a correction; the last correction naming it is the one it answers to.
        var old = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var correction = 0; correction < items.Count; correction++)
        {
            for (var statement = 0; statement < items.Count; statement++)
            {
                if (keys[statement] != keys[correction] && replaces(items[statement], items[correction]))
                    old[keys[statement]] = keys[correction];
            }
        }

        var replaced = new Dictionary<string, string>(StringComparer.Ordinal);
        var currents = new HashSet<string>(StringComparer.Ordinal);
        var relations = items.Select(relation).ToList();
        foreach (var group in Enumerable.Range(0, items.Count)
                     .Where(i => relations[i] is not null)
                     .GroupBy(i => relations[i]!, StringComparer.Ordinal))
        {
            var members = group.ToList();
            var live = members.Where(i => !old.ContainsKey(keys[i])).ToList();
            var current = keys[(live.Count > 0 ? live : members)
                .OrderBy(i => since?.Invoke(items[i]) ?? DateTimeOffset.MinValue)
                .ThenBy(i => i)
                .Last()];
            currents.Add(current);
            foreach (var member in members.Where(i => keys[i] != current))
                replaced[keys[member]] = current;
        }
        // A relation's current value stays current even when named old (every value was): something must.
        foreach (var (statement, correction) in old)
        {
            if (!currents.Contains(statement)) replaced.TryAdd(statement, correction);
        }

        // Followed to the statement that stays current. A cycle ("A, not B" and "B, not A") replaces nothing.
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var start in replaced.Keys)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { start };
            var at = replaced[start];
            while (replaced.TryGetValue(at, out var next) && seen.Add(at)) at = next;
            if (!seen.Contains(at)) resolved[start] = at;
        }
        return new Decision(resolved, old.Keys.ToHashSet(StringComparer.Ordinal));
    }
}
