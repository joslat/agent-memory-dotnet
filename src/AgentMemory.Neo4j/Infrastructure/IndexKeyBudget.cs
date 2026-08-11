using System.Text;
using AgentMemory.Abstractions.Exceptions;

namespace AgentMemory.Neo4j.Infrastructure;

/// <summary>
/// Rejects property values too large for a Neo4j range index, before the write reaches the driver.
/// </summary>
/// <remarks>
/// <c>entity_name_idx</c> and <c>entity_canonical_idx</c> (<c>SchemaQueries.cs:98,101</c>) are range
/// indexes over LLM-produced text, and the only length rule anywhere in this codebase is a
/// <b>minimum</b> — <c>ExtractionOptions.MinNameLength = 2</c>. Neo4j bounds an index key at roughly
/// 8 KB, so a pathological name makes the driver throw <c>Property value is too large to index</c>
/// from inside the write, with no indication of which value or which entity caused it.
/// <para>
/// <b>This guard changes no successful write.</b> Everything it rejects already fails today; it only
/// fails earlier, names the offending property and entity, and raises a typed
/// <see cref="MemoryValidationException"/> instead of surfacing a driver message from the middle of a
/// batch. Narrowing nothing is what makes it safe to add to a shipped write path.
/// </para>
/// <para>
/// The budget is measured in <b>UTF-8 bytes, not characters</b>: Neo4j bounds the encoded key, so a
/// character-count check would admit a multi-byte name that the index then rejects — reintroducing
/// precisely the failure this prevents.
/// </para>
/// </remarks>
internal static class IndexKeyBudget
{
    /// <summary>
    /// Largest indexable value in UTF-8 bytes, kept conservatively below Neo4j's own bound.
    /// </summary>
    /// <remarks>
    /// Neo4j 5 caps a range-index key near 8,167 bytes. The margin absorbs per-key overhead rather
    /// than sitting exactly on a limit whose accounting is not part of any public contract.
    /// </remarks>
    internal const int MaxIndexedBytes = 8_000;

    /// <summary>Throws when <paramref name="value"/> cannot be range-indexed.</summary>
    internal static void EnsureIndexable(string? value, string propertyName, string? owningId)
    {
        if (string.IsNullOrEmpty(value))
            return;

        // Cheap pre-check: UTF-8 is at most 4 bytes per char, so anything under a quarter of the
        // budget cannot exceed it and never needs encoding.
        if (value.Length <= MaxIndexedBytes / 4)
            return;

        var byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount <= MaxIndexedBytes)
            return;

        throw new MemoryException(
            $"Property '{propertyName}'" +
            (owningId is null ? "" : $" on '{owningId}'") +
            $" is {byteCount} UTF-8 bytes, which exceeds the {MaxIndexedBytes}-byte range-index " +
            "budget. Neo4j cannot index it, so the write would fail. Shorten the value at the source; " +
            "it is almost always an extraction artefact rather than a real name.");
    }

    /// <summary>
    /// Throws when the <b>combined</b> parts of a composite index key exceed the budget.
    /// </summary>
    /// <remarks>
    /// L11. Facts MERGE on <c>{subject_key, predicate_key, object_key, owner_key}</c>, so the indexed
    /// key is the composite, not any single property. Checking each part against the full budget
    /// separately would admit a composite roughly four times over it — the exact failure this guard
    /// exists to prevent.
    /// <para>
    /// Summing the parts is <b>deliberately conservative</b>. Neo4j documents a bound on the index
    /// key; how a composite key is encoded is not something this codebase has measured, so the guard
    /// budgets the sum rather than asserting an internal it cannot verify. Erring strict costs only
    /// pathological values, while erring loose reproduces the opaque driver failure.
    /// </para>
    /// </remarks>
    internal static void EnsureCompositeIndexable(
        IReadOnlyList<(string Name, string? Value)> parts,
        string? owningId)
    {
        ArgumentNullException.ThrowIfNull(parts);

        var total = 0;
        foreach (var (_, value) in parts)
            total += ByteCountOf(value);

        if (total <= MaxIndexedBytes)
            return;

        // Name the largest parts first: with a composite key the operator needs to know which value
        // to shorten, and the message is the only place that information exists.
        var offenders = parts
            .Where(part => !string.IsNullOrEmpty(part.Value))
            .OrderByDescending(part => ByteCountOf(part.Value))
            .Select(part => $"{part.Name}={ByteCountOf(part.Value)}B");

        throw new MemoryException(
            $"The composite index key" +
            (owningId is null ? "" : $" for '{owningId}'") +
            $" is {total} UTF-8 bytes across {parts.Count} properties ({string.Join(", ", offenders)}), " +
            $"which exceeds the {MaxIndexedBytes}-byte range-index budget. Neo4j cannot index it, so " +
            "the write would fail. Shorten the value at the source; it is almost always an extraction " +
            "artefact rather than a real subject or object.");
    }

    /// <summary>
    /// Whether the combined parts exceed the budget, as a question rather than a throw.
    /// </summary>
    /// <remarks>
    /// The bootstrap backfill needs this. It rewrites keys onto facts that are <b>already stored</b>,
    /// so throwing there would abort schema bootstrap — and bootstrap is the very thing an operator
    /// would run to recover, leaving the system unstartable with no remedy. It skips the row and logs
    /// instead; the unindexable fact is then surfaced by the FAILED-index check rather than by
    /// bricking startup.
    /// </remarks>
    internal static bool ExceedsCompositeBudget(IReadOnlyList<(string Name, string? Value)> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        var total = 0;
        foreach (var (_, value) in parts)
            total += ByteCountOf(value);

        return total > MaxIndexedBytes;
    }

    private static int ByteCountOf(string? value)
        => string.IsNullOrEmpty(value) ? 0 : Encoding.UTF8.GetByteCount(value);
}
