using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Extensibility.Ampack;

/// <summary>
/// What "these packs, held together" means, the one rule every door that holds packs together runs before anything else
/// (review 7, W4: <c>ampack serve</c> and <c>test</c> unioned two snapshots of one module that a host replaces one by the other,
/// merged two versions of one pack into one module, and crashed on two definitions with one id): a host holds one version of a
/// pack, and one live snapshot of a module, a later one replacing the earlier whole. Copies of one pack, the same digest reached
/// twice (as a member of a set and on its own), are one pack.
/// </summary>
[Experimental("AMEXT001")]
internal static class PackMountPlan
{
    /// <summary>The packs as a host holds them: each distinct pack once, in the order given.</summary>
    /// <exception cref="PackLoadException">Two versions or contents of one pack, or two snapshots of one module.</exception>
    internal static List<LoadedPack> Of(IEnumerable<LoadedPack> packs)
    {
        var distinct = packs.DistinctBy(p => p.Digest, StringComparer.Ordinal).ToList();
        if (Problems(distinct) is { Count: > 0 } problems) throw new PackLoadException(problems);
        return distinct;
    }

    /// <summary>Why the packs cannot be held together as a host holds them, in id order; empty when they can.</summary>
    internal static IReadOnlyList<string> Problems(IReadOnlyList<LoadedPack> packs)
    {
        var problems = new List<string>();
        foreach (var same in packs.GroupBy(p => p.Id, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
            if (same.Select(p => p.Digest).Distinct(StringComparer.Ordinal).Count() > 1)
                problems.Add($"{same.Key} comes more than once ({string.Join(", ", same.Select(p => p.Version).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))}): a host holds one version of a pack, so give one.");
        foreach (var of in packs.Where(p => p.Kind == "snapshot").GroupBy(p => p.Manifest.Modules.Keys.Single(), StringComparer.Ordinal)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
            if (of.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() > 1)
                problems.Add($"{string.Join(" and ", of.Select(p => p.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))} are snapshots of {of.Key}: a host holds one live snapshot of a module, the later replacing the earlier whole, so give one.");
        return problems;
    }
}
