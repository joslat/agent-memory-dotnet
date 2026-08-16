using System.Globalization;

namespace AgentMemory.Core.Extraction.Derivation;

/// <summary>
/// Turns a fact's object text into a number, or refuses.
/// </summary>
/// <remarks>
/// <para>
/// <b>The only hallucination surface in this feature.</b> Everything else is graph aggregation over
/// values that were already stored; this is the one place where a judgement is made about what a piece
/// of user text means. So it is deliberately narrow: it strips a leading currency symbol, thousands
/// separators, and a trailing percent sign, and then defers entirely to
/// <see cref="decimal.TryParse(string, NumberStyles, IFormatProvider, out decimal)"/> under the
/// invariant culture.
/// </para>
/// <para>
/// It does <b>not</b> attempt "twice a week", "a couple", "about 800", or unit normalisation. Every one
/// of those is a guess, and a guess here becomes a stored number carrying inline provenance that makes
/// it look verified. A group containing one unparsable object simply loses its numeric operators;
/// counting and enumeration still work, because those never needed the number.
/// </para>
/// </remarks>
internal static class DerivedNumberParser
{
    private const NumberStyles Styles =
        NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands;

    public static bool TryParse(string? text, out decimal value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var span = text.Trim();

        // A leading currency symbol is presentation, not magnitude: "$800" and "800" are the same
        // quantity, and refusing the first would silently drop every monetary group.
        if (span.Length > 0 && !char.IsAsciiDigit(span[0]) && span[0] is not ('-' or '+' or '.'))
        {
            var firstNumeric = span.AsSpan().IndexOfAnyInRange('0', '9');
            // -1 means there is no digit anywhere; a leading '-' or '+' would have been kept above.
            if (firstNumeric <= 0) return false;
            var prefix = span[..firstNumeric];
            // Only a SYMBOL prefix is stripped. Stripping a word prefix would turn "about 800" into
            // 800, which is a different claim -- approximately-800 asserted as exactly-800.
            if (prefix.Any(char.IsLetter)) return false;
            span = span[firstNumeric..];
        }

        // A trailing percent is a unit, and units are Phase 2. Refusing keeps "50%" out of a sum with
        // "50" rather than adding two quantities that are not the same kind of thing.
        if (span.EndsWith('%')) return false;

        // Anything left over after the number -- "800 dollars", "800kg" -- is a unit too.
        return decimal.TryParse(span, Styles, CultureInfo.InvariantCulture, out value);
    }
}
