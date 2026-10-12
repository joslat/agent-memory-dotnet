using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;

namespace AgentMemory.Core.Extraction;

/// <summary>
/// Applies all automatic ingestion closings through their typed repository contracts. Sources still
/// own their existing decisions, ordering and batching; no new judge or deduplication rule is implied.
/// </summary>
internal sealed class ClosingPipeline(
    IFactRepository facts,
    IPreferenceRepository preferences,
    IRelationshipRepository relationships,
    IEntityRepository entities,
    ExtractionOptions options)
{
    public async Task<bool> ApplyAsync(ClosingProposal proposal, CancellationToken cancellationToken = default)
    {
        bool closed;
        string? winner;
        bool record;
        switch (proposal)
        {
            case ClosingProposal.FactWithdrawal withdrawal:
                return await facts.InvalidateAsync(withdrawal.TargetId, withdrawal.Scope, cancellationToken).ConfigureAwait(false);
            case ClosingProposal.EntityRetirement retirement:
                return await entities.InvalidateAsync(retirement.TargetId, retirement.Scope, cancellationToken).ConfigureAwait(false);
            case ClosingProposal.FactClosing fact:
                closed = options.BitemporalChanges
                    ? await facts.SupersedeAsync(fact.TargetId, fact.ReplacementId, fact.Reason, fact.ChangedAtUtc,
                        fact.Scope, cancellationToken).ConfigureAwait(false)
                    : await facts.SupersedeAsync(fact.TargetId, fact.ReplacementId, fact.Scope, cancellationToken).ConfigureAwait(false);
                winner = fact.ReplacementId;
                record = true;
                break;
            case ClosingProposal.PreferenceClosing preference:
                closed = await preferences.SupersedeAsync(preference.TargetId, preference.ReplacementId,
                    preference.Scope, cancellationToken).ConfigureAwait(false);
                winner = preference.ReplacementId;
                record = preference.Source is ClosingSource.SimilarityJudge or ClosingSource.WriterNamed;
                break;
            case ClosingProposal.RelationshipEnding edge:
                closed = await relationships.EndAsync(edge.TargetId, edge.EndedAtUtc, edge.Scope, cancellationToken).ConfigureAwait(false);
                winner = edge.ReplacementId;
                record = edge.Source == ClosingSource.WriterNamed;
                break;
            default:
                throw new ArgumentException("Unsupported closing proposal.", nameof(proposal));
        }

        // Preserve attribution in the compatibility bridge. Deterministic preference/edge paths did
        // not report Closed outcomes; adding them belongs to a separate observable policy change.
        if (closed && record && winner is not null) PersistenceClosingJournal.Record(winner, proposal.TargetId);
        return closed;
    }

    /// <summary>The new value's stated start, else the day it happened, else when it was said.</summary>
    internal static DateTimeOffset ChangedAt(Fact winner, DateTimeOffset now) => winner.ValidFrom ?? winner.OccurredOn ?? now;

    /// <summary>A new value starting no later than its predecessor corrects that period; otherwise it changes it.</summary>
    internal static FactClosureReason ClosingOf(Fact loser, Fact winner) =>
        (winner.ValidFrom ?? winner.OccurredOn) is { } began && (loser.ValidFrom ?? loser.OccurredOn) is { } held && began <= held
            ? FactClosureReason.Correction
            : FactClosureReason.Change;
}
