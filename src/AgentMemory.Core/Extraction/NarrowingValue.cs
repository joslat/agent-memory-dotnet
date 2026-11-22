using System.Text.RegularExpressions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Core.Memory;

namespace AgentMemory.Core.Extraction;

/// <summary>
/// SUPERSEDE-NARROWER (found tracing Pablo's people scenario on 2.3.1, 22 Nov 2026): "Acme Logistics go-live | takes place at
/// | a war room at Acme during the go-live week" was judged to replace "... | takes place at | Acme's office in Córdoba", and
/// Córdoba was lost. The judges' criteria let a value that "states the same thing more precisely" replace the stored one,
/// but this one only narrows it: it keeps a name the stored value has (Acme), drops another (Córdoba) and adds none. Such a
/// value is a detail beside the stored one, not its replacement, so both are kept. A value that names something new is a
/// change and replaces as before ("Acme's office in Rosario", "Madrid" for "Lisbon"), and so is one that keeps every name
/// ("Paris, 15th arrondissement" for "Paris"). Deterministic; no model call.
/// </summary>
internal static class NarrowingValue
{
    // A capitalised word: a name, a place, a month or a day. Apostrophes end it, so "Acme's" reads "Acme".
    private static readonly Regex Name = new(@"\p{Lu}[\p{L}\p{M}-]+", RegexOptions.CultureInvariant);

    /// <summary>The capitalised words of a value.</summary>
    internal static IReadOnlySet<string> Names(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? new HashSet<string>(StringComparer.Ordinal)
            : Name.Matches(value).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Whether <paramref name="written"/> only narrows <paramref name="stored"/>: it shares a name with it, the stored value
    /// names something it drops, and it names nothing the stored value does not.
    /// </summary>
    internal static bool Narrows(string? stored, string? written)
    {
        var before = Names(stored);
        var after = Names(written);
        return before.Overlaps(after) && !before.IsSubsetOf(after) && after.IsSubsetOf(before);
    }

    /// <summary>The same subject and predicate, as the store compares them.</summary>
    internal static bool SameSlot(Fact written, Fact stored) =>
        MemoryTripleCanonicalizer.CanonicalValue(written.Subject) == MemoryTripleCanonicalizer.CanonicalValue(stored.Subject)
        && MemoryTripleCanonicalizer.CanonicalValue(written.Predicate) == MemoryTripleCanonicalizer.CanonicalValue(stored.Predicate);

    /// <summary>A written fact that only narrows a stored one in the same slot: both are kept.</summary>
    internal static bool KeepsBoth(Fact written, Fact stored) => SameSlot(written, stored) && Narrows(stored.Object, written.Object);
}
