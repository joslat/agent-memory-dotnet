using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using Microsoft.Extensions.Logging;

namespace AgentMemory.Core.Memory;

/// <summary>
/// Writes entity summaries, and refuses to hand back one that can no longer prove it is current (S1).
/// </summary>
/// <remarks>
/// <para>
/// The synthesis is the easy half. The half that decides whether this feature is an asset or a
/// liability is what happens <i>after</i> a source fact is superseded — and the answer here is that
/// the summary stops being used, immediately, without anything having to remember to delete it.
/// </para>
/// <para>
/// <b>Detection rather than invalidation-on-write.</b> A supersession could instead go and mark every
/// affected summary, and that is the design that fails quietly: it has to find them all, it has to run
/// in the same transaction, and any path that writes a fact without going through it leaves a summary
/// that looks current and is not. Recomputing the fingerprint on read cannot be bypassed by a writer
/// that did not know summaries existed.
/// </para>
/// <para>
/// The cost is one extra read of the entity's facts before a summary is used. That is the same read
/// the summary is saving the caller, so a stale summary costs what not having one would have cost —
/// and a fresh summary still saves the context, which is what it was for.
/// </para>
/// </remarks>
internal sealed class EntitySummaryService : IEntitySummaryService
{
    private readonly IFactRepository _facts;
    private readonly IEntitySummaryRepository _summaries;
    private readonly IEntitySummarySynthesizer _synthesizer;
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;
    private readonly ILogger<EntitySummaryService> _logger;

    /// <summary>Public ctor: the type is internal, and DI can only activate it through one.</summary>
    public EntitySummaryService(
        IFactRepository facts,
        IEntitySummaryRepository summaries,
        IEntitySummarySynthesizer synthesizer,
        IClock clock,
        IIdGenerator ids,
        ILogger<EntitySummaryService> logger)
    {
        _facts = facts;
        _summaries = summaries;
        _synthesizer = synthesizer;
        _clock = clock;
        _ids = ids;
        _logger = logger;
    }

    /// <summary>
    /// Synthesizes and stores a summary for <paramref name="entity"/>, replacing any existing one.
    /// </summary>
    /// <returns>The stored summary, or <see langword="null"/> when there was nothing to summarise.</returns>
    public async Task<EntitySummary?> RefreshAsync(
        Entity entity,
        MemoryScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var facts = await _facts.GetBySubjectAsync(entity.Name, scope, cancellationToken).ConfigureAwait(false);
        var live = facts.Where(f => f.InvalidatedAtUtc is null).ToList();

        var content = await _synthesizer
            .SynthesizeAsync(entity, live, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(content))
        {
            _logger.LogDebug("Nothing to summarise for entity {EntityId}.", entity.EntityId);
            return null;
        }

        // Fingerprinted from the SAME list handed to the synthesizer. Reading the facts twice, or
        // filtering differently here, would produce a fingerprint describing a set the text was never
        // written from -- a summary that could be stale from the moment it was stored.
        var summary = new EntitySummary
        {
            SummaryId = _ids.GenerateId(),
            EntityId = entity.EntityId,
            Content = content,
            SourceFactIds = live.Select(f => f.FactId).ToList(),
            SourceFingerprint = EntitySummary.ComputeFingerprint(Sources(live)),
            OwnerId = entity.OwnerId,
            GeneratedAtUtc = _clock.UtcNow,
        };

        await _summaries.UpsertAsync(summary, cancellationToken).ConfigureAwait(false);
        return summary;
    }

    /// <summary>
    /// Returns the entity's summary only if it still describes the current facts.
    /// </summary>
    /// <returns>
    /// The summary when its fingerprint still matches the store; <see langword="null"/> when there is
    /// none <b>or</b> when the one on record has gone stale.
    /// </returns>
    /// <remarks>
    /// Null for stale is deliberate and is the whole safety property. Returning it with an
    /// <c>IsStale</c> flag would put the decision in every caller's hands, and the failure of any one
    /// of them to check it is indistinguishable from correct memory.
    /// </remarks>
    public async Task<EntitySummary?> GetIfCurrentAsync(
        Entity entity,
        MemoryScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var stored = await _summaries
            .GetByEntityAsync(entity.EntityId, scope, cancellationToken).ConfigureAwait(false);
        if (stored is null) return null;

        var facts = await _facts.GetBySubjectAsync(entity.Name, scope, cancellationToken).ConfigureAwait(false);
        var live = facts.Where(f => f.InvalidatedAtUtc is null).ToList();
        var current = EntitySummary.ComputeFingerprint(Sources(live));

        if (string.Equals(current, stored.SourceFingerprint, StringComparison.Ordinal)) return stored;

        _logger.LogDebug(
            "Entity summary for {EntityId} is stale ({Stored} != {Current}); not used.",
            entity.EntityId, stored.SourceFingerprint, current);
        return null;
    }

    private static IEnumerable<EntitySummarySource> Sources(IEnumerable<Fact> facts) =>
        facts.Select(f => new EntitySummarySource(f.FactId, f.Confidence, Invalidated: false));
}
