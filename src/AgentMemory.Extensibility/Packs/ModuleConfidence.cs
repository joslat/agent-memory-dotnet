// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace AgentMemory.Extensibility.Packs;

/// <summary>
/// A record's confidence, capped by where it came from (feature 09, R-CORE-137): the one definition, used when a host loads a
/// pack, by the read floor and by the frame.
/// </summary>
[Experimental("AMEXT001")]
public static class ModuleConfidence
{
    /// <summary>Full confidence, in millionths.</summary>
    public const int Full = 1_000_000;

    /// <summary>
    /// The effective confidence: the least of the record's own, its source's and its signer's, each in millionths; a part that is
    /// absent caps nothing, and null when no part is given. It never exceeds any part.
    /// </summary>
    public static int? Effective(int? own, int? source, int? signer)
    {
        int? least = null;
        foreach (var part in (ReadOnlySpan<int?>)[own, source, signer])
            if (part is { } value) least = least is { } l ? Math.Min(l, value) : value;
        return least;
    }

    /// <summary>Whether a record of this confidence clears a floor: one with none counts as full; no floor keeps every record.</summary>
    public static bool Clears(int? confidence, int? floor) => floor is not { } f || (confidence ?? Full) >= f;

    /// <summary>Millionths as a decimal fraction for a frame: 900000 is 0.9, 1000000 is 1.</summary>
    public static string Shown(int micro)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(micro);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(micro, Full);
        var whole = micro / Full;
        var fraction = (micro % Full).ToString("D6", CultureInfo.InvariantCulture).TrimEnd('0');
        return fraction.Length == 0 ? whole.ToString(CultureInfo.InvariantCulture) : whole.ToString(CultureInfo.InvariantCulture) + "." + fraction;
    }
}
