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
