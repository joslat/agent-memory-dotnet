using System.Text;
using System.Text.Json;

namespace AgentMemory.Packs.Format;

/// <summary>
/// The one JSON profile every JSON and JSONL file in a pack follows (v1.1 §2, red-team M2): UTF-8 without a byte order mark,
/// LF only, no duplicate keys, strings in Unicode normalisation form C, integers within ±2^53, depth at most 64. A strict
/// parser in another language refuses exactly what this one refuses.
/// </summary>
internal static class PackJson
{
    internal const long MaxSafeInteger = 9007199254740991; // 2^53 - 1
    private const int MaxDepth = 64;
    private static readonly JsonDocumentOptions Options = new() { MaxDepth = MaxDepth, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow };

    /// <summary>Why <paramref name="utf8"/> breaks the profile, or null when it holds one JSON value that keeps it.</summary>
    internal static string? Check(ReadOnlySpan<byte> utf8, bool integersOnly = false)
    {
        if (utf8.Length >= 3 && utf8[0] == 0xEF && utf8[1] == 0xBB && utf8[2] == 0xBF) return "starts with a byte order mark";
        if (utf8.IndexOf((byte)'\r') >= 0) return "contains a carriage return (LF only)";
        try
        {
            using var document = JsonDocument.Parse(utf8.ToArray(), Options);
            return Problem(document.RootElement, integersOnly, "$");
        }
        catch (JsonException e) { return "is not valid JSON: " + e.Message; }
        catch (InvalidOperationException e) { return "is not valid JSON: " + e.Message; }
    }

    /// <summary>Why a JSONL file breaks the profile (each non-empty line one value; the file ends with LF), or null.</summary>
    internal static string? CheckLines(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length == 0) return null;
        if (utf8[^1] != (byte)'\n') return "does not end with a line feed";
        var line = 1;
        foreach (var range in new LineEnumerator(utf8))
        {
            var slice = utf8[range];
            if (slice.Length == 0) return $"line {line} is empty";
            if (Check(slice) is { } problem) return $"line {line} {problem}";
            line++;
        }
        return null;
    }

    private static string? Problem(JsonElement element, bool integersOnly, string where)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!seen.Add(property.Name)) return $"repeats the key '{property.Name}' at {where}";
                    if (!IsNfc(property.Name)) return $"has a key that is not in NFC at {where}";
                    if (Problem(property.Value, integersOnly, where + "." + property.Name) is { } inner) return inner;
                }
                return null;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                    if (Problem(item, integersOnly, $"{where}[{index++}]") is { } inner) return inner;
                return null;
            case JsonValueKind.String:
                string value;
                try { value = element.GetString()!; }
                catch (InvalidOperationException) { return $"has a string with an unpaired surrogate at {where}"; }
                // UAX #15 Stream-Safe Text Format: at most 30 combining marks in a row, which also bounds the NFC check's work.
                if (Nfc.LongestCombiningRun(value) > Nfc.MaximumCombiningRun) return $"has more than {Nfc.MaximumCombiningRun} combining marks in a row at {where}";
                return IsNfc(value) ? null : $"has a string that is not in NFC at {where}";
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var integer))
                    return integer is >= -MaxSafeInteger and <= MaxSafeInteger ? null : $"has an integer beyond ±2^53 at {where}";
                if (element.GetRawText().AsSpan().IndexOfAny('.', 'e', 'E') < 0) return $"has an integer beyond ±2^53 at {where}";
                if (integersOnly) return $"has a number that is not an integer at {where}";
                return element.TryGetDouble(out var real) && double.IsFinite(real) ? null : $"has a number out of range at {where}";
            default:
                return null;
        }
    }

    // The same verdict on every host, whatever its globalization mode (Nfc).
    private static bool IsNfc(string value) => Nfc.IsNormalized(value);

    /// <summary>Splits on LF, without the final empty segment after the last LF.</summary>
    private ref struct LineEnumerator(ReadOnlySpan<byte> text)
    {
        private readonly ReadOnlySpan<byte> _text = text;
        private int _start;
        public Range Current { get; private set; }
        public readonly LineEnumerator GetEnumerator() => this;
        public bool MoveNext()
        {
            if (_start >= _text.Length) return false;
            var end = _text[_start..].IndexOf((byte)'\n');
            Current = _start..(_start + end);
            _start += end + 1;
            return true;
        }
    }
}
