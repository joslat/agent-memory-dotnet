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
}
