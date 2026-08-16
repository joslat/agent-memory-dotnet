using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Memory;

namespace AgentMemory.Core.Extraction.Derivation;

/// <summary>
/// Computes and stores what a batch's facts <i>imply</i>, once the batch has committed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Incremental by construction.</b> It looks only at the <c>(subject, predicate)</c> groups the
/// batch just touched. A full sweep would recompute the whole graph on every turn and would still be
/// wrong in the same places, because a group nothing touched cannot have changed.
/// </para>
/// <para>
/// <b>Best-effort, always.</b> Every failure is logged and swallowed: this is a post-persistence
/// enrichment, and taking down an ingestion because an aggregate could not be computed would trade a
/// missing convenience for lost memory. Same posture as
/// <c>PersistenceStage.SupersedeReplacedFactsAsync</c>.
/// </para>
/// </remarks>
internal interface IDerivedMemoryAccountant
{
    /// <summary>Materialises aggregates for the groups this batch touched. Returns how many were written.</summary>
    Task<int> AccountAsync(
        ExtractionStageResult staged,
        string? ownerId,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IDerivedMemoryAccountant"/>
internal sealed class SessionAccountant : IDerivedMemoryAccountant
{
    private readonly IFactRepository _facts;
    private readonly IEmbeddingOrchestrator _embeddings;
    private readonly IIdGenerator _ids;
    private readonly IClock _clock;
    private readonly ExtractionOptions _options;
    private readonly ILogger<SessionAccountant> _logger;

    // Ordered so the derived facts a group produces arrive in a stable sequence run to run, which is
    // what makes a batch's output diffable at all.
    private static readonly IReadOnlyList<IDerivationEvaluator> Evaluators =
    [
        new CountEvaluator(),
        new DeltaEvaluator(),
        new LatestEvaluator(),
        new SumEvaluator(),
        new DurationEvaluator(),
        new SetEnumerationEvaluator(),
    ];

    public SessionAccountant(
        IFactRepository facts,
        IEmbeddingOrchestrator embeddings,
        IIdGenerator ids,
        IClock clock,
        IOptions<ExtractionOptions> options,
        ILogger<SessionAccountant> logger)
    {
        _facts = facts ?? throw new ArgumentNullException(nameof(facts));
        _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options?.Value ?? new ExtractionOptions();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Every evaluator, for the reachability guard to reflect over.</summary>
    internal static IReadOnlyList<IDerivationEvaluator> AllEvaluators => Evaluators;

    public async Task<int> AccountAsync(
        ExtractionStageResult staged,
        string? ownerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(staged);

        var derived = _options.DerivedMemory;
        // Gated INSIDE rather than at the registration, so IOptions reconfiguration works -- the
        // reranker pattern. A conditionally-registered service is one that silently stays absent when a
        // host flips the flag after the container is built.
        if (!derived.Enabled || derived.Operators == DerivationOperators.None) return 0;

        // Owner-scoped, and never include-shared: a group read that mixed a tenant's facts with global
        // ones would compute an aggregate spanning both and store it under one owner. Shared groups are
        // out of scope for phase 1 rather than half-handled.
        var scope = ownerId is null ? null : MemoryScope.For(ownerId, includeShared: false);

        var written = 0;
        foreach (var group in TouchedGroups(staged))
        {
            if (written >= derived.MaxDerivedFactsPerBatch)
            {
                _logger.LogDebug(
                    "Derived-fact batch cap ({Cap}) reached; remaining groups are left for a later batch.",
                    derived.MaxDerivedFactsPerBatch);
                break;
            }

            try
            {
                written += await AccountGroupAsync(group, scope, ownerId, derived, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One bad group must not cost the others their aggregates, and none of them may cost
                // the ingestion its facts.
                _logger.LogWarning(ex,
                    "Failed to derive aggregates for {Subject}/{Predicate}; the batch's facts are stored.",
                    group.Subject, group.Predicate);
            }
        }

        if (written > 0)
            _logger.LogDebug("Session accountant wrote {Count} derived facts.", written);

        return written;
    }

    private async Task<int> AccountGroupAsync(
        TouchedGroup touched,
        MemoryScope? scope,
        string? ownerId,
        DerivedMemoryOptions derived,
        CancellationToken cancellationToken)
    {
        var facts = await _facts.GetGroupFactsAsync(
            touched.SubjectKey, touched.PredicateKey, scope, derived.MaxGroupFanIn, cancellationToken)
            .ConfigureAwait(false);

        // Fewer than two facts cannot be aggregated by any operator, so the group is abandoned before
        // any embedding is paid for.
        if (facts.Count < 2) return 0;

        var group = new DerivationGroup(
            touched.Subject, touched.Predicate, touched.PredicateKey, facts, derived);

        var written = 0;
        foreach (var evaluator in Evaluators)
        {
            if (!derived.Operators.HasFlag(evaluator.Operator)) continue;

            var candidate = evaluator.Evaluate(group);
            if (candidate is null) continue;

            await WriteAsync(candidate, ownerId, derived, cancellationToken).ConfigureAwait(false);
            written++;
        }

        return written;
    }

    private async Task WriteAsync(
        DerivedCandidate candidate,
        string? ownerId,
        DerivedMemoryOptions derived,
        CancellationToken cancellationToken)
    {
        // Embedded on its RENDERED text rather than on the derived predicate spelling: nobody asks
        // "count_of:visited_city", they ask "how many cities have I been to", and the vector has to
        // carry that.
        float[]? embedding = null;
        try
        {
            embedding = await _embeddings.EmbedFactAsync(
                candidate.Subject, candidate.Predicate, candidate.Object, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Stored without a vector rather than not stored: the existing back-fill picks up facts
            // with a null embedding, so the aggregate becomes retrievable later instead of never.
            _logger.LogWarning(ex,
                "Could not embed derived fact {Subject} {Predicate}; storing it unembedded.",
                candidate.Subject, candidate.Predicate);
        }

        var fact = new Fact
        {
            FactId = _ids.GenerateId(),
            Subject = candidate.Subject,
            Predicate = candidate.Predicate,
            Object = candidate.Object,
            Confidence = derived.DerivedFactConfidence,
            CreatedAtUtc = _clock.UtcNow,
            OwnerId = ownerId,
            Embedding = embedding,
            // Untrusted, deliberately. A derived fact is computed from extracted text and renders
            // through the same admission machinery as everything else; being arithmetic does not make
            // its inputs trustworthy.
            Metadata = MemoryDerivationMetadataExtensions
                .CreateWithDerivation(candidate.Operator, candidate.Derivation, candidate.InputFactIds)
                .WithTrustLevel(MemoryTrustLevel.Untrusted),
        };

        await _facts.UpsertDerivedAsync(fact, candidate.InputFactIds, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The distinct <c>(subject, predicate)</c> groups this batch's facts belong to.
    /// </summary>
    /// <remarks>
    /// Canonicalised through the same <see cref="MemoryTripleCanonicalizer"/> the write path uses, so
    /// the group read finds the facts that were actually stored. Deriving the keys any other way would
    /// mean the accountant asks about groups the graph does not have.
    /// </remarks>
    private static IEnumerable<TouchedGroup> TouchedGroups(ExtractionStageResult staged)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fact in staged.FilteredFacts)
        {
            if (string.IsNullOrWhiteSpace(fact.Subject) || string.IsNullOrWhiteSpace(fact.Predicate))
                continue;

            var subjectKey = MemoryTripleCanonicalizer.CanonicalValue(fact.Subject);
            var predicateKey = MemoryTripleCanonicalizer.Canonical(fact.Predicate);
            if (!seen.Add($"{subjectKey}{predicateKey}")) continue;

            yield return new TouchedGroup(fact.Subject, fact.Predicate, subjectKey, predicateKey);
        }
    }

    private readonly record struct TouchedGroup(
        string Subject, string Predicate, string SubjectKey, string PredicateKey);
}
