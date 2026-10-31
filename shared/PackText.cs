// One source compiled into both the pack format and the module host, as PackRegistry.cs is.
#if AGENTMEMORY_PACKS_FORMAT
namespace AgentMemory.Packs.Format;
#else
namespace AgentMemory.Extensibility;
#endif

/// <summary>
/// Text as the format counts and orders it (feature 02): a character is a Unicode code point, so a length or an order is the
/// same in every language, whatever its strings are made of. .NET strings are UTF-16, so a length here counts a surrogate pair
/// once, and the order is code-point order (which is UTF-8 byte order), not .NET's ordinal UTF-16 order. Equality and hashing
/// are the same either way. One exception the format does not own: RFC 8785 (JCS) sorts keys by UTF-16 code unit.
/// </summary>
internal static class PackText
{
    /// <summary>The characters (code points) of <paramref name="text"/>; a lone surrogate counts as one.</summary>
    internal static int Length(string text) => Length(text.AsSpan());

    /// <summary>The characters (code points) of <paramref name="text"/>; a lone surrogate counts as one.</summary>
    internal static int Length(ReadOnlySpan<char> text)
    {
        var count = text.Length;
        for (var i = 0; i + 1 < text.Length; i++)
            if (char.IsHighSurrogate(text[i]) && char.IsLowSurrogate(text[i + 1])) { count--; i++; }
        return count;
    }

    /// <summary>
    /// <paramref name="a"/> against <paramref name="b"/> in code-point order: the units up to the first difference, then the code
    /// points there when a surrogate is involved; null sorts first.
    /// </summary>
    internal static int Compare(string? a, string? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++)
        {
            int x = a[i], y = b[i];
            if (x == y) continue;
            // Where a surrogate differs, the code points there decide (review F02, L1: a lone low surrogate sorted above an
            // astral character); elsewhere the units are the code points.
            if (char.IsSurrogate((char)x) || char.IsSurrogate((char)y)) return At(a, i).CompareTo(At(b, i));
            return x - y;
        }
        return a.Length - b.Length;
    }

    /// <summary>
    /// Display text (feature 04, R-CORE-126 and its review): 1 to <paramref name="maximum"/> characters, display-safe, not blank
    /// (some character that is not Unicode White_Space), and for one line no line feed, tab, line separator or paragraph
    /// separator. White_Space is listed here, as Python lists it, never read from either platform.
    /// </summary>
    internal static bool IsDisplayText(string text, int maximum, bool line)
    {
        if (Length(text) is var n && (n < 1 || n > maximum) || !IsDisplaySafe(text)) return false;
        var blank = true;
        foreach (var c in CodePoints(text))
        {
            if (line && c is '\n' or '\t' or 0x2028 or 0x2029) return false;
            if (!IsWhiteSpace(c)) blank = false;
        }
        return !blank;
    }

    // Unicode's White_Space property (stable since 6.3).
    private static bool IsWhiteSpace(int c) =>
        c is >= 0x09 and <= 0x0D or 0x20 or 0x85 or 0xA0 or 0x1680 or >= 0x2000 and <= 0x200A or 0x2028 or 0x2029 or 0x202F or 0x205F or 0x3000;

    /// <summary>
    /// Whether a text may be shown to a person (feature 04, R-CORE-126): no code point the registry refuses, so no control but a
    /// line feed or a tab, no format character but ZWNJ and ZWJ (no bidi control, no zero-width space), no private use, no
    /// surrogate. The ranges are the registry's, fixed at Unicode 15.0, never the platform's own categories.
    /// </summary>
    internal static bool IsDisplaySafe(string text)
    {
        var refused = PackRegistry.DisplayRefused;
        foreach (var c in CodePoints(text))
        {
            var (lo, hi) = (0, refused.Count - 1);
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                if (c < refused[mid].First) hi = mid - 1;
                else if (c > refused[mid].Last) lo = mid + 1;
                else return false;
            }
        }
        return true;
    }

    /// <summary>The format's order, as a comparer.</summary>
    internal static readonly IComparer<string> Order = Comparer<string>.Create(Compare);

    /// <summary>The code points of <paramref name="text"/> in turn, a lone surrogate as itself.</summary>
    internal static IEnumerable<int> CodePoints(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                yield return char.ConvertToUtf32(text[i], text[i + 1]);
                i++;
            }
            else yield return text[i];
        }
    }

    // The code point a string holds at a unit: a pair read whole from either of its units, a lone surrogate as itself.
    private static int At(string text, int i)
    {
        if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) return char.ConvertToUtf32(text[i], text[i + 1]);
        if (char.IsLowSurrogate(text[i]) && i > 0 && char.IsHighSurrogate(text[i - 1])) return char.ConvertToUtf32(text[i - 1], text[i]);
        return text[i];
    }
}
