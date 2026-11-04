// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentMemory.Packs.Format;

/// <summary>
/// RFC 8785 (JSON Canonicalization Scheme) for the two files whose exact bytes are an identity: <c>META/files.json</c> and
/// <c>META/lock.json</c> (v1.1 H1). Those files hold strings, integers, booleans, null, arrays and objects only, so the
/// number rule reduces to plain integer digits; a fraction is refused rather than formatted.
/// </summary>
internal static class Jcs
{
    /// <summary>The canonical bytes of one JSON value.</summary>
    internal static byte[] Canonical(JsonElement element)
    {
        var builder = new StringBuilder();
        Write(element, builder);
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>The canonical bytes of a node, written without reflection (the library stays NativeAOT-safe).</summary>
    internal static byte[] Canonical(JsonNode node)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) node.WriteTo(writer);
        return Canonical(buffer.ToArray());
    }

    /// <summary>The canonical bytes of <paramref name="utf8"/>, which must already keep the JSON profile with integers only.</summary>
    internal static byte[] Canonical(ReadOnlySpan<byte> utf8)
    {
        using var document = JsonDocument.Parse(utf8.ToArray());
        return Canonical(document.RootElement);
    }

    /// <summary>Whether <paramref name="utf8"/> is already canonical: the profile holds and the bytes equal their canonical form.</summary>
    internal static string? CheckCanonical(ReadOnlySpan<byte> utf8)
    {
        if (PackJson.Check(utf8, integersOnly: true) is { } problem) return problem;
        return utf8.SequenceEqual(Canonical(utf8)) ? null : "is not in RFC 8785 canonical form";
    }

    private static void Write(JsonElement element, StringBuilder output)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                output.Append('{');
                var first = true;
                // RFC 8785 §3.2.3: members sorted by their names as arrays of UTF-16 code units.
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first) output.Append(',');
                    first = false;
                    WriteString(property.Name, output);
                    output.Append(':');
                    Write(property.Value, output);
                }
                output.Append('}');
                break;
            case JsonValueKind.Array:
                output.Append('[');
                var firstItem = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!firstItem) output.Append(',');
                    firstItem = false;
                    Write(item, output);
                }
                output.Append(']');
                break;
            case JsonValueKind.String:
                WriteString(element.GetString()!, output);
                break;
            case JsonValueKind.Number:
                if (!element.TryGetInt64(out var integer) || integer is < -PackJson.MaxSafeInteger or > PackJson.MaxSafeInteger)
                    throw new InvalidDataException("A canonical pack file holds integers within ±2^53 only.");
                output.Append(integer.ToString(CultureInfo.InvariantCulture));
                break;
            case JsonValueKind.True: output.Append("true"); break;
            case JsonValueKind.False: output.Append("false"); break;
            case JsonValueKind.Null: output.Append("null"); break;
            default: throw new InvalidDataException("Unexpected JSON value.");
        }
    }

    /// <summary>RFC 8785 §3.2.2.2: escape only the quote, the backslash and control characters; everything else literal.</summary>
    private static void WriteString(string value, StringBuilder output)
    {
        output.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': output.Append("\\\""); break;
                case '\\': output.Append("\\\\"); break;
                case '\b': output.Append("\\b"); break;
                case '\f': output.Append("\\f"); break;
                case '\n': output.Append("\\n"); break;
                case '\r': output.Append("\\r"); break;
                case '\t': output.Append("\\t"); break;
                default:
                    if (c < 0x20) output.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else output.Append(c);
                    break;
            }
        }
        output.Append('"');
    }
}
