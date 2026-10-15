using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Extensibility.Modules;

/// <summary>A NuGet-style version interval using SemVer 2 precedence, including prereleases.</summary>
/// <remarks>Supports exact [v], bounded/unbounded intervals and bare inclusive minimums; no floating patterns.</remarks>
[Experimental("AMEXT001")]
public sealed record ModuleVersionRange
{
    private ModuleVersionRange(string value, ModuleVersion? minimum, ModuleVersion? maximum,
        bool includeMinimum, bool includeMaximum)
    {
        Value = value;
        Minimum = minimum;
        Maximum = maximum;
        IncludeMinimum = includeMinimum;
        IncludeMaximum = includeMaximum;
    }

    /// <summary>The trimmed input notation, retained for diagnostics.</summary>
    public string Value { get; }
    /// <summary>Lower bound, or null for unbounded.</summary>
    public ModuleVersion? Minimum { get; }
    /// <summary>Upper bound, or null for unbounded.</summary>
    public ModuleVersion? Maximum { get; }
    /// <summary>Whether the lower endpoint is admitted.</summary>
    public bool IncludeMinimum { get; }
    /// <summary>Whether the upper endpoint is admitted.</summary>
    public bool IncludeMaximum { get; }

    /// <summary>Parses an interval. One/two-part numeric endpoints are padded to major.minor.patch.</summary>
    public static ModuleVersionRange Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        value = value.Trim();
        if (value.Length == 0) throw new FormatException("A module version range cannot be empty.");
        if (value[0] is not ('[' or '('))
            return new ModuleVersionRange(value, Endpoint(value), null, true, false);
        if (value.Length < 3 || value[^1] is not (']' or ')'))
            throw new FormatException($"Invalid module version range '{value}'.");

        var bounds = value[1..^1].Split(',').Select(b => b.Trim()).ToArray();
        if (bounds.Length == 1 && value[0] == '[' && value[^1] == ']')
        {
            var exact = Endpoint(bounds[0]);
            return new ModuleVersionRange(value, exact, exact, true, true);
        }
        if (bounds.Length != 2 || (bounds[0].Length == 0 && value[0] != '(')
                || (bounds[1].Length == 0 && value[^1] != ')'))
            throw new FormatException($"Invalid module version range '{value}'.");
        var minimum = bounds[0].Length == 0 ? null : Endpoint(bounds[0]);
        var maximum = bounds[1].Length == 0 ? null : Endpoint(bounds[1]);
        var includeMinimum = value[0] == '[';
        var includeMaximum = value[^1] == ']';
        if (minimum is not null && maximum is not null
                && (minimum.CompareTo(maximum) > 0
                    || (minimum.CompareTo(maximum) == 0 && !(includeMinimum && includeMaximum))))
            throw new FormatException($"Module version range '{value}' is empty or reversed.");
        return new ModuleVersionRange(value, minimum, maximum, includeMinimum, includeMaximum);
    }

    /// <summary>Tests precedence against both endpoints; metadata does not affect membership.</summary>
    public bool Contains(ModuleVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var lower = Minimum is null ? 1 : version.CompareTo(Minimum);
        var upper = Maximum is null ? -1 : version.CompareTo(Maximum);
        return (lower > 0 || (lower == 0 && IncludeMinimum))
            && (upper < 0 || (upper == 0 && IncludeMaximum));
    }

    /// <inheritdoc/>
    public override string ToString() => Value;

    private static ModuleVersion Endpoint(string value)
    {
        var suffix = value.IndexOfAny(['-', '+']);
        var core = suffix < 0 ? value : value[..suffix];
        var count = core.Count(c => c == '.');
        if (count < 2) value = core + (count == 0 ? ".0.0" : ".0") + (suffix < 0 ? "" : value[suffix..]);
        return ModuleVersion.Parse(value);
    }
}
