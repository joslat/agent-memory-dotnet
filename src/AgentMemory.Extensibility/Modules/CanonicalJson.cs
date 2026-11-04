// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AgentMemory.Extensibility.Modules;

/// <summary>
/// RFC 8785 (JSON Canonicalization Scheme): object members sorted by UTF-16 code units, no whitespace, strings escaped as
/// ECMAScript's JSON.stringify escapes them, numbers as ECMAScript prints a double. Any language that implements the RFC gets
/// the same bytes for the same JSON value (PACK-bind: the manifest digest an activation record binds to).
/// </summary>
internal static class CanonicalJson
{
    /// <summary>The canonical bytes of <paramref name="utf8"/>.</summary>
    /// <exception cref="FormatException">A number is not finite.</exception>
    internal static byte[] Canonical(ReadOnlySpan<byte> utf8)
    {
        using var document = JsonDocument.Parse(utf8.ToArray());
        var builder = new StringBuilder();
        Write(document.RootElement, builder);
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static void Write(JsonElement element, StringBuilder builder)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                var first = true;
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first) builder.Append(',');
                    first = false;
                    Quote(property.Name, builder);
                    builder.Append(':');
                    Write(property.Value, builder);
                }
                builder.Append('}');
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (index++ > 0) builder.Append(',');
                    Write(item, builder);
                }
                builder.Append(']');
                break;
            case JsonValueKind.String: Quote(element.GetString()!, builder); break;
            case JsonValueKind.Number: builder.Append(Number(element.GetDouble())); break;
            case JsonValueKind.True: builder.Append("true"); break;
            case JsonValueKind.False: builder.Append("false"); break;
            default: builder.Append("null"); break;
        }
    }

    private static void Quote(string value, StringBuilder builder)
    {
        builder.Append('"');
        foreach (var c in value)
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (c < ' ') builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else builder.Append(c);
                    break;
            }
        builder.Append('"');
    }

    /// <summary>A double as ECMAScript's Number.prototype.toString prints it (ECMA-262 7.1.12.1), from the shortest round-trip digits.</summary>
    internal static string Number(double value)
    {
        if (!double.IsFinite(value)) throw new FormatException("JSON numbers are finite.");
        if (value == 0) return "0";
        var shortest = value.ToString("R", CultureInfo.InvariantCulture);
        var negative = shortest[0] == '-';
        if (negative) shortest = shortest[1..];
        var e = shortest.IndexOfAny(['E', 'e']);
        var mantissa = e < 0 ? shortest : shortest[..e];
        var exponent = e < 0 ? 0 : int.Parse(shortest[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var dot = mantissa.IndexOf('.', StringComparison.Ordinal);
        var digits = dot < 0 ? mantissa : mantissa[..dot] + mantissa[(dot + 1)..];
        // n: where the decimal point falls after the first digit of `digits`; k: how many significant digits there are.
        var n = (dot < 0 ? mantissa.Length : dot) + exponent;
        var lead = 0;
        while (lead < digits.Length - 1 && digits[lead] == '0') { lead++; n--; }
        digits = digits[lead..].TrimEnd('0');
        var k = digits.Length;
        string text;
        if (k <= n && n <= 21) text = digits + new string('0', n - k);
        else if (0 < n && n <= 21) text = digits[..n] + "." + digits[n..];
        else if (-6 < n && n <= 0) text = "0." + new string('0', -n) + digits;
        else
        {
            var power = n - 1;
            text = digits[..1] + (k > 1 ? "." + digits[1..] : "") + "e" + (power >= 0 ? "+" : "-") + Math.Abs(power).ToString(CultureInfo.InvariantCulture);
        }
        return negative ? "-" + text : text;
    }
}
