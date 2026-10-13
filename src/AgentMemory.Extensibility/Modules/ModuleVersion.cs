using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace AgentMemory.Extensibility.Modules;

/// <summary>A validated SemVer 2 module version. Build metadata does not affect precedence.</summary>
/// <remarks>Value equality retains the complete spelling, including metadata; use CompareTo for precedence.</remarks>
[Experimental("AMEXT001")]
public sealed record ModuleVersion : IComparable<ModuleVersion>
{
    private static readonly Regex Pattern = new(
        @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private ModuleVersion(string value) => Value = value;

    /// <summary>The original validated version, including optional build metadata.</summary>
    public string Value { get; }

    /// <summary>Parses a complete major.minor.patch version; malformed input throws FormatException.</summary>
    public static ModuleVersion Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var match = Pattern.Match(value);
        if (!match.Success || (match.Groups[4].Success && match.Groups[4].Value.Split('.')
                .Any(part => IsNumeric(part) && part.Length > 1 && part[0] == '0')))
            throw new FormatException($"Invalid SemVer 2 module version '{value}'.");
        return new ModuleVersion(value);
    }

    /// <inheritdoc/>
    public int CompareTo(ModuleVersion? other)
    {
        if (other is null) return 1;
        var left = Value.Split('+')[0].Split('-', 2);
        var right = other.Value.Split('+')[0].Split('-', 2);
        var leftCore = left[0].Split('.');
        var rightCore = right[0].Split('.');
        for (var i = 0; i < 3; i++)
        {
            var comparison = CompareNumeric(leftCore[i], rightCore[i]);
            if (comparison != 0) return comparison;
        }
        if (left.Length == 1 || right.Length == 1)
            return right.Length.CompareTo(left.Length);

        var leftPre = left[1].Split('.');
        var rightPre = right[1].Split('.');
        for (var i = 0; i < Math.Min(leftPre.Length, rightPre.Length); i++)
        {
            var ln = IsNumeric(leftPre[i]);
            var rn = IsNumeric(rightPre[i]);
            var comparison = ln && rn ? CompareNumeric(leftPre[i], rightPre[i])
                : ln != rn ? (ln ? -1 : 1) : string.CompareOrdinal(leftPre[i], rightPre[i]);
            if (comparison != 0) return comparison;
        }
        return leftPre.Length.CompareTo(rightPre.Length);
    }

    /// <inheritdoc/>
    public override string ToString() => Value;

    private static bool IsNumeric(string value) => value.All(char.IsAsciiDigit);
    private static int CompareNumeric(string left, string right) => left.Length != right.Length
        ? left.Length.CompareTo(right.Length) : string.CompareOrdinal(left, right);
}
