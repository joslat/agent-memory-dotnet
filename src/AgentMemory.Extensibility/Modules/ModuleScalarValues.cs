// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentMemory.Extensibility.Modules;

internal static class ModuleScalarValues
{
    private static readonly Regex Timestamp = new(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,9})?(?:Z|[+-][0-9]{2}:[0-9]{2})\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    internal static bool NumberFits(JsonElement value, string type)
    {
        try
        {
            return value.ValueKind == JsonValueKind.Number && (type == "int" ? value.TryGetInt64(out _)
                : value.TryGetDouble(out var number) && double.IsFinite(number));
        }
        catch (ObjectDisposedException) { return false; }
    }

    /// <summary>
    /// Whether a declared bound fits its numeric type (review 3): as <see cref="NumberFits"/>, except that an int bound may be
    /// written as an integral number (2.0), as the manifest's other integers and JSON Schema's integer read it. Record values stay
    /// strict.
    /// </summary>
    internal static bool BoundFits(JsonElement value, string type) =>
        NumberFits(value, type) || type == "int" && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d)
            && d == Math.Floor(d) && d is >= -9.2233720368547748E18 and < 9.2233720368547758E18;

    /// <summary>An int bound as a number, written 2 or 2.0.</summary>
    internal static long Int(JsonElement value) => value.TryGetInt64(out var number) ? number : (long)value.GetDouble();

    internal static bool ValueFits(JsonElement value, string type, bool allowNull, bool allowPlaceholders = true)
    {
        if (value.ValueKind == JsonValueKind.Null) return allowNull;
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            if (allowPlaceholders && text == "$now") return type == "datetime";
            if (allowPlaceholders && text == "$session") return type == "string";
            return type switch
            {
                "string" => true,
                "date" => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
                "datetime" => Timestamp.IsMatch(text) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
                _ => false
            };
        }
        return type switch
        {
            "int" or "float" => NumberFits(value, type),
            "bool" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            _ => false
        };
    }
}
