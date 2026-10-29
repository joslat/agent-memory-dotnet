
// One namespace per assembly that compiles this file (review 4, H2): the format checks with it, and the walk normalizes anchors
// with it, so both read text by the same Unicode 15 tables whatever the host's globalization mode.
#if AGENTMEMORY_PACKS_FORMAT
namespace AgentMemory.Packs.Format;
#else
namespace AgentMemory.Extensibility.Unicode;
#endif

/// <summary>
/// Whether a string is in Unicode Normalization Form C, with the same verdict on every host: this class normalizes by itself
/// (UAX #15: full canonical decomposition, canonical ordering, canonical composition) from the tables in
/// <see cref="NfcTables"/>, and compares. Not the platform's check: under invariant globalization (NativeAOT tools, many
/// Linux containers) .NET reads every non-ASCII string as normalized, and elsewhere its ICU follows the host's Unicode version.
/// </summary>
internal static class Nfc
{
    /// <summary>The Stream-Safe Text Format's limit (UAX #15 D4): at most 30 non-starters in a row, once normalized to NFKD.</summary>
    internal const int MaximumCombiningRun = 30;

    private static readonly Lazy<(Dictionary<int, int[]> Decompositions, Dictionary<long, int> Compositions)> Data = new(Build);
    private static readonly Lazy<Dictionary<int, (int Lead, int Trail, bool All)>> StreamSafe = new(BuildStreamSafe);

    private const int SBase = 0xAC00, LBase = 0x1100, VBase = 0x1161, TBase = 0x11A7, LCount = 19, VCount = 21, TCount = 28;
    private const int NCount = VCount * TCount, SCount = LCount * NCount;

    /// <summary>
    /// Whether <paramref name="value"/> is NFC; an unpaired surrogate is not. Always by the tables (Unicode
    /// <see cref="NfcTables.UnicodeVersion"/>), never the platform's ICU, whose Unicode version differs from host to host: a
    /// code point assigned later would otherwise read one way here and another there (fourth review).
    /// </summary>
    internal static bool IsNormalized(string value) => Check(value);

    /// <summary>
    /// The NFC of <paramref name="value"/> by the tables, or null when it holds an unpaired surrogate, which has no NFC (review
    /// 4: the walk took anchors in NFC with the platform's normalizer, which under invariant globalization leaves text as it is).
    /// </summary>
    internal static string? Normalize(string value)
    {
        if (Check(value)) return value;
        if (CodePoints(value) is not { } input) return null;
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var cp in Normalize(input)) builder.Append(char.ConvertFromUtf32(cp));
        return builder.ToString();
    }

    /// <summary>
    /// The longest run of non-starters the string would hold once normalized to NFKD, as the Stream-Safe Text Format counts
    /// them (UAX #15 D4 and §13): a code point that decomposes to non-starters only adds them all; any other adds the
    /// non-starters it leads with, ends the run, and starts the next with those it trails with. Counting the decomposition,
    /// not the code points, is what bounds the normalizer's work: U+0F73 is a starter that decomposes to two marks.
    /// </summary>
    internal static int LongestCombiningRun(string value)
    {
        int longest = 0, run = 0;
        for (var i = 0; i < value.Length; i++)
        {
            int cp = value[i];
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) cp = char.ConvertToUtf32(value[i], value[++i]);
            var (lead, trail, all) = Profile(cp);
            run += lead;
            longest = Math.Max(longest, run);
            if (!all) run = trail;
        }
        return longest;
    }

    // A code point's Stream-Safe profile: from the table where its NFKD decomposition differs, else from its combining class.
    private static (int Lead, int Trail, bool All) Profile(int cp)
    {
        if (cp < 0xA0) return (0, 0, false);
        if (StreamSafe.Value.TryGetValue(cp, out var profile)) return profile;
        return Class(cp) != 0 ? (1, 1, true) : (0, 0, false);
    }

    private static Dictionary<int, (int, int, bool)> BuildStreamSafe()
    {
        var table = NfcTables.StreamSafe;
        var profiles = new Dictionary<int, (int, int, bool)>(table.Length / 4);
        for (var i = 0; i < table.Length; i += 4) profiles[table[i]] = (table[i + 1], table[i + 2], table[i + 3] == 1);
        return profiles;
    }

    /// <summary>The table-driven check: <paramref name="value"/> equals its own NFC.</summary>
    internal static bool Check(string value)
    {
        var low = true;
        foreach (var c in value)
            if (c >= 0x300) { low = false; break; }
        if (low) return true; // below U+0300 nothing decomposes or combines
        if (CodePoints(value) is not { } input) return false;
        return Normalize(input).SequenceEqual(input);
    }

    /// <summary>The NFC of a sequence of code points.</summary>
    internal static List<int> Normalize(IReadOnlyList<int> input)
    {
        var decomposed = new List<int>(input.Count + 8);
        foreach (var cp in input) Decompose(cp, decomposed);
        // Canonical ordering: a stable sort of each run of non-starters by combining class.
        for (var i = 1; i < decomposed.Count; i++)
        {
            var cls = Class(decomposed[i]);
            if (cls == 0) continue;
            for (var j = i; j > 0 && Class(decomposed[j - 1]) > cls; j--)
                (decomposed[j - 1], decomposed[j]) = (decomposed[j], decomposed[j - 1]);
        }
        if (decomposed.Count == 0) return decomposed;
        // Canonical composition: each character composes with the last starter unless something between blocks it.
        var result = new List<int>(decomposed.Count) { decomposed[0] };
        var starterPos = 0;
        var starter = decomposed[0];
        var lastClass = Class(starter) == 0 ? 0 : 256;
        for (var i = 1; i < decomposed.Count; i++)
        {
            var ch = decomposed[i];
            var cls = Class(ch);
            if ((lastClass < cls || lastClass == 0) && Compose(starter, ch) is { } composite)
            {
                result[starterPos] = composite;
                starter = composite;
                continue;
            }
            if (cls == 0) { starterPos = result.Count; starter = ch; }
            lastClass = cls;
            result.Add(ch);
        }
        return result;
    }

    private static void Decompose(int cp, List<int> into)
    {
        if (cp is >= SBase and < SBase + SCount)
        {
            var s = cp - SBase;
            into.Add(LBase + s / NCount);
            into.Add(VBase + s % NCount / TCount);
            if (s % TCount != 0) into.Add(TBase + s % TCount);
            return;
        }
        if (cp >= 0xC0 && Data.Value.Decompositions.TryGetValue(cp, out var parts)) into.AddRange(parts);
        else into.Add(cp);
    }

    private static int? Compose(int first, int second)
    {
        if (first is >= LBase and < LBase + LCount && second is >= VBase and < VBase + VCount)
            return SBase + ((first - LBase) * VCount + (second - VBase)) * TCount;
        if (first is >= SBase and < SBase + SCount && (first - SBase) % TCount == 0 && second is > TBase and < TBase + TCount)
            return first + (second - TBase);
        return Data.Value.Compositions.TryGetValue(((long)first << 21) | (uint)second, out var composite) ? composite : null;
    }

    private static int Class(int cp)
    {
        if (cp < 0x300) return 0;
        var table = NfcTables.CombiningClasses;
        int lo = 0, hi = table.Length / 3 - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (cp < table[mid * 3]) hi = mid - 1;
            else if (cp > table[mid * 3 + 1]) lo = mid + 1;
            else return table[mid * 3 + 2];
        }
        return 0;
    }

    // The code points of a string; null when it holds an unpaired surrogate.
    private static List<int>? CodePoints(string value)
    {
        var codePoints = new List<int>(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) codePoints.Add(char.ConvertToUtf32(value[i], value[++i]));
            else if (char.IsSurrogate(value[i])) return null;
            else codePoints.Add(value[i]);
        }
        return codePoints;
    }

    private static (Dictionary<int, int[]>, Dictionary<long, int>) Build()
    {
        var decompositions = new Dictionary<int, int[]>();
        var flat = NfcTables.Decompositions;
        for (var i = 0; i < flat.Length;)
        {
            var cp = flat[i];
            var length = flat[i + 1];
            decompositions[cp] = flat[(i + 2)..(i + 2 + length)];
            i += 2 + length;
        }
        var compositions = new Dictionary<long, int>();
        var triples = NfcTables.Compositions;
        for (var i = 0; i < triples.Length; i += 3) compositions[((long)triples[i] << 21) | (uint)triples[i + 1]] = triples[i + 2];
        return (decompositions, compositions);
    }
}
