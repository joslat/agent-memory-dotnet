using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentMemory.Packs.Format;

/// <summary>A SemVer 2.0.0 version. Precedence ignores build metadata, as the spec says; the host ledger does not (v1.1 H4).</summary>
internal sealed partial record SemVer(long Major, long Minor, long Patch, string[] Prerelease, string Build) : IComparable<SemVer>
{
    /// <summary>Longest version read: a name, never a payload; the same bound in every verifier.</summary>
    internal const int MaximumLength = 256;

    internal static bool TryParse(string text, out SemVer version)
    {
        version = null!;
        // Bounded: a version is a name, not a payload (fifth review: a megabyte of prerelease identifiers).
        if (text.Length > MaximumLength) return false;
        var match = Pattern().Match(text);
        if (!match.Success) return false;
        if (!long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !long.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !long.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch)) return false;
        var prerelease = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
        if (prerelease.Any(p => p.Length > 1 && p[0] == '0' && p.All(char.IsAsciiDigit))) return false;
        version = new SemVer(major, minor, patch, prerelease, match.Groups[5].Success ? match.Groups[5].Value : "");
        return true;
    }

    /// <summary>SemVer precedence (§11): build metadata is ignored.</summary>
    public int CompareTo(SemVer? other)
    {
        if (other is null) return 1;
        var core = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (core != 0) return core;
        if (Prerelease.Length == 0 || other.Prerelease.Length == 0) return other.Prerelease.Length.CompareTo(Prerelease.Length);
        for (var i = 0; i < Math.Min(Prerelease.Length, other.Prerelease.Length); i++)
        {
            var a = Prerelease[i];
            var b = other.Prerelease[i];
            var aNumeric = a.All(char.IsAsciiDigit);
            var bNumeric = b.All(char.IsAsciiDigit);
            var order = (aNumeric, bNumeric) switch
            {
                (true, true) => a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(a, b),
            };
            if (order != 0) return order;
        }
        return Prerelease.Length.CompareTo(other.Prerelease.Length);
    }

    [GeneratedRegex(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
