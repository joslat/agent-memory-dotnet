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
    /// <summary>
    /// The packs as a host holds them: each distinct pack once, in the order first given. Copies of one pack (one digest) reached
    /// by several routes merge by one rule, whatever their order (review 8, W3: the first copy given was kept, so a pack that a set
    /// narrows expired or not by the order of the arguments): it is available for as long as any route makes it available. What
    /// travels outside the digest pools across the copies, by the rules that hold for it within one copy (review 9, C-H1: the
    /// first copy's certificates were kept): its certificates by the copies rule (<see cref="PackCertificates.Pooled"/>), so
    /// adding a copy never takes trust away and a trusted failure is never hidden; its signers, every one.
    /// </summary>
    /// <exception cref="PackLoadException">Two versions or contents of one pack, or two snapshots of one module.</exception>
    internal static List<LoadedPack> Of(IEnumerable<LoadedPack> packs)
    {
        var distinct = packs.GroupBy(p => p.Digest, StringComparer.Ordinal).Select(Merged).ToList();
        if (Problems(distinct) is { Count: > 0 } problems) throw new PackLoadException(problems);
        return distinct;
    }

    // One pack from its copies: valid until the latest of their ends, and without an end if any copy has none; the certificates
    // and signers of all of them.
    private static LoadedPack Merged(IEnumerable<LoadedPack> copies)
    {
        var all = copies.ToList();
        if (all.Count == 1) return all[0];
        var until = all.Any(c => c.ValidUntil is null) ? null : all.Max(c => c.ValidUntil);
        return all[0] with
        {
            ValidUntil = until, Knowledge = all[0].Knowledge?.WithValidUntil(until),
            Certificates = PackCertificates.Pooled(all.SelectMany(c => c.Certificates)),
            Signers = [.. all.SelectMany(c => c.Signers).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
        };
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
