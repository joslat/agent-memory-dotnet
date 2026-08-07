using System.Text;

namespace AgentMemory.Core.Memory;

/// <summary>
/// Canonical forms for the fact triple's subject, predicate and object.
/// </summary>
/// <remarks>
/// <para>
/// Facts already deduplicate at write time — the repository MERGEs on
/// <c>{subject, predicate, object, owner_key}</c> — but that key uses the raw strings, so trivial
/// surface differences defeat it. Measured on a real extracted graph:
/// <c>"Ava and Lily"/were_born_in/"April"</c> and <c>"Ava and Lily"/were born in/"April"</c> became
/// two nodes, as did <c>"User"/is planning</c> and <c>"user"/is planning</c>. That graph held 575
/// facts across <b>407 distinct predicates</b> — 1.41 facts per predicate — which also makes any
/// query keyed on a relation impossible.
/// </para>
/// <para>
/// Canonicalization is therefore not a new deduplication mechanism; it repairs the one that exists.
/// </para>
/// <para>
/// <b>Deterministic only, by design.</b> No embedding or fuzzy similarity is used to fold one
/// predicate into another. Relations such as <c>bought</c> and <c>sold</c> are semantically adjacent
/// and <i>opposite</i>; merging them would silently corrupt meaning in a way ordinary tests would not
/// catch. Only differences that cannot change meaning are collapsed: surrounding whitespace, letter
/// case, and word separators.
/// </para>
/// <para>
/// <b>Compute once, in C#, at write time.</b> Never recompute this in Cypher: .NET's
/// <see cref="string.ToLowerInvariant"/> and Cypher's <c>toLower()</c> disagree on U+0130 (Turkish
/// dotted capital I), so a value canonicalized on one side and matched on the other would produce two
/// keys for one relation — reintroducing the exact fragmentation this removes.
/// </para>
/// </remarks>
public static class MemoryTripleCanonicalizer
{
    /// <summary>
    /// Returns the canonical form used for identity: trimmed, lower-cased invariantly, with word
    /// separators unified and runs of whitespace collapsed.
    /// </summary>
    /// <remarks>
    /// The original text is always retained separately; this value is for matching, never display.
    /// </remarks>
    public static string Canonical(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var lowered = value.ToLowerInvariant();
        var builder = new StringBuilder(lowered.Length);
        var pendingSeparator = false;
        foreach (var character in lowered)
        {
            // '_' and '-' are word separators in extracted predicates ("was_born" / "was born"),
            // never meaningful punctuation, so they normalize to the same break as whitespace.
            if (char.IsWhiteSpace(character) || character is '_' or '-')
            {
                pendingSeparator = builder.Length > 0;
                continue;
            }

            if (pendingSeparator)
            {
                builder.Append(' ');
                pendingSeparator = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}
