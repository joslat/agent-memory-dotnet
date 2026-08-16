namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// What projection computed about a recalled context, keyed so any render surface can consume it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Annotations and blocks, deliberately not pre-rendered text.</b> Three surfaces render recalled
/// memory — the Core Markdown formatter, the Agent Framework <c>ChatMessage</c> mapper, and the
/// benchmark prompt builder — and they have genuinely different output shapes. A pre-rendered string
/// would force one shape on all three and would bypass the admission and delimiting machinery that
/// keeps recalled content from speaking with system authority. Annotations keyed by item id let each
/// surface keep its own security-checked assembly while every projection <i>decision</i> is made
/// exactly once, which is the drift this layer exists to kill: a rendering fix used to land in three
/// places or in one and rot in the other two.
/// </para>
/// <para>
/// Null on <see cref="MemoryContext.Projection"/> means no feature was enabled, and that is the
/// default. Null is not "empty projection" — it is the signal for every surface to take its exact
/// pre-existing path.
/// </para>
/// </remarks>
public sealed record ProjectedContext
{
    /// <summary>Per-item annotations, keyed by item id (fact/entity/preference/trace/message id).</summary>
    public IReadOnlyDictionary<string, ProjectedItemAnnotation> Annotations { get; init; } =
        new Dictionary<string, ProjectedItemAnnotation>(StringComparer.Ordinal);

    /// <summary>Standalone blocks that belong to a section rather than to any one item.</summary>
    public IReadOnlyList<ProjectedBlock> Blocks { get; init; } = Array.Empty<ProjectedBlock>();

    /// <summary>
    /// Section key → item ids in render order. Present <b>only</b> for sections whose order projection
    /// actually changed, so an absent entry means "render as retrieved".
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> SectionOrder { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
}

/// <summary>What projection knows about one recalled item.</summary>
public sealed record ProjectedItemAnnotation
{
    /// <summary>
    /// The retrieval similarity score, or null when the provider could not supply one.
    /// </summary>
    /// <remarks>
    /// <b>Null, never 0.0.</b> An unscoreable provider and a genuinely terrible match are different
    /// facts, and collapsing them would let an unscored section emit confident near-miss marks — a
    /// fabricated abstention cue, which is worse than no cue at all.
    /// </remarks>
    public double? Score { get; init; }

    /// <summary>The score fell below the configured threshold: render this as a closest match, not a match.</summary>
    public bool IsNearMiss { get; init; }

    /// <summary>e.g. <c>"(since 2023-05-12; previously Globex)"</c>. Null when nothing superseded this item.</summary>
    public string? SupersessionNote { get; init; }

    /// <summary>
    /// What shape a recalled procedure has, e.g. <c>"(16 steps)"</c>. Null for anything else.
    /// </summary>
    /// <remarks>
    /// Its own field rather than sharing <see cref="SupersessionNote"/>, which is what a first pass
    /// did: the two say unrelated things, and a later reader debugging supersession would have found
    /// a step count sitting in a property whose documentation promises a supersession chain. Cheap
    /// field, honest name.
    /// </remarks>
    public string? ProcedureShape { get; init; }

    /// <summary>The raw source sentence, NOT pre-wrapped — each surface wraps it its own way.</summary>
    public string? SourceQuote { get; init; }

    /// <summary>The item's real source date, from message metadata where available.</summary>
    public string? SourceDate { get; init; }
}

/// <summary>The kinds of standalone block projection can emit.</summary>
/// <remarks>
/// <para>
/// <b>Only the first two are emitted.</b> The other three were reserved in Wave B on the reasoning that
/// the working-memory, due-reminder and delta features would need "only a slot here", so naming them
/// early would stop each inventing a rendering path of its own.
/// </para>
/// <para>
/// <b>That is not how they shipped.</b> Wave C built all three, and each turned out to need a whole
/// ordered section rather than a block inside someone else's — the profile renders ahead of every
/// probabilistic section, and reminders render ahead of the query's own answer. So all three render
/// through the existing per-section path, symmetrically on both surfaces
/// (<c>MemoryContextFormatter</c> and the MAF <c>MafTypeMapper</c>), and these three members are
/// unused. They are kept because removing a public enum member is a breaking change, not because
/// anything is waiting on them.
/// </para>
/// <para>
/// The prediction was wrong in a specific and useful way: the thing that prevents a fourth rendering
/// path is a shared <i>section</i> pipeline, which exists, not a reserved block kind.
/// </para>
/// </remarks>
public enum ProjectedBlockKind
{
    /// <summary>Nothing in this section matched the query well enough to be treated as an answer.</summary>
    NoDirectMatch,

    /// <summary>Two live recalled facts contradict each other.</summary>
    ConflictingMemory,

    /// <summary>
    /// UNUSED — the working-memory tier renders as its own section, not as a block. See the remarks
    /// on this enum.
    /// </summary>
    WorkingMemoryProfile,

    /// <summary>UNUSED — prospective firing renders as its own section. See the remarks on this enum.</summary>
    DueReminders,

    /// <summary>UNUSED — delta recall is returned as a typed result, not rendered as a block.</summary>
    DeltaSummary,
}

/// <summary>One standalone projected block, belonging to a section rather than to an item.</summary>
/// <param name="Kind">What sort of block this is.</param>
/// <param name="SectionKey">The section it renders under ("facts", "entities", …).</param>
/// <param name="Text">The rendered text, without any surface-specific wrapping.</param>
public sealed record ProjectedBlock(ProjectedBlockKind Kind, string SectionKey, string Text);
