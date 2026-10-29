using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;
using AgentMemory.Extensibility.Modules;

namespace AgentMemory.Extensibility.Context.Traversal;

/// <summary>How well a record's anchor property matches a turn's anchors (CMP-1): pure, so the walk and its tests agree.</summary>
[Experimental("AMEXT001")]
internal static class AnchorMatcher
{
    /// <summary>The value equals the anchor.</summary>
    internal const double Exact = 1.0;
    /// <summary>A symbol's simple name equals the anchor's.</summary>
    internal const double Named = 0.9;
    /// <summary>A glob with wildcards matches.</summary>
    internal const double Globbed = 0.7;
    /// <summary>A folder prefix contains the anchor: at least this, more the deeper the folder.</summary>
    internal const double PrefixBase = 0.5;

    private static readonly char[] Separators = [' ', '\t', '\r', '\n', ',', ';'];
    private static readonly char[] NameSeparators = ['\r', '\n', ',', ';'];

    /// <summary>The best match of one stored value against the anchors: its quality (0 for none), the anchor and the pattern.</summary>
    internal static (double Quality, string? Anchor, string? Pattern) Best(ModuleAnchorMatch match, string? value, IEnumerable<string> anchors)
    {
        if (string.IsNullOrWhiteSpace(value)) return (0, null, null);
        // Paths split on whitespace too; names do not, because a SCIP identity holds spaces.
        var patterns = match switch
        {
            ModuleAnchorMatch.Glob or ModuleAnchorMatch.Prefix => value.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            ModuleAnchorMatch.Name => value.Split(NameSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            _ => [value.Trim()],
        };
        (double Quality, string? Anchor, string? Pattern) best = (0, null, null);
        foreach (var anchor in anchors)
            foreach (var pattern in patterns)
            {
                var quality = Quality(match, pattern, anchor);
                if (quality > best.Quality) best = (quality, anchor, pattern);
            }
        return best;
    }

    internal static double Quality(ModuleAnchorMatch match, string pattern, string anchor) => match switch
    {
        ModuleAnchorMatch.Exact => string.Equals(Path(pattern), Path(anchor), StringComparison.OrdinalIgnoreCase) ? Exact : 0,
        ModuleAnchorMatch.Prefix => PrefixQuality(Path(pattern).TrimEnd('/'), Path(anchor)),
        ModuleAnchorMatch.Glob => GlobQuality(Path(pattern), Path(anchor)),
        ModuleAnchorMatch.Name => NameQuality(pattern, anchor),
        _ => 0,
    };

    /// <summary>A path as matching compares it (review 2: stores filter with it too): trimmed, '/' for '\', no leading './' or '/'.</summary>
    internal static string Normalized(string value) => Path(value);

    // Repository-relative with forward slashes: "./src\a.cs" and "src/a.cs" are the same file; a dot file keeps its dot.
    private static string Path(string value)
    {
        var path = value.Trim().Replace('\\', '/');
        while (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
        return path.TrimStart('/');
    }

    private static double PrefixQuality(string prefix, string anchor)
    {
        if (prefix.Length == 0) return 0;
        if (string.Equals(prefix, anchor, StringComparison.OrdinalIgnoreCase)) return Exact;
        if (!anchor.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)) return 0;
        // A deeper folder says more about the file: src/a/b/ beats src/ for src/a/b/c.cs.
        var depth = (double)prefix.Count(c => c == '/') + 1;
        var total = (double)anchor.Count(c => c == '/') + 1;
        return PrefixBase + 0.3 * Math.Min(1, depth / total);
    }

    private static double GlobQuality(string pattern, string anchor)
    {
        if (pattern.Length == 0) return 0;
        if (pattern.IndexOfAny(['*', '?']) < 0)
            return pattern.EndsWith('/') ? PrefixQuality(pattern.TrimEnd('/'), anchor) : string.Equals(pattern, anchor, StringComparison.OrdinalIgnoreCase) ? Exact : 0;
        return Glob(pattern).IsMatch(anchor) ? Globbed : 0;
    }

    private static readonly Dictionary<string, Regex> Globs = new(StringComparer.Ordinal);
    private static readonly object GlobsLock = new();

    private static Regex Glob(string pattern)
    {
        lock (GlobsLock)
        {
            if (Globs.TryGetValue(pattern, out var cached)) return cached;
            var regex = new StringBuilder("^");
            for (var i = 0; i < pattern.Length; i++)
            {
                var c = pattern[i];
                if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
                {
                    // "**/" matches zero or more folders; a bare "**" matches anything.
                    var slash = i + 2 < pattern.Length && pattern[i + 2] == '/';
                    regex.Append(slash ? "(?:.*/)?" : ".*");
                    i += slash ? 2 : 1;
                }
                else if (c == '*') regex.Append("[^/]*");
                else if (c == '?') regex.Append("[^/]");
                else regex.Append(Regex.Escape(c.ToString()));
            }
            var compiled = new Regex(regex.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
            if (Globs.Count < 4096) Globs[pattern] = compiled;
            return compiled;
        }
    }

    private static double NameQuality(string value, string anchor)
    {
        value = value.Trim(); anchor = anchor.Trim();
        if (string.Equals(value, anchor, StringComparison.Ordinal)) return Exact;
        var simpleValue = SimpleName(value); var simpleAnchor = SimpleName(anchor);
        return simpleValue.Length > 0 && string.Equals(simpleValue, simpleAnchor, StringComparison.Ordinal) ? Named : 0;
    }

    /// <summary>
    /// The simple name of a SCIP symbol (<c>… Storage/Neo4jModuleStore#</c> → <c>Neo4jModuleStore</c>; <c>…#RemovePackAsync().</c>
    /// → <c>RemovePackAsync</c>), or the value itself when it is already one.
    /// </summary>
    internal static string SimpleName(string symbol)
    {
        var s = symbol.Trim();
        var space = s.LastIndexOf(' ');
        if (space >= 0) s = s[(space + 1)..];
        s = s.TrimEnd('#', '.', '/', ':', '!');
        if (s.EndsWith(')')) s = s[..(s.LastIndexOf('(') is var open and >= 0 ? open : s.Length)];
        var cut = s.LastIndexOfAny(['/', '#', '.', ':']);
        return cut >= 0 ? s[(cut + 1)..] : s;
    }
}
