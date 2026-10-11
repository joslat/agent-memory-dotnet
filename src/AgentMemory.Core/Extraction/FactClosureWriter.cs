using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;

namespace AgentMemory.Core.Extraction;

/// <summary>Applies the existing temporal fact-closure contract and records successful closings.</summary>
internal sealed class FactClosureWriter
{
    private readonly IFactRepository _factRepository;
    private readonly ExtractionOptions _options;

    public FactClosureWriter(IFactRepository factRepository, ExtractionOptions options)
    {
        _factRepository = factRepository;
        _options = options;
    }

    /// <summary>
    /// Closes <paramref name="loserId"/> in favour of <paramref name="winnerId"/> (40.65). With
    /// <see cref="ExtractionOptions.BitemporalChanges"/> the closing says why: a change ends the loser's valid time at
    /// <paramref name="changedAt"/> and keeps it believed, a correction withdraws belief. Without it, exactly the call
    /// every closing made before, so the default is unchanged.
    /// </summary>
    public async Task<bool> CloseAsync(
        string loserId, string winnerId, FactClosureReason reason, DateTimeOffset? changedAt, MemoryScope? scope,
        CancellationToken cancellationToken = default)
    {
        var closed = _options.BitemporalChanges
            ? await _factRepository.SupersedeAsync(loserId, winnerId, reason, changedAt, scope, cancellationToken).ConfigureAwait(false)
            : await _factRepository.SupersedeAsync(loserId, winnerId, scope, cancellationToken).ConfigureAwait(false);
        if (closed) PersistenceClosingJournal.Record(winnerId, loserId);
        return closed;
    }

    /// <summary>When a change took effect: the new value's stated start, else the day it happened, else when it was said.</summary>
    internal static DateTimeOffset ChangedAt(Fact winner, DateTimeOffset now) => winner.ValidFrom ?? winner.OccurredOn ?? now;

    /// <summary>
    /// 40.65. A new value that starts no later than the one it replaces is a correction of that period ("Calderwick as of
    /// February", then "actually Ardenholm as of February"): the old value was never right for it. Otherwise a change.
    /// </summary>
    internal static FactClosureReason ClosingOf(Fact loser, Fact winner) =>
        (winner.ValidFrom ?? winner.OccurredOn) is { } began && (loser.ValidFrom ?? loser.OccurredOn) is { } held && began <= held
            ? FactClosureReason.Correction
            : FactClosureReason.Change;
}
