using System.Security.Cryptography;
using System.Text;

namespace AgentMemory.Core.Memory;

/// <summary>
/// A stable content hash of a relation vocabulary or lexicon.
/// </summary>
/// <remarks>
/// <para>
/// The extraction vocabulary decides what is <i>stored</i> and the query lexicon decides what is
/// <i>retrieved</i>, so two runs made under different tables are not comparable. Recording the hash is
/// what lets a report say which table produced a given graph, and it is the same class of defect as
/// retrieval flags that were absent from a run fingerprint: a setting that changes the outcome but
/// leaves no trace in the artifact.
/// </para>
/// <para>
/// Order-independent by construction, because a vocabulary is a set: reordering entries is an
/// authoring change with no meaning, and if it altered the hash then every cosmetic edit would
/// invalidate comparisons for nothing.
/// </para>
/// </remarks>
internal static class MemoryVocabularyFingerprint
{
    /// <summary>Hashes a flat set of relation names.</summary>
    internal static string Of(IEnumerable<string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var normalized = entries
            .Select(MemoryTripleCanonicalizer.Canonical)
            .Where(entry => entry.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(entry => entry, StringComparer.Ordinal);
        return Hash(string.Join('\n', normalized));
    }

    /// <summary>Hashes a canonical-to-surface-forms table.</summary>
    /// <remarks>
    /// Each relation is hashed together with its own forms, so moving a surface form from one relation
    /// to another changes the hash even though the entry count is unchanged. A hash over counts, or
    /// over the two sides separately, would miss exactly that.
    /// </remarks>
    internal static string OfTable(IReadOnlyDictionary<string, string[]> table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var lines = table
            .Select(entry =>
            {
                var canonical = MemoryTripleCanonicalizer.Canonical(entry.Key);
                var forms = (entry.Value ?? [])
                    .Select(MemoryTripleCanonicalizer.Canonical)
                    .Where(form => form.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(form => form, StringComparer.Ordinal);
                return $"{canonical}>{string.Join(',', forms)}";
            })
            .OrderBy(line => line, StringComparer.Ordinal);
        return Hash(string.Join('\n', lines));
    }

    // ToHexString().ToLowerInvariant() rather than ToHexStringLower(): Core multi-targets down to
    // net8.0, where the lowercase overload does not exist.
    private static string Hash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))
            .ToLowerInvariant();
}
