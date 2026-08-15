using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Core.Services;

/// <summary>
/// Appends a derived fact's arithmetic to its rendered line, so the model can check it.
/// </summary>
/// <remarks>
/// <para>
/// <c>17 — derived: 12 (a1) + 5 (b2)</c>. A derived number presented bare is a claim; presented with
/// its inputs and its operator it is an argument, and the model reading it can tell the difference
/// between an aggregate that follows from what is stored and one that does not.
/// </para>
/// <para>
/// <b>Shared by both surfaces on purpose.</b> Core's formatter and the Agent Framework mapper render
/// the same facts, and this codebase has already paid twice for letting two surfaces re-derive one
/// rendering decision — most recently a procedure-trust clause fixed in the harness while the product
/// shipped the contradiction.
/// </para>
/// <para>
/// <b>An ordinary fact renders byte-identically to before.</b> No metadata, no suffix, same string —
/// which is what keeps every sealed prompt fingerprint valid while the feature is off, and also while
/// it is on for facts that were merely observed.
/// </para>
/// </remarks>
internal static class DerivedFactRenderer
{
    /// <summary>Returns <paramref name="line"/> with the derivation appended, or unchanged.</summary>
    public static string Append(string line, Fact fact)
    {
        var derivation = fact.Metadata.GetDerivation();
        if (string.IsNullOrWhiteSpace(derivation)) return line;

        // An em dash, matching the projection layer's existing annotation separator, so a fact carrying
        // both a projection annotation and a derivation does not read as two different formats stapled
        // together.
        return $"{line} — derived: {derivation}";
    }
}
