using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Reorders the candidates a retrieval returned, before context budgeting truncates them.
/// </summary>
/// <remarks>
/// <para>
/// <b>This seam is deliberately narrower than the task that proposed it.</b> R2 asked for a reranker
/// in order to <i>fuse</i> GraphRAG as a distinct retrieval channel, and that premise was killed on
/// measurement: of 132 GraphRAG items, <b>132 were duplicates of what the structured surface had
/// already retrieved and 0 verdicts changed</b>. Fusing a channel that returns the same rows in a
/// different order cannot help, and the trigger for revisiting it — a corpus with a genuinely separate
/// knowledge graph — has not fired.
/// </para>
/// <para>
/// What survives is the <i>extension point</i>, which R6 (node-distance) and R7 (mention frequency)
/// both need and neither of which is about fusion. Keeping the seam and dropping the fusion is the
/// honest reading of that measurement: the mechanism was worth building, the use case was not.
/// </para>
/// <para>
/// <b>Reranking reorders survivors; it cannot rescue what retrieval never returned.</b> That is why
/// this ships behind the retrieval-sufficiency work rather than before it — a reranker over a starved
/// candidate set reorders seven of an owner's 504 facts and reports success. The signal now measures
/// AUC 0.709–0.768, which is what made this safe to build.
/// </para>
/// <para>
/// Implementations must be <b>pure and fast</b>: this runs inside recall, on the blocking path, once
/// per section. Anything needing a database round trip should issue <b>one bounded query</b>, never
/// one per candidate.
/// </para>
/// </remarks>
public interface IMemoryReranker
{
    /// <summary>
    /// Whether this reranker should run at all for the current request.
    /// </summary>
    /// <remarks>
    /// Checked before the candidates are materialised, so a disabled reranker costs nothing — not even
    /// the allocation of the list it would have reordered.
    /// </remarks>
    bool IsEnabled { get; }

    /// <summary>
    /// Returns <paramref name="candidates"/> reordered, or the same sequence when nothing changed.
    /// </summary>
    /// <param name="candidates">
    /// The retrieval's own ranking, best first. Scores are the provider's, comparable within this
    /// section and meaningless across sections.
    /// </param>
    /// <param name="context">What the request knows: the query, its embedding, and the owner scope.</param>
    /// <param name="cancellationToken">Cancels the reordering; a cancelled rerank leaves the provider order.</param>
    /// <remarks>
    /// <para>
    /// Returning a <b>different set</b> — adding or dropping candidates — is not supported and not
    /// checked for cheaply, so it would corrupt the section's diagnostics silently. A reranker
    /// reorders; a filter belongs in retrieval, where its effect on recall is measurable.
    /// </para>
    /// <para>
    /// A reranker that throws must not fail the recall: callers treat an exception as "no reordering"
    /// and continue with the provider's own ranking, because a degraded order is recoverable and a
    /// failed recall is not.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<MemoryContextRankedItem>> RerankAsync(
        IReadOnlyList<MemoryContextRankedItem> candidates,
        MemoryRerankContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// What a reranker is told about the request it is reordering for.
/// </summary>
/// <param name="Query">The user's query text, for a reranker that reads language.</param>
/// <param name="QueryEmbedding">
/// The query vector, when one was generated. Null on a turn narrowed to categories that need no
/// vector — a reranker depending on it must handle that rather than assume.
/// </param>
/// <param name="Scope">
/// The owner scope the retrieval ran under. A reranker issuing its own query <b>must</b> carry this:
/// a reordering pass that reads another owner's graph is an isolation breach wearing a ranking hat.
/// </param>
/// <param name="SectionKind">
/// Which recall category is being reordered, so one reranker can serve several and behave differently
/// per kind rather than pretending a fact and a trace rank alike.
/// </param>
public sealed record MemoryRerankContext(
    string? Query,
    float[]? QueryEmbedding,
    MemoryScope? Scope,
    MemoryItemKind SectionKind);
