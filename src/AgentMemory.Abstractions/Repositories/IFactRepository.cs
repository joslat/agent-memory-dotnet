using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Abstractions.Repositories;

/// <summary>
/// Repository for fact persistence.
/// </summary>
public interface IFactRepository
{
    /// <summary>Adds or updates a fact.</summary>
    Task<Fact> UpsertAsync(Fact fact, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the most-similar existing fact with the same subject+predicate within the same owner
    /// (<paramref name="ownerId"/>; null = shared) whose cosine score ≥ <paramref name="threshold"/>,
    /// or null. Used for dedup-on-create.
    /// </summary>
    Task<Fact?> FindDuplicateAsync(
        string subject, string predicate, float[] embedding, string? ownerId, double threshold,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reinforces an existing fact reached by dedup: sets its confidence and returns it. Returns <c>null</c>
    /// if no fact with that id still exists (it may have been concurrently hard-deleted between the duplicate
    /// lookup and this call) — the caller should then create the new node instead.
    /// </summary>
    Task<Fact?> MarkDeduplicatedAsync(string factId, double confidence, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a fact by identifier. Deliberately unscoped (R1): the id is itself an already-owned handle,
    /// so no owner filter is applied. See the unscoped-reads disposition in
    /// <c>docs/archive/Memory_Review_and_Implementation_Plan.md</c>.
    /// </summary>
    Task<Fact?> GetByIdAsync(string factId, CancellationToken cancellationToken = default);

    /// <summary>Gets facts by subject.</summary>
    Task<IReadOnlyList<Fact>> GetBySubjectAsync(string subject, MemoryScope? scope = null, CancellationToken cancellationToken = default);

    /// <summary>Searches facts by vector similarity.</summary>
    Task<IReadOnlyList<(Fact Fact, double Score)>> SearchByVectorAsync(
        float[] queryEmbedding,
        int limit = 10,
        double minScore = 0.0,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches facts by vector similarity, optionally restricted to facts valid <i>now</i>.
    /// </summary>
    /// <remarks>
    /// A <b>default interface method</b> rather than a new parameter on the method above: the public
    /// surface is locked under SemVer and this is the sanctioned way to extend it without breaking a
    /// third-party implementer. The default body ignores <paramref name="validTime"/> and calls the
    /// existing overload, which is exactly the behaviour every implementation has today — so a provider
    /// that does not support valid time keeps working and simply does not filter, rather than silently
    /// appearing to.
    /// </remarks>
    Task<IReadOnlyList<(Fact Fact, double Score)>> SearchByVectorAsync(
        float[] queryEmbedding,
        ValidTimeMode validTime,
        int limit = 10,
        double minScore = 0.0,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default) =>
        SearchByVectorAsync(queryEmbedding, limit, minScore, scope, cancellationToken);

    /// <summary>
    /// Adds or updates a batch of facts atomically. Facts are merged by their {subject, predicate, object}
    /// triple scoped per owner — the same idempotency key as the single-fact upsert — so duplicate triples
    /// (whether repeated within the batch or already in the store) collapse onto one node rather than
    /// creating duplicates. The returned list is therefore deduplicated by triple: same-triple inputs are
    /// folded to the last occurrence, and each returned fact carries the surviving node's stable id (the
    /// first-created id, never a re-extraction's fresh id).
    /// </summary>
    Task<IReadOnlyList<Fact>> UpsertBatchAsync(IReadOnlyList<Fact> facts, CancellationToken cancellationToken = default);

    /// <summary>Creates an EXTRACTED_FROM relationship from a fact to a source message.</summary>
    Task CreateExtractedFromRelationshipAsync(string factId, string messageId, CancellationToken cancellationToken = default);

    /// <summary>Creates an ABOUT relationship from a fact to an entity.</summary>
    Task CreateAboutRelationshipAsync(string factId, string entityId, CancellationToken cancellationToken = default);

    /// <summary>Creates a HAS_FACT relationship from a conversation to this fact.</summary>
    Task CreateConversationFactRelationshipAsync(string conversationId, string factId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns up to <paramref name="limit"/> facts that have no embedding set.
    /// Used for batch back-fill operations.  Uses the N+1 pattern so callers can
    /// detect a next page without an extra COUNT(*) round-trip.
    /// </summary>
    Task<PagedResult<Fact>> GetPageWithoutEmbeddingAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>Sets the embedding vector on an existing fact node.</summary>
    Task UpdateEmbeddingAsync(string factId, float[] embedding, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a fact and all its relationships. When <paramref name="scope"/> is supplied (R1) the
    /// delete only affects the owner's own fact — never another owner's, and never shared/global ones.
    /// </summary>
    Task<bool> DeleteAsync(string factId, MemoryScope? scope = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft-invalidates a fact by id (D5 transaction clock): stamps <c>invalidated_at</c> so it leaves
    /// live recall but is retained — auditable, recoverable, and still visible to as-of recall for times
    /// before invalidation. Owner-scoped (R1) when <paramref name="scope"/> is set (never another owner's,
    /// never shared/global). Idempotent. Returns true if a matching fact existed in scope.
    /// </summary>
    Task<bool> InvalidateAsync(string factId, MemoryScope? scope = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Supersedes the <paramref name="loserFactId"/> with the <paramref name="winnerFactId"/> (D7): closes
    /// the loser non-destructively — stamps <c>invalidated_at</c> (drops it from live recall) and
    /// <c>valid_until</c> (closes its real-world window) — and links
    /// <c>(loser)-[:SUPERSEDED_BY]-&gt;(winner)</c>. Nothing is deleted; the loser stays visible to as-of
    /// recall for times before supersession. Owner-scoped (R1) when <paramref name="scope"/> is set: both
    /// facts must belong to the owner. Idempotent. Returns true if a matching loser+winner existed in scope.
    /// </summary>
    Task<bool> SupersedeAsync(string loserFactId, string winnerFactId, MemoryScope? scope = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// The <b>live</b> facts asserting a different object for the same subject and predicate as
    /// <paramref name="winnerFactId"/> — the ones a newly written fact about a functional relation
    /// replaces (M1 write-time supersession). Never returns the winner itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Matching is on the canonical <c>subject_key</c>/<c>predicate_key</c>/<c>object_key</c>, the same
    /// keys the write path MERGEs on, so a restatement in different words is recognised as the same
    /// assertion rather than accumulating beside it.
    /// </para>
    /// <para>
    /// <b>Default: none.</b> A store that has not implemented this simply does not perform write-time
    /// supersession — the append behaviour it already had. Returning nothing is the only safe default:
    /// throwing would break every third-party repository on a feature they never opted into, and there
    /// is no store-agnostic way to answer the question correctly.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<Fact>> FindSupersededCandidatesAsync(
        string winnerFactId,
        string subject,
        string predicate,
        string @object,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Fact>>([]);

    /// <summary>
    /// Finds an existing fact matching the subject-predicate-object triple. When <paramref name="scope"/>
    /// is supplied (R1) the lookup is confined to the owner's own and (optionally) shared facts. Null
    /// scope ⇒ unscoped.
    /// </summary>
    Task<Fact?> FindByTripleAsync(string subject, string predicate, string @object, MemoryScope? scope = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bitemporal fact search (D6): <paramref name="asOf"/> is the <b>valid-time</b> clock (a fact's
    /// <c>valid_from</c>/<c>valid_until</c> — "what was true"); <paramref name="systemAsOf"/> is the
    /// <b>transaction-time</b> clock (<c>created_at</c>/<c>invalidated_at</c> — "what we believed"), and
    /// defaults to <paramref name="asOf"/> for ordinary single-clock point-in-time recall.
    /// </summary>
    Task<IReadOnlyList<(Fact Fact, double Score)>> SearchByVectorAsOfAsync(
        float[] queryEmbedding,
        DateTimeOffset asOf,
        int limit = 10,
        double minScore = 0.0,
        MemoryScope? scope = null,
        DateTimeOffset? systemAsOf = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Every fact under the given canonical predicates, bounded — a relation retrieved <b>whole</b>.
    /// </summary>
    /// <remarks>
    /// Top-K vector search is a relevance cutoff and gives no completeness guarantee, so it cannot
    /// answer "how many": miss one of five births and the count is four. This composes with top-K
    /// rather than replacing it — similarity finds which relation matters, this returns all of it.
    /// <para>
    /// Defaults to empty so existing implementations remain source-compatible; a store that cannot
    /// retrieve by relation simply contributes nothing rather than failing.
    /// </para>
    /// <para>
    /// J3.1: the budget is shared with predicates the caller borrowed from top-K, so without a
    /// priority set an unrelated high-confidence predicate can exhaust it before the named relation
    /// is complete — defeating the whole guarantee. <c>priorityPredicates</c> is optional and
    /// trailing, so existing implementations and callers are unaffected.
    /// </para>
    /// </remarks>
    /// <param name="canonicalPredicates">Canonical predicate keys to retrieve, whole.</param>
    /// <param name="limit">Maximum facts returned across all of them combined.</param>
    /// <param name="scope">Owner scope for the read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="priorityPredicates">
    /// Predicates the caller asked for by name, ordered ahead of the rest when the budget binds.
    /// </param>
    Task<IReadOnlyList<Fact>> SearchByCanonicalPredicatesAsync(
        IReadOnlyList<string> canonicalPredicates,
        int limit,
        MemoryScope scope,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? priorityPredicates = null) =>
        Task.FromResult<IReadOnlyList<Fact>>(Array.Empty<Fact>());
}
