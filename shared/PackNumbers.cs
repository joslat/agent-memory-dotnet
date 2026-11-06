using System.Globalization;
using System.Text.Json;

// One source compiled into both the pack format and the module host, as PackRegistry.cs is.
#if AGENTMEMORY_PACKS_FORMAT
namespace AgentMemory.Packs.Format;
#else
namespace AgentMemory.Extensibility;
#endif

/// <summary>
/// The numbers of a pack document (feature 02): integers only, within ±(2⁵³−1), each unit-bearing field in the unit and range
/// <c>spec/registry.json</c> gives it (<c>units</c>). Rates, weights and tolerances are millionths (<c>…Micro</c>), confidences
/// thousandths from a closed set (<c>…Permille</c>), latencies whole milliseconds. A number with a fraction or an exponent is
/// not an integer and refuses the document, so two languages read every number the same.
/// </summary>
internal static class PackNumbers
{
    /// <summary>One, in millionths.</summary>
    internal const long One = 1_000_000;
    /// <summary>The largest integer every JSON reader holds exactly: 2⁵³−1.</summary>
    internal const long MaxSafe = 9_007_199_254_740_991;

    /// <summary>
    /// The integer a JSON value is, or null when it is not written as an integer within ±(2⁵³−1): digits only, no fraction and no
    /// exponent. As both verifiers read every integer of the format (a JSON library's integer token, Python's <c>int</c>), so
    /// <c>800000.0</c> and <c>8e5</c> are not integers in any language.
    /// </summary>
    internal static long? Integer(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) && n is >= -MaxSafe and <= MaxSafe ? n : null;

    /// <summary>What is wrong with <paramref name="value"/> for <paramref name="field"/> by the registry's units, or null.</summary>
    internal static string? Problem(string field, long value)
    {
        if (!PackRegistry.UnitFields.TryGetValue(field, out var unit)) throw new InvalidOperationException($"The format registry gives '{field}' no unit.");
        if (unit.Values is { } values)
            return values.Contains(value) ? null : $"expected {Join(values.Order())}";
        if (value < unit.Min || value > unit.Max) return $"expected {unit.Min.ToString(CultureInfo.InvariantCulture)} to {unit.Max.ToString(CultureInfo.InvariantCulture)} ({unit.Means})";
        return null;
    }

    /// <summary>A count of millionths as a fraction of one, for display and for the arithmetic not yet exact.</summary>
    internal static double Fraction(long micro) => micro / (double)One;

    /// <summary>a × b in millionths, rounded down: the one rounding rule of the walk.</summary>
    internal static long MulFloor(long a, long b) => (long)Math.Floor((decimal)a * b / One);

    /// <summary>
    /// A decimal written by a person (<c>0.35</c>, <c>-0.05</c>) as millionths, exactly; null when it is not a decimal or has
    /// more than six decimals.
    /// </summary>
    internal static long? ParseMicro(string text) =>
        decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d)
        // Bounded before it is scaled (review F02, L8: a value past 7.9e22 overflowed instead of being refused).
        && Math.Abs(d) <= (decimal)MaxSafe / One && d * One == decimal.Truncate(d * One) ? (long)(d * One) : null;

    /// <summary>Millionths shown as the shortest decimal that is exactly them (<c>350000</c> → <c>0.35</c>).</summary>
    internal static string ShowMicro(long micro) => ((decimal)micro / One).ToString("0.######", CultureInfo.InvariantCulture);

    private static string Join(IEnumerable<long> values)
    {
        var list = values.Select(v => v.ToString(CultureInfo.InvariantCulture)).ToList();
        return list.Count < 2 ? string.Concat(list) : string.Join(", ", list[..^1]) + " or " + list[^1];
    }
}
