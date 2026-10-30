using System.Diagnostics.CodeAnalysis;
using AgentMemory.Extensibility.Modules;

namespace AgentMemory.Extensibility.Reading;

/// <summary>
/// The host-level read capability traversal context walks with (coding packs CMP-1). Unlike <see cref="IModuleReader"/> it is not
/// bound to one calling module: the host grants it to the walk, which reads only what manifests declare traversable, live
/// records only, within the scope the host's isolation policy resolves.
/// </summary>
[Experimental("AMEXT001")]
public interface IModuleTraversalReader
{
    /// <summary>
    /// The live records of one type whose anchor property matches the anchors by the walk's rules (pack format v1.2 §6.1,
    /// <see cref="ModuleAnchorQuery.Quality"/>), best match first and then in the format's order (key, then id, ordinal), at
    /// most the limit: never cut before matching (review 2: the first 512 records holding the property were returned, so
    /// matches past them never seeded), and never more than the walk can keep (review 6, M2: a short anchor returned up to
    /// 250,000 full records, which the walk then matched one by one). A store may scan a superset; it returns only matches.
    /// </summary>
    Task<ModuleReadResult> MatchAsync(ModuleAnchorQuery query, ModuleReadContext context, CancellationToken cancellationToken = default);

    /// <summary>One hop through one owned link: for each starting record, the live records it leads to, at most the fan-out each.</summary>
    Task<ModuleTraversalStep> ExpandAsync(ModuleExpandQuery query, ModuleReadContext context, CancellationToken cancellationToken = default);
}

/// <summary>The records of one type whose anchor property may match a turn's anchors.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleAnchorQuery
{
    /// <summary>Most records one anchor query returns: a snapshot's whole size, so a match is never cut off before it is matched.</summary>
    public const int MaximumLimit = 250_000;

    /// <summary>The module that owns the type.</summary>
    public required string ModuleId { get; init; }
    /// <summary>The owned type.</summary>
    public required string Type { get; init; }
    /// <summary>The declared string property.</summary>
    public required string Property { get; init; }
    /// <summary>How the property's value is compared.</summary>
    public required ModuleAnchorMatch Match { get; init; }
    /// <summary>The anchor values of the matching kind.</summary>
    public required IReadOnlyList<string> Values { get; init; }
    /// <summary>Most records returned, the best matches.</summary>
    public int Limit { get; init; } = 512;
    /// <summary>
    /// The time the walk has left for this read, or null for none: a store that can stop a statement on its server stops it then
    /// (review 7, W3: the budget reached a store only as a token, which a statement in progress never looks at).
    /// </summary>
    public TimeSpan? Budget { get; init; }

    /// <summary>
    /// How well a stored value matches the anchors by the walk's rules, 0 for not at all: the one measure every store ranks
    /// its matches by, so that each returns the same best ones (review 6, M2).
    /// </summary>
    public static double Quality(ModuleAnchorMatch match, string? value, IEnumerable<string> anchors) =>
        Context.Traversal.AnchorMatcher.Best(match, value, anchors ?? throw new ArgumentNullException(nameof(anchors))).Quality;

    /// <summary>
    /// <see cref="Quality(ModuleAnchorMatch, string?, IEnumerable{string})"/> for this query's match and non-blank values, with the
    /// anchors worked out once for every value a store matches (review 7, W1: each record normalized every anchor again). Each
    /// value's match is bounded in work, the same in every store, so a store checks its time budget on every record.
    /// </summary>
    public Func<string?, double> Matcher()
    {
        var anchors = new Context.Traversal.AnchorMatcher.Anchors(Values.Where(v => !IsBlank(v)));
        var match = Match;
        return value => Context.Traversal.AnchorMatcher.Best(match, value, anchors).Quality;
    }

    /// <summary>
    /// A path as the walk compares it, which a store's own filter must keep to (pack format v1.2 §6.1): trimmed of ASCII white
    /// space, a forward slash for each backslash, without leading './' or '/'; compared with A-Z folded to a-z, nothing else.
    /// </summary>
    public static string NormalizedPath(string value) => Context.Traversal.AnchorMatcher.Normalized(value);

    /// <summary>The simple name of a SCIP symbol, as a name anchor matches it (pack format v1.2 §6.1).</summary>
    public static string SimpleName(string symbol) => Context.Traversal.AnchorMatcher.SimpleName(symbol);

    /// <summary>Whether an anchor value is blank as the walk reads it: empty or ASCII white space only (pack format v1.2 §6.1).</summary>
    public static bool IsBlank(string? value) => Context.Traversal.AnchorMatcher.IsBlank(value);
}

/// <summary>One hop through one owned link from a set of live records.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleExpandQuery
{
    /// <summary>The module that owns the link.</summary>
    public required string ModuleId { get; init; }
    /// <summary>The owned relationship name.</summary>
    public required string Relation { get; init; }
    /// <summary>Out: the ids are the link's sources. In: the ids are records of its target type, and the hop finds their sources.</summary>
    public required ModuleTraversalDirection Direction { get; init; }
    /// <summary>Live version ids of the starting records.</summary>
    public required IReadOnlyList<string> FromIds { get; init; }
    /// <summary>Most records one starting record leads to.</summary>
    public int FanOut { get; init; } = 8;
    /// <summary>Most question words one expansion ranks by.</summary>
    public const int MaximumWords = 16;
    /// <summary>The longest question word a walk ranks by; a store counts the same words (review 2).</summary>
    public const int MaximumWordLength = 40;

    /// <summary>A question word as a store compares it: trimmed of ASCII white space, A-Z folded to a-z, nothing else (§6.1).</summary>
    public static string Word(string value) => Context.Traversal.AnchorMatcher.Folded(Context.Traversal.AnchorMatcher.Trimmed(value));
    /// <summary>The count at which a degree stops (a record's specificity is 1 over its degree, counted up to this).</summary>
    public const int MaximumScan = 1024;

    /// <summary>
    /// Most links of one relation a record may have and still be expanded through it (red-team M3, review 2): every neighbour of
    /// a record within it is ranked, in the format's order, so two stores choose the same; a record past it is not expanded,
    /// and the store says so. Counted as distinct live records in scope at the other end: for an outgoing hop, what the
    /// starting record's own links lead to; for an incoming one, the records that link to any version of it (review 3, 4).
    /// </summary>
    public const int MaximumLinks = 16_384;
    /// <summary>
    /// The question's words, lower case (CMP-4). When a starting record leads to more than the fan-out, the reader keeps first the
    /// records whose text (<see cref="Modules.ModuleType.TextProperties"/>) holds most of them, then the most specific: those
    /// fewest records share the link with. Empty keeps the most specific first.
    /// </summary>
    public IReadOnlyList<string> Words { get; init; } = [];
    /// <summary>
    /// The time the walk has left for this read, or null for none: a store that can stop a statement on its server stops it then
    /// (review 7, W3: the budget reached a store only as a token, which a statement in progress never looks at).
    /// </summary>
    public TimeSpan? Budget { get; init; }
}

/// <summary>What one hop reached.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleTraversalStep
{
    /// <summary>Read disposition; refusal reaches nothing.</summary>
    public required ModuleReadStatus Status { get; init; }
    /// <summary>Each reached record with the starting record that led to it.</summary>
    public IReadOnlyList<ModuleTraversalEdge> Edges { get; init; } = [];
    /// <summary>Refusal details.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];
}

/// <summary>A reached live record and the record it was reached from.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleTraversalEdge
{
    /// <summary>The live version id of the starting record.</summary>
    public required string FromId { get; init; }
    /// <summary>The reached live record.</summary>
    public required ModuleRecord To { get; init; }
    /// <summary>
    /// How many records share this link with the reached one: for an incoming hop, how many records of this relation the reached
    /// record links to; for an outgoing hop, how many link to it. A test linked to one type says more about it than one linked to
    /// twelve. At least 1.
    /// </summary>
    public int Degree { get; init; } = 1;
}
