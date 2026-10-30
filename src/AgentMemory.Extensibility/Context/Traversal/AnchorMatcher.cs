using System.Diagnostics.CodeAnalysis;
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

    /// <summary>
    /// The most work one stored value's match may take, in characters compared (review 7, W1: one value of many pieces, or a long
    /// run of "./", cost seconds a record, and the scans looked at the budget only every 1,024 records). Past it the value keeps
    /// the best match found so far, in the one order every store uses (anchors as given, then pieces in order), so the stores
    /// still agree, and a record costs at most about a millisecond whatever it holds.
    /// </summary>
    internal const long MaximumWork = 1L << 20;

    private static readonly char[] Separators = [' ', '\t', '\r', '\n', ',', ';'];
    private static readonly char[] NameSeparators = ['\r', '\n', ',', ';'];

    // ASCII only, as the format states (review 3): white space is these six characters, and only A-Z fold to a-z. Unicode
    // folding differed between .NET, Cypher and the spec (µ and μ, final Σ, İ), so the stores seeded different records.
    private static readonly char[] AsciiSpace = [' ', '\t', '\r', '\n', '\f', '\v'];

    private static bool IsAsciiSpace(char c) => c is ' ' or '\t' or '\r' or '\n' or '\f' or '\v';

    /// <summary>The text without leading and trailing ASCII white space.</summary>
    internal static string Trimmed(string value) => value.Trim(AsciiSpace);

    /// <summary>Whether the text is empty or ASCII white space only.</summary>
    internal static bool IsBlank(string? value) => value is null || value.AsSpan().Trim(AsciiSpace).IsEmpty;

    /// <summary>The text with A-Z as a-z and every other character as it is.</summary>
    internal static string Folded(string value) =>
        string.Create(value.Length, value, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++) span[i] = source[i] is >= 'A' and <= 'Z' ? (char)(source[i] + 32) : source[i];
        });

    private static bool SameFolded(string a, string b) => a.Length == b.Length && string.Equals(Folded(a), Folded(b), StringComparison.Ordinal);

    /// <summary>A turn's anchors as matching compares them, worked out once for every record a read matches (review 7, W1).</summary>
    internal sealed class Anchors
    {
        internal Anchors(IEnumerable<string> anchors) =>
            Items = [.. anchors.Select(a => new Anchor(a, Path(a), Folded(Path(a)), Trimmed(a), SimpleName(a)))];

        internal IReadOnlyList<Anchor> Items { get; }
    }

    /// <summary>One anchor: as given, as a path, the path folded, trimmed, and its simple name.</summary>
    internal sealed record Anchor(string Raw, string Path, string FoldedPath, string Trimmed, string Simple)
    {
        internal int Slashes { get; } = Path.Count(c => c == '/');
    }

    /// <summary>The best match of one stored value against the anchors: its quality (0 for none), the anchor and the pattern.</summary>
    internal static (double Quality, string? Anchor, string? Pattern) Best(ModuleAnchorMatch match, string? value, IEnumerable<string> anchors) =>
        Best(match, value, new Anchors(anchors));

    /// <summary>The best match of one stored value against anchors prepared once: its quality, the anchor and the pattern.</summary>
    internal static (double Quality, string? Anchor, string? Pattern) Best(ModuleAnchorMatch match, string? value, Anchors anchors)
    {
        if (IsBlank(value)) return (0, null, null);
        // Paths split on whitespace too; names do not, because a SCIP identity holds spaces.
        var patterns = match switch
        {
            ModuleAnchorMatch.Glob or ModuleAnchorMatch.Prefix => Pieces(value!, Separators),
            ModuleAnchorMatch.Name => Pieces(value!, NameSeparators),
            _ => [Trimmed(value!)],
        };
        long work = value!.Length;
        // Each piece worked out once: its path, folded, and for a glob its tokens (review 6, M4, and review 7, W2: a regex per
        // piece hit the engine's size limit and threw; the tokens are matched in time linear in both lengths, with no limit).
        var paths = match == ModuleAnchorMatch.Name ? null : patterns.Select(p => Path(p)).ToArray();
        var folded = paths?.Select(Folded).ToArray();
        var globs = match == ModuleAnchorMatch.Glob ? folded!.Select(p => p.Length > 0 && p.IndexOfAny(['*', '?']) >= 0 ? GlobPattern.Of(p) : null).ToArray() : null;
        (double Quality, string? Anchor, string? Pattern) best = (0, null, null);
        foreach (var anchor in anchors.Items)
            for (var i = 0; i < patterns.Length; i++)
            {
                var quality = match switch
                {
                    ModuleAnchorMatch.Exact => paths![i].Length > 0 && Equal(folded![i], anchor.FoldedPath) ? Exact : 0,
                    ModuleAnchorMatch.Prefix => PrefixQuality(folded![i].TrimEnd('/'), anchor),
                    ModuleAnchorMatch.Glob => globs![i] is { } glob ? (anchor.Path.Length > 0 && glob.Matches(anchor.FoldedPath, ref work) ? Globbed : 0)
                        : folded![i].Length == 0 || anchor.Path.Length == 0 ? 0
                        : folded[i].EndsWith('/') ? PrefixQuality(folded[i].TrimEnd('/'), anchor)
                        : Equal(folded[i], anchor.FoldedPath) ? Exact : 0,
                    ModuleAnchorMatch.Name => NameQuality(patterns[i], anchor),
                    _ => 0,
                };
                if (quality > best.Quality) best = (quality, anchor.Raw, patterns[i]);
                work += patterns[i].Length + anchor.Raw.Length;
                if (work > MaximumWork) return best;
            }
        return best;
    }

    private static bool Equal(string a, string b) => string.Equals(a, b, StringComparison.Ordinal);

    // The pieces of a value between separators, each trimmed of ASCII white space, empty ones dropped.
    private static string[] Pieces(string value, char[] separators) =>
        [.. value.Split(separators).Select(Trimmed).Where(piece => piece.Length > 0)];

    internal static double Quality(ModuleAnchorMatch match, string pattern, string anchor) => Best(match, pattern, [anchor]).Quality;

    /// <summary>
    /// Whether a seed's pattern is its anchor, as its reason shows it (review 3: the raw texts were compared): the same path,
    /// normalized and folded as matching compares them.
    /// </summary>
    internal static bool SamePath(string pattern, string anchor) => SameFolded(Path(pattern), Path(anchor));

    /// <summary>A path as matching compares it (review 2: stores filter with it too): ASCII-trimmed, '/' for '\', no leading './' or '/'.</summary>
    internal static string Normalized(string value) => Path(value);

    // Repository-relative with forward slashes: "./src\a.cs" and "src/a.cs" are the same file; a dot file keeps its dot. In closed
    // form, one pass (review 7, W1: the loop "repeated until nothing changes" ran once per alternation of "./" and white space,
    // quadratic in the value): '\' as '/', trailing ASCII white space dropped, then the leading run of ASCII white space, '/' and
    // "./" dropped. It equals that loop's fixpoint, which the tests keep as the oracle, so a normalized path normalizes to itself.
    private static string Path(string value)
    {
        var end = value.Length;
        while (end > 0 && IsAsciiSpace(value[end - 1])) end--;
        var start = 0;
        while (start < end)
        {
            var c = value[start];
            if (IsAsciiSpace(c) || c is '/' or '\\') start++;
            else if (c == '.' && start + 1 < end && value[start + 1] is '/' or '\\') start += 2;
            else break;
        }
        return value[start..end].Replace('\\', '/');
    }

    private static double PrefixQuality(string foldedPrefix, Anchor anchor)
    {
        if (foldedPrefix.Length == 0 || anchor.Path.Length == 0) return 0;
        if (Equal(foldedPrefix, anchor.FoldedPath)) return Exact;
        if (anchor.FoldedPath.Length <= foldedPrefix.Length || anchor.FoldedPath[foldedPrefix.Length] != '/'
            || !anchor.FoldedPath.StartsWith(foldedPrefix, StringComparison.Ordinal)) return 0;
        // A deeper folder says more about the file: src/a/b/ beats src/ for src/a/b/c.cs.
        var depth = (double)foldedPrefix.Count(c => c == '/') + 1;
        var total = (double)anchor.Slashes + 1;
        return PrefixBase + 0.3 * Math.Min(1, depth / total);
    }

    private static double NameQuality(string value, Anchor anchor)
    {
        value = Trimmed(value);
        if (value.Length == 0) return 0;
        if (Equal(value, anchor.Trimmed)) return Exact;
        var simpleValue = SimpleName(value);
        return simpleValue.Length > 0 && Equal(simpleValue, anchor.Simple) ? Named : 0;
    }

    /// <summary>
    /// The simple name of a SCIP symbol (<c>… Storage/Neo4jModuleStore#</c> → <c>Neo4jModuleStore</c>; <c>…#RemovePackAsync().</c>
    /// → <c>RemovePackAsync</c>), or the value itself when it is already one: the text after the last space, without trailing
    /// '#', '.', '/', ':' and '!', cut at the last '(' when it ends with ')', then what follows the last '/', '#', '.' or ':'.
    /// </summary>
    internal static string SimpleName(string symbol)
    {
        var s = Trimmed(symbol);
        var space = s.LastIndexOf(' ');
        if (space >= 0) s = s[(space + 1)..];
        s = s.TrimEnd('#', '.', '/', ':', '!');
        if (s.EndsWith(')')) s = s[..(s.LastIndexOf('(') is var open and >= 0 ? open : s.Length)];
        var cut = s.LastIndexOfAny(['/', '#', '.', ':']);
        return cut >= 0 ? s[(cut + 1)..] : s;
    }

    /// <summary>
    /// A glob over ASCII-folded paths, matched by stepping a set of positions through the text (review 7, W2: it was a regex
    /// per piece, which threw past the engine's size limit). '*' is any run without '/', '?' one character but '/', "**/" zero or
    /// more folders, a bare "**" anything, every other character itself. Runs of wildcards that mean the same collapse to one,
    /// so between two characters the text must hold there are at most two wildcards, and a pattern that needs more characters than
    /// the text has fails at once: the work is at most the text's length times about three times its own.
    /// </summary>
    internal sealed class GlobPattern
    {
        private enum Kind : byte { Literal, One, Run, Any, Folders }

        private readonly (Kind Kind, char Char)[] _tokens;
        private readonly int _needs;
        // The position sets, kept for every text this pattern is matched against (one Best call, one thread).
        private bool[] _at, _next, _inside, _insideNext;

        private GlobPattern((Kind, char)[] tokens, int needs)
        {
            (_tokens, _needs) = (tokens, needs);
            (_at, _next, _inside, _insideNext) = (new bool[tokens.Length + 1], new bool[tokens.Length + 1], new bool[tokens.Length], new bool[tokens.Length]);
        }

        internal static GlobPattern Of(string folded)
        {
            var tokens = new List<(Kind Kind, char Char)>(Math.Min(folded.Length, 1024));
            var needs = 0;
            void Add(Kind kind, char c)
            {
                if (kind is Kind.Literal or Kind.One) { needs++; tokens.Add((kind, c)); return; }
                if (tokens.Count > 0 && tokens[^1].Kind is var last and not (Kind.Literal or Kind.One))
                {
                    // "**" swallows any wildcard beside it; two of one kind are one.
                    if (last == Kind.Any || last == kind) return;
                    if (kind == Kind.Any) { tokens[^1] = (Kind.Any, '\0'); return; }
                }
                tokens.Add((kind, '\0'));
            }
            for (var i = 0; i < folded.Length; i++)
            {
                var c = folded[i];
                if (c == '*' && i + 1 < folded.Length && folded[i + 1] == '*')
                {
                    var slash = i + 2 < folded.Length && folded[i + 2] == '/';
                    Add(slash ? Kind.Folders : Kind.Any, '\0');
                    i += slash ? 2 : 1;
                }
                else if (c == '*') Add(Kind.Run, '\0');
                else if (c == '?') Add(Kind.One, '\0');
                else Add(Kind.Literal, c);
            }
            return new([.. tokens], needs);
        }

        /// <summary>Whether the folded text matches, charging the characters stepped times the positions held to <paramref name="work"/>.</summary>
        internal bool Matches(string text, ref long work)
        {
            if (_needs > text.Length) return false;
            var n = _tokens.Length;
            // at[k]: the text so far can be followed by tokens k..; inside[k]: within a "**/" at k, before its closing '/'.
            var (at, next, inside, insideNext) = (_at, _next, _inside, _insideNext);
            Array.Clear(at);
            Array.Clear(inside);
            at[0] = true;
            Close(at);
            foreach (var c in text)
            {
                work += n + 1;
                Array.Clear(next);
                Array.Clear(insideNext);
                var any = false;
                for (var k = 0; k < n; k++)
                {
                    if (inside[k]) { insideNext[k] = true; if (c == '/') next[k + 1] = true; any = true; }
                    if (!at[k]) continue;
                    switch (_tokens[k].Kind)
                    {
                        case Kind.Literal: if (c == _tokens[k].Char) { next[k + 1] = true; any = true; } break;
                        case Kind.One: if (c != '/') { next[k + 1] = true; any = true; } break;
                        case Kind.Run: if (c != '/') { next[k] = true; any = true; } break;
                        case Kind.Any: next[k] = true; any = true; break;
                        case Kind.Folders: insideNext[k] = true; if (c == '/') next[k + 1] = true; any = true; break;
                    }
                }
                if (!any) return Keep(at, next, inside, insideNext, false);
                Close(next);
                (at, next) = (next, at);
                (inside, insideNext) = (insideNext, inside);
            }
            return Keep(at, next, inside, insideNext, at[n]);
        }

        private bool Keep(bool[] at, bool[] next, bool[] inside, bool[] insideNext, bool result)
        {
            (_at, _next, _inside, _insideNext) = (at, next, inside, insideNext);
            return result;
        }

        // What follows without consuming a character: past a wildcard that may match nothing.
        private void Close(bool[] positions)
        {
            for (var k = 0; k < _tokens.Length; k++)
                if (positions[k] && _tokens[k].Kind is Kind.Run or Kind.Any or Kind.Folders) positions[k + 1] = true;
        }
    }
}
