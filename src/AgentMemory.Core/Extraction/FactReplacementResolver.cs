using static AgentMemory.Core.Extraction.FactClosureWriter;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Core.Memory;
using AgentMemory.Core.Services;
using Microsoft.Extensions.Logging;

namespace AgentMemory.Core.Extraction;

/// <summary>Applies deterministic corrections and current-value replacements after all fact writes.</summary>
internal sealed class FactReplacementResolver
{
    private readonly IFactRepository _factRepository;
    private readonly ExtractionOptions _options;
    private readonly ILogger _logger;
    private readonly FactClosureWriter _factClosures;

    public FactReplacementResolver(IFactRepository factRepository, ExtractionOptions options,
        ILogger logger, FactClosureWriter factClosures)
    {
        _factRepository = factRepository;
        _options = options;
        _logger = logger;
        _factClosures = factClosures;
    }

    public async Task<FactReplacementResult> ApplyAsync(FactWriteSet state, string? ownerId,
        bool writtenByWriter, CancellationToken cancellationToken = default)
    {
        var now = state.Now;
        var factCorrections = state.FactCorrections;
        var factDecision = state.Decision;
        var replacedFacts = state.Replaced;
        var factIndex = state.FactIndex;
        var factsByKey = state.ByKey;
        var writtenFacts = state.WrittenFacts;
        var currentFactIds = state.CurrentIds;
        var extractedByKey = state.ExtractedByKey;
        var createdHere = state.CreatedIds;
        var supersessionEligible = 0;
        var supersessionRefusals = 0;
        var withdrawn = new List<WithdrawnFact>();

        // M1 write-time UPDATE. Runs after the write, so the incoming fact is already the winner and a
        // failure here leaves the graph in the append-only state it was in before -- strictly the old
        // behaviour, never a half-resolved one. Best-effort by design: losing a supersession costs
        // precision in live recall, while failing the ingestion over it would lose the memory itself.
        // With <under>, the winner supersedes what the replaced fact <under> would have: the same guards, one routine.
        async Task SupersedeReplacedFactsAsync(Fact winner, Fact? under = null)
        {
            if (!_options.SupersedeReplacedFacts || writtenByWriter) return;
            var said = under ?? winner;

            // Only declared single-valued relations can replace an older value. Count refusals so the
            // batch can warn once when supersession was enabled but none of its predicates qualified.
            if (!WriteTimeFactResolution.CanSupersede(said))
            {
                if (under is not null) return;
                supersessionRefusals++;
                // Guarded, because the arguments are evaluated whether or not Debug is enabled: this
                // runs once per refused fact, and on an ingestion where NOTHING qualifies that is once
                // per fact in the batch. Joining the relation list each time would be a real cost paid
                // for output nobody is reading. The batch warning below is unguarded on purpose --
                // it fires at most once and only when something is wrong.
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(
                        "Supersession skipped for '{Subject} {Predicate} {Object}': predicate is not "
                        + "declared single-valued, so at most one live value per subject is not "
                        + "assumed. Single-valued relations: {Known}.",
                        winner.Subject, winner.Predicate, winner.Object,
                        string.Join(", ", MemoryRelationCardinality.SingleValuedPredicates));
                }

                return;
            }

            // 36.4. A value that has ended, or not begun, replaces nothing: "I worked at Google until 2019" said after "I
            // work at Meta" is history, not a change of employer; "moving to Oslo next month" is a plan.
            if (!ReplacementShapes.HoldsNow(winner.ValidFrom ?? winner.OccurredOn, winner.ValidUntil, now))
                return;

            if (under is null) supersessionEligible++;

            var scope = string.IsNullOrEmpty(ownerId) ? null : MemoryScope.For(ownerId, includeShared: false);
            // G-15 review: a write without an owner replaces only owner-less (shared) facts. Read with no scope,
            // a shared write superseded every tenant's private facts with the same subject and predicate. The
            // supersede statement itself already refuses to link facts of different owners.
            var candidates = SharedScopes.OwnedOrShared(ownerId);
            try
            {
                // J-6: one clock. The winner was judged to hold at `now`; the candidates are judged at the same instant.
                var losers = await _factRepository.FindSupersededCandidatesAsync(
                    winner.FactId, said.Subject, said.Predicate, winner.Object, now, candidates,
                    cancellationToken).ConfigureAwait(false);
                // I-5. A fact now stored under the user's name also replaces what was stored before the
                // name was known, under the words used then ("user | lives in | Lisbon").
                if (said.Metadata.TryGetValue("subject_surface", out var surface) && surface is string surfaceSubject &&
                    MemoryUserNames.IsSelf(surfaceSubject))
                {
                    losers = [.. losers, .. await _factRepository.FindSupersededCandidatesAsync(
                        winner.FactId, surfaceSubject, said.Predicate, winner.Object, now, candidates,
                        cancellationToken).ConfigureAwait(false)];
                }

                foreach (var loser in losers.DistinctBy(fact => fact.FactId).Where(loser => loser.FactId != winner.FactId))
                {
                    await _factClosures.CloseAsync(
                        loser.FactId, winner.FactId, ClosingOf(loser, winner), ChangedAt(winner, now), scope, cancellationToken)
                        .ConfigureAwait(false);
                    _logger.LogDebug(
                        "Superseded fact '{Loser}' with '{Winner}' ({S} {P}).",
                        loser.FactId, winner.FactId, winner.Subject, winner.Predicate);
                    // 38.6. Its edge too ("Lena —lives in→ Lyon" stayed live beside "moved to → Copenhagen"). The relation
                    // is single-valued, so the winner is the only value still said.
                    withdrawn.Add(new WithdrawnFact(loser, winner, [winner], candidates, scope, Correction: false));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Write-time supersession failed for fact '{Id}'; it remains stored alongside the "
                    + "assertion it replaces.", winner.FactId);
            }
        }

        // 36.4. A correction the extractor marked closes the live fact that stated the replaced value, when
        // nothing else would (a plan, a value under a multi-valued relation). Same gate and same failure policy
        // as supersession above: best-effort, never the reason a stored memory is reported as failed.
        async Task CloseCorrectedAsync(Fact winner, string replaced, string sourceKey)
        {
            if (!_options.SupersedeReplacedFacts || writtenByWriter) return;
            // No start guard, unlike supersession: a marked correction is the person withdrawing what they said,
            // now ("the full marathon in May instead of the half in April" retracts the half today, though the full
            // marathon is dated May; found in simulated conversations after a guard here left both plans live).
            // Read like supersession reads (own, or shared only for a shared write); write with the owner's scope, or
            // none for a shared write: the supersede statement already refuses to link facts of different owners.
            var readScope = SharedScopes.OwnedOrShared(ownerId);
            var writeScope = string.IsNullOrEmpty(ownerId) ? null : MemoryScope.For(ownerId, includeShared: false);
            try
            {
                var candidates = (await _factRepository.GetBySubjectAsync(winner.Subject, readScope, cancellationToken)
                    .ConfigureAwait(false)).ToList();
                // As supersession does: the subject as it was stored before the user's name was known.
                if (winner.Metadata.TryGetValue("subject_surface", out var surface) && surface is string said &&
                    !string.Equals(said, winner.Subject, StringComparison.Ordinal))
                    candidates.AddRange(await _factRepository.GetBySubjectAsync(said, readScope, cancellationToken)
                        .ConfigureAwait(false));
                // No order guard: a marked correction names what it replaces, wherever that was said, except a current
                // value of this extraction. Its fallback (another relation) spares what this extraction created and what
                // it said after the correction: that is what the person holds now.
                var saidAt = factIndex.GetValueOrDefault(sourceKey);
                var spared = new HashSet<string>(createdHere, StringComparer.Ordinal);
                foreach (var (key, fact) in factsByKey)
                {
                    if (factIndex.GetValueOrDefault(key) > saidAt) spared.Add(fact.FactId);
                }
                // B-13 (the branch review). With BitemporalChanges a change keeps the old value believed as history, so a
                // correction of it ("I never lived in Bilbao") must reach it too. Only when the correction is new: a value said
                // again ("I moved to Madrid, not Bilbao", Madrid already stored) is a restatement, and must not erase the past.
                var closed = Corrections.Closed(
                    candidates.Where(candidate => !currentFactIds.Contains(candidate.FactId)), winner, replaced, spared,
                    includeHistory: _options.BitemporalChanges && createdHere.Contains(winner.FactId));
                foreach (var loser in closed)
                {
                    await _factClosures.CloseAsync(loser.FactId, winner.FactId, FactClosureReason.Correction, null, writeScope, cancellationToken)
                        .ConfigureAwait(false);
                    _logger.LogDebug("Correction '{Winner}' closed fact '{Loser}' (replaces '{Replaced}').",
                        winner.FactId, loser.FactId, replaced);
                }
                if (closed.Count > 0)
                {
                    var closedIds = closed.Select(fact => fact.FactId).ToHashSet(StringComparer.Ordinal);
                    var stillLive = candidates
                        .Where(fact => fact.InvalidatedAtUtc is null && !closedIds.Contains(fact.FactId))
                        .Append(winner)
                        .ToList();
                    foreach (var loser in closed)
                        withdrawn.Add(new WithdrawnFact(loser, winner, stillLive, readScope, writeScope, Correction: true));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Closing what fact '{Id}' corrects failed; it remains stored alongside it.", winner.FactId);
            }
        }

        // 36.4. Everything this extraction wrote is in the store: now, and only now, it closes what it replaces. Marked
        // corrections first (they name what they replace, and supersession would otherwise close that and leave the
        // fallback to find something else); then each current value's supersession; then the values it replaced.
        async Task ResolveWrittenFactsAsync()
        {
            var once = writtenFacts.DistinctBy(written => written.Key, StringComparer.Ordinal).ToList();
            foreach (var (key, fact) in once)
            {
                if (factCorrections.TryGetValue(key, out var replaced))
                    await CloseCorrectedAsync(fact, replaced, key).ConfigureAwait(false);
            }
            foreach (var (key, fact) in once)
            {
                if (!replacedFacts.ContainsKey(key))
                    await SupersedeReplacedFactsAsync(fact).ConfigureAwait(false);
            }
            await CloseReplacedFactsAsync(once.Where(written => replacedFacts.ContainsKey(written.Key)).ToList()).ConfigureAwait(false);
        }

        // 36.4. The values this extraction replaced, closed by the one it left current now that everything is written,
        // with what each would have superseded: it may be stored under another self word or another form of the relation
        // than the current value's ("user lives in Lisbon" before "I live in Oslo"). Read first: supersession is
        // idempotent on the edge but lowers confidence again. A current value that failed to write is stood in for by the
        // last replaced value the person did not correct away, latest first (the same order the current value is chosen
        // by); none, and nothing is closed. Best-effort, as every closing is.
        async Task CloseReplacedFactsAsync(List<(string Key, Fact Fact)> replacedWritten)
        {
            var writeScope = string.IsNullOrEmpty(ownerId) ? null : MemoryScope.For(ownerId, includeShared: false);
            foreach (var group in replacedWritten.GroupBy(r => replacedFacts[r.Key], StringComparer.Ordinal))
            {
                var members = group.ToList();
                if (!factsByKey.TryGetValue(group.Key, out var current))
                {
                    var standIn = members
                        .Where(member => !factDecision.Old.Contains(member.Key))
                        .OrderBy(member => extractedByKey.TryGetValue(member.Key, out var said)
                            ? said.ValidFrom ?? said.OccurredOn ?? now : now)
                        .ThenBy(member => factIndex.GetValueOrDefault(member.Key))
                        .Select(member => member.Fact)
                        .LastOrDefault();
                    if (standIn is null)
                    {
                        // Nothing left to stand in (the correction failed to write): a value this extraction created
                        // and itself corrected away is withdrawn, not left live beside the stored one.
                        foreach (var (key, fact) in members.Where(member => factDecision.Old.Contains(member.Key) &&
                                                                           createdHere.Contains(member.Fact.FactId)))
                        {
                            try
                            {
                                await _factRepository.InvalidateAsync(fact.FactId, writeScope, cancellationToken).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Withdrawing '{Key}' failed; it stays stored.", key);
                            }
                        }
                        continue;
                    }
                    current = standIn;
                    currentFactIds.Add(current.FactId);
                    await SupersedeReplacedFactsAsync(current).ConfigureAwait(false);
                }
                foreach (var (memberKey, member) in members.Where(member => member.Fact.FactId != current.FactId))
                {
                    try
                    {
                        if (await _factRepository.GetByIdAsync(member.FactId, cancellationToken).ConfigureAwait(false) is
                            { InvalidatedAtUtc: null })
                        {
                            await _factClosures.CloseAsync(member.FactId, current.FactId,
                                    factDecision.Old.Contains(memberKey) ? FactClosureReason.Correction : FactClosureReason.Change,
                                    ChangedAt(current, now), writeScope, cancellationToken)
                                .ConfigureAwait(false);
                            _logger.LogDebug("'{Winner}' closed '{Loser}', replaced in the same extraction.", current.FactId, member.FactId);
                        }
                        // What the replaced value would have superseded, the current one supersedes, with every guard.
                        await SupersedeReplacedFactsAsync(current, under: member).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Closing replaced value '{Id}' failed; it remains stored alongside the current one.", member.FactId);
                    }
                }
            }
        }
        await ResolveWrittenFactsAsync().ConfigureAwait(false);
        return new FactReplacementResult
        {
            Eligible = supersessionEligible,
            Refusals = supersessionRefusals,
            Withdrawn = withdrawn,
        };
    }
}

internal sealed record FactReplacementResult
{
    public required int Eligible { get; init; }
    public required int Refusals { get; init; }
    public required IReadOnlyList<WithdrawnFact> Withdrawn { get; init; }

    public static FactReplacementResult Empty { get; } = new() { Eligible = 0, Refusals = 0, Withdrawn = [] };
}
