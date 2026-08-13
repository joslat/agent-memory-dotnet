using AgentMemory.Abstractions.Options;

namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// Represents the assembled memory context for an agent run.
/// </summary>
public sealed record MemoryContext
{
    /// <summary>
    /// Session identifier for this context.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Recent conversation messages.
    /// </summary>
    public MemoryContextSection<Message> RecentMessages { get; init; } =
        MemoryContextSection<Message>.Empty;

    /// <summary>
    /// Semantically relevant past messages.
    /// </summary>
    public MemoryContextSection<Message> RelevantMessages { get; init; } =
        MemoryContextSection<Message>.Empty;

    /// <summary>
    /// Relevant entities.
    /// </summary>
    public MemoryContextSection<Entity> RelevantEntities { get; init; } =
        MemoryContextSection<Entity>.Empty;

    /// <summary>
    /// Relevant preferences.
    /// </summary>
    public MemoryContextSection<Preference> RelevantPreferences { get; init; } =
        MemoryContextSection<Preference>.Empty;

    /// <summary>
    /// Relevant facts.
    /// </summary>
    public MemoryContextSection<Fact> RelevantFacts { get; init; } =
        MemoryContextSection<Fact>.Empty;

    /// <summary>
    /// Similar past reasoning traces.
    /// </summary>
    public MemoryContextSection<ReasoningTrace> SimilarTraces { get; init; } =
        MemoryContextSection<ReasoningTrace>.Empty;

    /// <summary>
    /// Optional GraphRAG-derived context.
    /// </summary>
    public string? GraphRagContext { get; init; }


    /// <summary>
    /// The canonical relations this turn's question resolved to, empty when resolution was off or
    /// matched nothing.
    /// </summary>
    /// <remarks>
    /// Distinguishes "predicate expansion had nothing to expand" from "expansion ran and did not
    /// help", which need opposite responses — a missing vocabulary entry versus a retrieval or
    /// reading problem. The resolution was previously computed inline and discarded, so no report
    /// could tell the two apart: a question failing because its verb is absent from the table looked
    /// identical to one failing for any other reason. Verified by hand on the n=50 losses, where
    /// <c>service</c>/<c>serviced</c> turned out to be absent entirely and <c>has</c> is a
    /// deliberate query stop form.
    /// </remarks>
    public IReadOnlyList<string> ResolvedQueryRelations { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The GraphRAG passages behind <see cref="GraphRagContext"/>, with their scores and source ids.
    /// </summary>
    /// <remarks>
    /// Every other memory surface contributes typed items that evidence accounting can attribute;
    /// GraphRAG contributed one opaque string, so it could not be scored, attributed, or shown to
    /// have helped or harmed - which is the most plausible reason its recall budget was set to zero
    /// and left there.
    /// <para>
    /// The information was never missing. <c>GraphRagContextItem</c> already carried
    /// <c>SourceNodeIds</c>, <c>Score</c> and <c>Metadata</c>; the assembler joined the text and
    /// discarded the rest. This retains them. <see cref="GraphRagContext"/> is unchanged, so nothing
    /// the reader sees moves - the addition is instrumentation, not a retrieval change.
    /// </para>
    /// </remarks>
    public IReadOnlyList<GraphRagContextItem> GraphRagItems { get; init; } =
        Array.Empty<GraphRagContextItem>();

    /// <summary>
    /// The blend mode that produced this context. Determines which sources were retrieved
    /// (see <see cref="RetrievalBlendMode"/>) and the order in which memory and GraphRAG-derived
    /// context are rendered by formatters. Defaults to <see cref="RetrievalBlendMode.Blended"/>.
    /// </summary>
    public RetrievalBlendMode BlendMode { get; init; } = RetrievalBlendMode.Blended;

    /// <summary>
    /// UTC timestamp when the context was assembled.
    /// </summary>
    public required DateTimeOffset AssembledAtUtc { get; init; }

    /// <summary>
    /// True when the configured context budget forced items (or the GraphRAG block) to be dropped while
    /// assembling this context. False when everything fit within the budget (or no budget was configured).
    /// Surfaced to callers via <c>RecallResult.Truncated</c>.
    /// </summary>
    public bool Truncated { get; init; }

    /// <summary>
    /// At least one section was abandoned because the recall latency budget expired (rank 13).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Separate from <see cref="Truncated"/>, which means something else.</b> Truncation is the
    /// context budget trimming items that were retrieved; this is a retrieval that never came back.
    /// A caller told only "truncated" would reasonably conclude the memories exist and were dropped
    /// to fit — here they may exist and were never looked at.
    /// </para>
    /// <para>
    /// Which sections were lost is on their own diagnostics. This flag exists so a caller can notice
    /// at all without inspecting every section, because a partial context that looks complete is
    /// worse than a slow one: the answer is given confidently from memory that was never consulted.
    /// </para>
    /// </remarks>
    public bool LatencyBudgetExceeded { get; init; }

    /// <summary>
    /// Set when the query named a past time and recall was routed bitemporally as a result (R4).
    /// Null on an ordinary recall, and null when the caller asked for an as-of recall explicitly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is a witness, not decoration.</b> Query-time temporal resolution is opt-in and biased
    /// hard toward returning null, so the overwhelmingly common outcome of enabling it is that
    /// nothing changes — which is indistinguishable from the option not being wired, the parser never
    /// being reached, or the reference time being wrong. Every one of those reports as "temporal
    /// resolution did not help", and this project has voided six measurement runs to exactly that
    /// shape. A caller measuring the feature can require this to be non-null somewhere before
    /// believing a null result.
    /// </para>
    /// <para>
    /// Deliberately null for an explicit <c>RecallAsOfAsync</c> call: that caller already knows which
    /// instant it asked for, and reporting it here would make "the parser fired" and "someone passed
    /// a date" the same observation.
    /// </para>
    /// </remarks>
    public DateTimeOffset? ResolvedTemporalAsOf { get; init; }

    /// <summary>
    /// Additional metadata.
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } =
        new Dictionary<string, object>();
}
