namespace AgentMemory.Shared;

/// <summary>Internal linked source shared by built-in schema extensions and the module SDK.</summary>
internal static class DependencyOrder
{
    // Preserve ordinal ready batches, rather than switching to a priority queue: built-in migration
    // order has already shipped. Error text remains owned by each caller.
    internal static IReadOnlyList<T> Resolve<T>(
        IReadOnlyDictionary<string, T> registered,
        IEnumerable<string> roots,
        Func<T, IEnumerable<string>> dependencies,
        Func<string, string, Exception> missingDependency,
        Func<IReadOnlyList<string>, Exception> dependencyCycle)
    {
        ArgumentNullException.ThrowIfNull(registered);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(missingDependency);
        ArgumentNullException.ThrowIfNull(dependencyCycle);
        var requested = roots.ToArray();
        if (requested.Length == 0) return [];

        var edges = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var pending = new Stack<(string Parent, string Id)>(Enumerable.Reverse(requested).Select(id => ("", id)));
        while (pending.TryPop(out var next))
        {
            if (!registered.TryGetValue(next.Id, out var item))
                throw missingDependency(next.Parent, next.Id);
            if (edges.ContainsKey(next.Id)) continue;
            var names = dependencies(item).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            edges.Add(next.Id, names);
            // Reverse push preserves the original recursive depth-first missing-dependency order.
            for (var i = names.Length - 1; i >= 0; i--)
                pending.Push((next.Id, names[i]));
        }

        var remaining = new HashSet<string>(edges.Keys, StringComparer.Ordinal);
        var ordered = new List<T>(remaining.Count);
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(id => edges[id].All(dependency => !remaining.Contains(dependency)))
                .OrderBy(id => id, StringComparer.Ordinal).ToArray();
            if (ready.Length == 0)
                throw dependencyCycle(remaining.OrderBy(id => id, StringComparer.Ordinal).ToArray());
            foreach (var id in ready)
            {
                ordered.Add(registered[id]);
                remaining.Remove(id);
            }
        }
        return ordered.AsReadOnly();
    }
}
