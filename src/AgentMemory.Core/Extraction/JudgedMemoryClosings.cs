using static AgentMemory.Core.Extraction.ClosingPipeline;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using Microsoft.Extensions.Logging;

namespace AgentMemory.Core.Extraction;

/// <summary>Runs the existing named and similarity closing decisions with their distinct fallback rules.</summary>
internal sealed class JudgedMemoryClosings
{
    private readonly IEmbeddingOrchestrator _embeddingOrchestrator;
    private readonly IEntityRepository _entityRepository;
    private readonly IFactRepository _factRepository;
    private readonly IPreferenceRepository _preferenceRepository;
    private readonly IRelationshipRepository _relationshipRepository;
    private readonly IClock _clock;
    private readonly ExtractionOptions _options;
    private readonly ILogger _logger;
    private readonly IMemoryUpdateJudge? _updateJudge;
    private readonly IMemoryUpdateJudgeFallback? _fallbackJudge;
    private readonly ClosingPipeline _closings;

    public JudgedMemoryClosings(IEmbeddingOrchestrator embeddingOrchestrator, IEntityRepository entityRepository,
        IFactRepository factRepository, IPreferenceRepository preferenceRepository,
        IRelationshipRepository relationshipRepository, IClock clock, ExtractionOptions options, ILogger logger,
        IMemoryUpdateJudge? updateJudge, IMemoryUpdateJudgeFallback? fallbackJudge, ClosingPipeline closings)
    {
        _embeddingOrchestrator = embeddingOrchestrator;
        _entityRepository = entityRepository;
        _factRepository = factRepository;
        _preferenceRepository = preferenceRepository;
        _relationshipRepository = relationshipRepository;
        _clock = clock;
        _options = options;
        _logger = logger;
        _updateJudge = updateJudge;
        _fallbackJudge = fallbackJudge;
        _closings = closings;
    }

    public bool IsEnabled => _updateJudge is { IsEnabled: true };

    /// <summary>
    /// 41.06. The update judge: each fact and preference this extraction created is set against the owner's most similar
    /// live memories of its kind (<see cref="ExtractionOptions.UpdateJudgeCandidates"/>), the judge is asked of each pair
    /// whether the new one replaces the stored one, and a stored memory is closed, as a change, at
    /// <see cref="ExtractionOptions.UpdateJudgeThreshold"/>. A failing judge closes nothing.
    /// </summary>
    /// <summary>
    /// The writer's named closings asked of the update judge, and of its second tier (<see cref="IMemoryUpdateJudgeFallback"/>)
    /// when that judge is missing, off or fails (an outside judge down or left out after failures): the step down is logged.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, double>> JudgeNamedAsync(MemoryUpdateRequest request, CancellationToken cancellationToken = default)
    {
        if (_updateJudge is { IsEnabled: true } judge)
        {
            if (_fallbackJudge is not { IsEnabled: true })
                return await judge.JudgeAsync(request, cancellationToken).ConfigureAwait(false);
            try
            {
                return await judge.JudgeAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Memory writer: the update judge failed ({Error}); the host's chat model judges these closings instead.",
                    ex.Message);
            }
        }
        return await _fallbackJudge!.JudgeAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task JudgeUpdatesAsync(
        ExtractionStageResult extraction, string? ownerId, IReadOnlyList<Fact> newFacts, IReadOnlyList<Preference> newPreferences,
        CancellationToken cancellationToken = default)
    {
        if (newFacts.Count == 0 && newPreferences.Count == 0) return;
        // The owner's own memories only: a shared fact is not the owner's to close. Without an owner, only ownerless memories:
        // a vector search with no scope reads every owner's, and a write without an owner must never close another owner's
        // memory (a single-tenant store, where nothing has an owner, keeps the judge).
        var scope = string.IsNullOrEmpty(ownerId) ? null : MemoryScope.For(ownerId, includeShared: false);
        bool Closable(string? owner) => string.IsNullOrEmpty(ownerId) ? string.IsNullOrEmpty(owner) : owner == ownerId;
        var created = new HashSet<string>(
            newFacts.Select(f => f.FactId).Concat(newPreferences.Select(p => p.PreferenceId)), StringComparer.Ordinal);
        var limit = Math.Max(1, _options.UpdateJudgeCandidates);
        var pairs = new List<MemoryUpdatePair>();
        var targets = new Dictionary<string, (Fact? NewFact, string Winner, string Loser)>(StringComparer.Ordinal);
        try
        {
            foreach (var fact in newFacts)
            {
                var vector = fact.Embedding
                    ?? await _embeddingOrchestrator.EmbedFactAsync(fact.Subject, fact.Predicate, fact.Object, cancellationToken).ConfigureAwait(false);
                var similar = await _factRepository.SearchByVectorAsync(
                    vector, ValidTimeMode.Current, limit + created.Count, 0.0, scope, cancellationToken).ConfigureAwait(false);
                foreach (var stored in similar.Select(s => s.Fact)
                             .Where(f => !created.Contains(f.FactId) && f.InvalidatedAtUtc is null && Closable(f.OwnerId)).Take(limit))
                {
                    var key = $"p{pairs.Count + 1}";
                    pairs.Add(new MemoryUpdatePair(key, FactText(fact), FactText(stored)));
                    targets[key] = (fact, fact.FactId, stored.FactId);
                }
            }
            foreach (var preference in newPreferences)
            {
                var vector = preference.Embedding
                    ?? await _embeddingOrchestrator.EmbedAsync(preference.PreferenceText, cancellationToken).ConfigureAwait(false);
                var similar = await _preferenceRepository.SearchByVectorAsync(
                    vector, limit + created.Count, 0.0, scope, cancellationToken).ConfigureAwait(false);
                foreach (var stored in similar.Select(s => s.Preference)
                             .Where(p => !created.Contains(p.PreferenceId) && p.InvalidatedAtUtc is null && Closable(p.OwnerId)).Take(limit))
                {
                    var key = $"p{pairs.Count + 1}";
                    pairs.Add(new MemoryUpdatePair(key, preference.PreferenceText, stored.PreferenceText));
                    targets[key] = (null, preference.PreferenceId, stored.PreferenceId);
                }
            }
            if (pairs.Count == 0)
            {
                // Said, not silent: "nothing to compare" and "the judge said no" must be told apart when closings are missing.
                _logger.LogInformation(
                    "Update judge: nothing to compare for {Facts} new fact(s) and {Preferences} new preference(s): no live memory of the owner was found similar to them.",
                    newFacts.Count, newPreferences.Count);
                return;
            }
            var verdicts = await _updateJudge!.JudgeAsync(
                new MemoryUpdateRequest(extraction.SourceText.Length > 0 ? extraction.SourceText : null, _clock.UtcNow, pairs),
                cancellationToken).ConfigureAwait(false);
            var closedOnce = new HashSet<string>(StringComparer.Ordinal);
            var closings = 0;
            foreach (var (key, (newFact, winner, loser)) in targets)
            {
                if (!verdicts.TryGetValue(key, out var probability))
                {
                    _logger.LogWarning("Update judge: no answer for pair '{Key}' ('{New}' against '{Stored}'); it is left open.",
                        key, pairs.First(p => p.Key == key).NewMemory, pairs.First(p => p.Key == key).StoredMemory);
                    continue;
                }
                if (!double.IsFinite(probability) || probability is < 0 or > 1
                    || probability < _options.UpdateJudgeThreshold || !closedOnce.Add(loser))
                    continue;
                ClosingProposal proposal = newFact is not null
                    ? ClosingProposal.Fact(loser, winner, FactClosureReason.Change, ChangedAt(newFact, _clock.UtcNow), scope, ClosingSource.SimilarityJudge)
                    : ClosingProposal.Preference(loser, winner, scope, ClosingSource.SimilarityJudge);
                var closed = await _closings.ApplyAsync(proposal, cancellationToken).ConfigureAwait(false);
                if (closed)
                {
                    closings++;
                    _logger.LogInformation("Update judge: closed '{Stored}', replaced by '{New}' (P {Probability:0.00}).",
                        pairs.First(p => p.Key == key).StoredMemory, pairs.First(p => p.Key == key).NewMemory, probability);
                }
                else
                {
                    _logger.LogWarning("Update judge: '{Stored}' should close (P {Probability:0.00}) but the store closed nothing (id '{Loser}').",
                        pairs.First(p => p.Key == key).StoredMemory, probability, loser);
                }
            }
            _logger.LogInformation(
                "Update judge: {Pairs} pair(s) asked for {Facts} new fact(s) and {Preferences} new preference(s); {Closed} closed at P >= {Threshold}; highest P {Highest:0.00}.",
                pairs.Count, newFacts.Count, newPreferences.Count, closings, _options.UpdateJudgeThreshold,
                verdicts.Count == 0 ? 0 : verdicts.Values.Max());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update judge FAILED ({Error}); nothing it would have closed was closed.", ex.Message);
        }

        static string FactText(Fact fact) => $"{fact.Subject} | {fact.Predicate} | {fact.Object}";
    }

    private static int s_warnedNamedWithoutJudge;

    /// <summary>
    /// AMWRITE001. The stored memories a store-aware writer said the new ones replace (<see cref="ExtractedFact.ReplacesId"/>,
    /// <see cref="ExtractedPreference.ReplacesId"/>, <see cref="ExtractedRelationship.ReplacesId"/>): each pair goes to the
    /// update judge, and the stored memory is closed, as a change or as a correction (a stored relationship is ended, and
    /// stays as history), at <see cref="ExtractionOptions.NamedClosingThreshold"/>, the judge asked the named question
    /// (<see cref="MemoryUpdateRequest.Named"/>). A pair the judge does not confirm leaves the stored memory open beside the new
    /// one (measured on a fresh world: 20 of 30 replaced memories closed, 1.0 wrong closures a run). Without a judge, or when it
    /// fails, nothing is closed: a stale memory is a smaller harm than a true one erased.
    /// </summary>
    public async Task CloseNamedAsync(
        ExtractionStageResult extraction, string? ownerId, IReadOnlyList<ExtractedFact> facts, IReadOnlyList<ExtractedPreference> preferences,
        IReadOnlyDictionary<string, Fact> factsByKey, IReadOnlyDictionary<string, Preference> preferencesByKey,
        IReadOnlyDictionary<string, string> relationshipsByKey, CancellationToken cancellationToken = default)
    {
        // The items as written: after the within-extraction merge, which carries a merged item's named memory to the one kept.
        // One question per stored memory: the first item that names it asks.
        var asked = new HashSet<string>(StringComparer.Ordinal);
        var namedFacts = facts.Where(f => !string.IsNullOrWhiteSpace(f.ReplacesId) && asked.Add(f.ReplacesId!)).ToList();
        var namedPreferences = preferences.Where(p => !string.IsNullOrWhiteSpace(p.ReplacesId) && asked.Add(p.ReplacesId!)).ToList();
        var namedRelationships = extraction.FilteredRelationships
            .Where(r => !string.IsNullOrWhiteSpace(r.ReplacesId) && asked.Add(r.ReplacesId!)).ToList();
        if (namedFacts.Count == 0 && namedPreferences.Count == 0 && namedRelationships.Count == 0) return;
        if (_updateJudge is not { IsEnabled: true } && _fallbackJudge is not { IsEnabled: true })
        {
            if (Interlocked.Exchange(ref s_warnedNamedWithoutJudge, 1) == 0)
                _logger.LogWarning(
                    "The memory writer named {Count} stored memory(ies) as replaced, but no update judge is registered and enabled, "
                    + "so nothing is closed: the new memories are stored beside the old. Register the update judge (AMGATE001) "
                    + "for the writer's closings to apply.", namedFacts.Count + namedPreferences.Count + namedRelationships.Count);
            return;
        }
        // The owner's own memories only, as the judge's own path (JudgeUpdatesAsync) reads them.
        var scope = string.IsNullOrEmpty(ownerId) ? null : MemoryScope.For(ownerId, includeShared: false);
        bool Closable(string? owner) => string.IsNullOrEmpty(ownerId) ? string.IsNullOrEmpty(owner) : owner == ownerId;
        var pairs = new List<MemoryUpdatePair>();
        var targets = new Dictionary<string, (Fact? NewFact, string Winner, string Loser, bool Correction, bool Edge)>(StringComparer.Ordinal);
        try
        {
            foreach (var item in namedFacts)
            {
                if (!factsByKey.TryGetValue($"{item.Subject} {item.Predicate} {item.Object}", out var written))
                {
                    _logger.LogWarning("Memory writer: '{Subject} | {Predicate} | {Object}' named '{Id}' as replaced but was not written; nothing is closed.",
                        item.Subject, item.Predicate, item.Object, item.ReplacesId);
                    continue;
                }
                var stored = await _factRepository.GetByIdAsync(item.ReplacesId!, cancellationToken).ConfigureAwait(false);
                if (stored is null && await LiveEdgeAsync(item.ReplacesId!).ConfigureAwait(false) is { } edge)
                {
                    // A fact that ends a stored connection: "Owen and Simone | got married on | 18 August" ends "engaged to".
                    var edgeKey = $"p{pairs.Count + 1}";
                    pairs.Add(new MemoryUpdatePair(edgeKey, NamedFactText(written), edge.Text));
                    targets[edgeKey] = (written, written.FactId, edge.Id, item.ReplacementIsCorrection, true);
                    continue;
                }
                if (stored is null || stored.InvalidatedAtUtc is not null || !Closable(stored.OwnerId) || stored.FactId == written.FactId)
                {
                    _logger.LogInformation("Memory writer: '{Id}' is not a live fact or relationship of this owner; '{New}' is stored without closing it.",
                        item.ReplacesId, NamedFactText(written));
                    continue;
                }
                var key = $"p{pairs.Count + 1}";
                pairs.Add(new MemoryUpdatePair(key, NamedFactText(written), NamedFactText(stored)));
                targets[key] = (written, written.FactId, stored.FactId, item.ReplacementIsCorrection, false);
            }
            foreach (var item in namedPreferences)
            {
                if (!preferencesByKey.TryGetValue(item.PreferenceText, out var written))
                {
                    _logger.LogWarning("Memory writer: preference '{Text}' named '{Id}' as replaced but was not written; nothing is closed.",
                        item.PreferenceText, item.ReplacesId);
                    continue;
                }
                var stored = await _preferenceRepository.GetByIdAsync(item.ReplacesId!, cancellationToken).ConfigureAwait(false);
                if (stored is null || stored.InvalidatedAtUtc is not null || !Closable(stored.OwnerId) || stored.PreferenceId == written.PreferenceId)
                {
                    _logger.LogInformation("Memory writer: '{Id}' is not a live preference of this owner; '{New}' is stored without closing it.",
                        item.ReplacesId, written.PreferenceText);
                    continue;
                }
                var key = $"p{pairs.Count + 1}";
                pairs.Add(new MemoryUpdatePair(key, written.PreferenceText, stored.PreferenceText));
                targets[key] = (null, written.PreferenceId, stored.PreferenceId, false, false);
            }
            foreach (var item in namedRelationships)
            {
                var text = EdgeText(item.SourceEntity, item.RelationshipType, item.TargetEntity);
                // The new connection may not be stored: its other end can be a name the store does not hold yet ("Walter
                // -[LIVES_IN]-> Saskatoon", the scenario suite's H7). The judge still decides whether the named one holds;
                // the turn's facts carry the new value.
                var writtenId = relationshipsByKey.GetValueOrDefault($"{item.SourceEntity}-{item.RelationshipType}->{item.TargetEntity}") ?? "";
                if (writtenId.Length == 0)
                    _logger.LogInformation("Memory writer: relationship '{New}' was not stored; the judge is still asked whether '{Id}' ends.",
                        text, item.ReplacesId);
                if (await LiveEdgeAsync(item.ReplacesId!).ConfigureAwait(false) is not { } edge || edge.Id == writtenId
                    || string.Equals(edge.Text, text, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Memory writer: '{Id}' is not another live relationship of this owner; '{New}' is stored without ending it.",
                        item.ReplacesId, text);
                    continue;
                }
                var key = $"p{pairs.Count + 1}";
                pairs.Add(new MemoryUpdatePair(key, text, edge.Text));
                targets[key] = (null, writtenId, edge.Id, false, true);
            }
            if (pairs.Count == 0) return;
            var verdicts = await JudgeNamedAsync(
                new MemoryUpdateRequest(extraction.SourceText.Length > 0 ? extraction.SourceText : null, _clock.UtcNow, pairs) { Named = true },
                cancellationToken).ConfigureAwait(false);
            var closings = 0;
            var closedOnce = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (key, (newFact, winner, loser, correction, edge)) in targets)
            {
                var pair = pairs.First(p => p.Key == key);
                if (!verdicts.TryGetValue(key, out var probability))
                {
                    _logger.LogWarning("Memory writer: the judge gave no answer for '{New}' against '{Stored}'; it is left open.",
                        pair.NewMemory, pair.StoredMemory);
                    continue;
                }
                if (!double.IsFinite(probability) || probability is < 0 or > 1 || probability < _options.NamedClosingThreshold)
                {
                    _logger.LogInformation("Memory writer: the judge did not confirm that '{New}' replaces '{Stored}' (P {Probability:0.00}); both stay.",
                        pair.NewMemory, pair.StoredMemory, probability);
                    continue;
                }
                if (!closedOnce.Add(loser)) continue;
                ClosingProposal proposal = edge
                    ? ClosingProposal.Relationship(loser, EdgeEnd(newFact, correction), scope, ClosingSource.WriterNamed, winner)
                    : newFact is not null
                    ? ClosingProposal.Fact(loser, winner, correction ? FactClosureReason.Correction : FactClosureReason.Change,
                        correction ? null : ChangedAt(newFact, _clock.UtcNow), scope, ClosingSource.WriterNamed)
                    : ClosingProposal.Preference(loser, winner, scope, ClosingSource.WriterNamed);
                var closed = await _closings.ApplyAsync(proposal, cancellationToken).ConfigureAwait(false);
                if (closed)
                {
                    closings++;
                    _logger.LogInformation("Memory writer: closed '{Stored}', {How} by '{New}' (P {Probability:0.00}).",
                        pair.StoredMemory, edge ? "ended" : correction ? "corrected" : "replaced", pair.NewMemory, probability);
                }
                else
                {
                    _logger.LogWarning("Memory writer: '{Stored}' should close (P {Probability:0.00}) but the store closed nothing (id '{Loser}').",
                        pair.StoredMemory, probability, loser);
                }
            }
            _logger.LogInformation("Memory writer: {Pairs} named closing(s) asked; {Closed} closed at P >= {Threshold}.",
                pairs.Count, closings, _options.NamedClosingThreshold);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Memory writer: the judge FAILED ({Error}); nothing the writer named was closed.", ex.Message);
        }

        static string NamedFactText(Fact fact) => $"{fact.Subject} | {fact.Predicate} | {fact.Object}";

        // A connection as the judge reads a fact: "Owen Hnatiuk | engaged to | Simone Gagné".
        static string EdgeText(string source, string type, string target) =>
            $"{source} | {type.Replace('_', ' ').ToLowerInvariant()} | {target}";

        // A stored relationship the writer named: live, this owner's, with its two ends' names; else null.
        async Task<(string Id, string Text)?> LiveEdgeAsync(string id)
        {
            var stored = await _relationshipRepository.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
            if (stored is null || stored.ValidUntil is { } until && until <= _clock.UtcNow || !Closable(stored.OwnerId)) return null;
            var source = await _entityRepository.GetByIdAsync(stored.SourceEntityId, cancellationToken).ConfigureAwait(false);
            var target = await _entityRepository.GetByIdAsync(stored.TargetEntityId, cancellationToken).ConfigureAwait(false);
            return source is null || target is null ? null : (stored.RelationshipId, EdgeText(source.Name, stored.RelationshipType, target.Name));
        }

        // A change's edge ends when the change took effect (with BitemporalChanges, as its fact does), never later than now;
        // a correction's, and a connection's, at once.
        DateTimeOffset EdgeEnd(Fact? winner, bool correction)
        {
            var now = _clock.UtcNow;
            return winner is not null && !correction && _options.BitemporalChanges && ChangedAt(winner, now) is var at && at <= now ? at : now;
        }
    }
}
