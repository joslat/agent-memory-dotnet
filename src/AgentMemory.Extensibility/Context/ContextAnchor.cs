using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Extensibility.Context;

/// <summary>What a turn is about in the host's world: the file being edited or a symbol it names (coding packs CMP-1).</summary>
/// <param name="Kind">A repository path or a symbol.</param>
/// <param name="Value">A repository-relative path with forward slashes, or a symbol's SCIP identity or simple name.</param>
/// <remarks>Anchors start a traversal walk; with none, traversal does nothing. They are what the host claims, never instructions:
/// a value that matches no record simply reaches nothing.</remarks>
[Experimental("AMEXT001")]
public sealed record ContextAnchor(ContextAnchorKind Kind, string Value)
{
    /// <summary>Most anchors a request carries; a host passes the few that matter.</summary>
    public const int MaximumPerRequest = 32;
    /// <summary>Longest anchor value accepted.</summary>
    public const int MaximumLength = 512;

    /// <summary>Where the host found it, for the trace: <c>prompt</c>, <c>working-tree</c>, <c>tool</c> or a host's own word.</summary>
    public string? Source { get; init; }

    /// <summary>
    /// A repository path anchor; ASCII white space trimmed, backslashes become forward slashes and a leading <c>./</c> is dropped.
    /// Only ASCII white space, as the walk reads anchors (review 4: a trailing non-breaking space was trimmed here and kept by a
    /// host that built the record itself).
    /// </summary>
    public static ContextAnchor Path(string path, string? source = null) =>
        new(ContextAnchorKind.Path, Normalized(ContextAnchorKind.Path, path ?? throw new ArgumentNullException(nameof(path)))) { Source = source };

    /// <summary>A symbol anchor: a SCIP identity or a simple name, ASCII white space trimmed.</summary>
    public static ContextAnchor Symbol(string symbol, string? source = null) =>
        new(ContextAnchorKind.Symbol, Normalized(ContextAnchorKind.Symbol, symbol ?? throw new ArgumentNullException(nameof(symbol)))) { Source = source };

    /// <summary>
    /// A value as the walk's intake normalizes it (pack format v1.2 §6.1), so a factory and the record's own constructor give the
    /// walk the same anchor (review 5): a path ASCII-trimmed, backslashes as forward slashes, leading <c>./</c> and <c>/</c>
    /// removed until nothing changes; a symbol ASCII-trimmed.
    /// </summary>
    /// A value past the bound the intake drops is left as given (review 6, L1: the factories normalized it first).
    internal static string Normalized(ContextAnchorKind kind, string value) =>
        value.Length > 4 * MaximumLength ? value
        : kind == ContextAnchorKind.Path ? Traversal.AnchorMatcher.Normalized(value) : Traversal.AnchorMatcher.Trimmed(value);
}

/// <summary>What an anchor names.</summary>
[Experimental("AMEXT001")]
public enum ContextAnchorKind
{
    /// <summary>A repository-relative file or folder path.</summary>
    Path,
    /// <summary>A code symbol.</summary>
    Symbol,
}
