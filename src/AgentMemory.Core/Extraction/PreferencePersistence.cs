using static AgentMemory.Core.Extraction.PersistenceOutcomes;
using static AgentMemory.Core.Extraction.PersistenceMetadata;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Memory;
using AgentMemory.Core.Services;
using Microsoft.Extensions.Logging;

namespace AgentMemory.Core.Extraction;

/// <summary>Persists preferences, provenance and deterministic corrections within the current transaction.</summary>
internal sealed class PreferencePersistence
{
    private readonly IPreferenceRepository _preferenceRepository;
    private readonly IClock _clock;
    private readonly IIdGenerator _idGenerator;
    private readonly ExtractionOptions _options;
    private readonly ILogger _logger;

    public PreferencePersistence(IPreferenceRepository preferenceRepository, IClock clock,
        IIdGenerator idGenerator, ExtractionOptions options, ILogger logger)
    {
        _preferenceRepository = preferenceRepository;
        _clock = clock;
        _idGenerator = idGenerator;
        _options = options;
        _logger = logger;
    }

    public async Task<PreferencePersistenceResult> PersistAsync(
        IReadOnlyList<PreparedPreference> preferences,
        string? ownerId,
        MemoryTrustLevel trustLevel,
        IReadOnlyList<string> sourceMessageIds,
        List<IngestionItemOutcome> outcomes,
        bool failFast,
        bool writtenByWriter,
        CancellationToken cancellationToken = default)
    {
        // 3. Embed + upsert preferences.
        // 36.4. Preferences have no single-valued relation, so a marked correction is the only way one replaces
        // another ("Arcade Fire, not Radiohead" left both live).
        // A correction naming its own value ("Favourite band is Radiohead", replaces "Radiohead") marks nothing.
        var preferenceCorrections = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var preparedPreference in preferences)
        {
            var item = preparedPreference.Item;
            if (string.IsNullOrWhiteSpace(item.Replaces) || Corrections.Value(item.Replaces) is not { Length: > 0 } named ||
                Corrections.StatedValue(item.PreferenceText) == named)
                continue;
            preferenceCorrections.TryAdd(item.PreferenceText, item.Replaces!);
        }
        // As for facts (CurrentValues): a relation is the category and the single-valued relation the text states
        // ("Favourite band is ..."); a correction names what of its category contains the value it replaces.
        var preferenceDecision = !(_options.SupersedeReplacedFacts && !writtenByWriter)
            ? new CurrentValues.Decision(new Dictionary<string, string>(), new HashSet<string>())
            : CurrentValues.Decide(
                preferences.Select(p => p.Item).ToList(),
                preference => preference.PreferenceText,
                preference => Corrections.SingleValuedRelation(preference.PreferenceText) is { } relation
                    ? MemoryTripleCanonicalizer.CanonicalValue(preference.Category) + "\u0001" + relation
                    : null,
                (preference, correction) =>
                    preferenceCorrections.TryGetValue(correction.PreferenceText, out var named) &&
                    string.Equals(preference.Category, correction.Category, StringComparison.OrdinalIgnoreCase) &&
                    Corrections.Names(preference.PreferenceText, named));
        var replacedPreferences = preferenceDecision.Replaced;
        var preferenceIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (preparedPreference, index) in preferences.Select((p, i) => (p, i)))
            preferenceIndex.TryAdd(preparedPreference.Item.PreferenceText, index);
        var writtenPreferences = new List<(string Key, Preference Preference)>();
        var preferencesByKey = new Dictionary<string, Preference>(StringComparer.Ordinal);
        // 41.06. The preferences this extraction created, for the update judge (a restated one replaces nothing).
        var preferencesCreatedHere = new HashSet<string>(StringComparer.Ordinal);
        var currentPreferenceIds = new HashSet<string>(StringComparer.Ordinal);

        var preferenceInputs = preferences.Select(preparedPreference =>
        {
            var extracted = preparedPreference.Item;
            return (Item: new Preference
            {
                PreferenceId = _idGenerator.GenerateId(),
                Category = extracted.Category,
                PreferenceText = extracted.PreferenceText,
                Context = extracted.Context,
                Confidence = extracted.Confidence,
                Embedding = preparedPreference.Embedding,
                OwnerId = ownerId,
                SourceMessageIds = SourceTurnProvenance.Resolve(extracted.SourceTurn, sourceMessageIds),
                CreatedAtUtc = _clock.UtcNow,
                Metadata = MemoryTrustMetadataExtensions.CreateWithTrustLevel(
                    SourceRoleTrust.Refine(trustLevel, extracted.SourceRole))
            }, SourceKey: extracted.PreferenceText);
        }).ToList();

        var persistedPrefCount = 0;

        async Task RecordPersistedPreferenceAsync(
            string sourceKey, Preference persisted, IReadOnlyList<string> provenanceMessageIds, bool created)
        {
            if (created) preferencesCreatedHere.Add(persisted.PreferenceId);
            RecordSuccess(outcomes, MemoryItemKind.Preference, sourceKey, persisted.PreferenceId,
                created ? MemoryWriteEffect.Created : MemoryWriteEffect.AlreadyStored);
            PreferenceWritten(sourceKey, persisted);

            // The input item's resolved ids, for the same reason the fact path uses them.
            foreach (var msgId in ExplicitProvenanceMessageIds(_preferenceRepository, provenanceMessageIds))
            {
                try
                {
                    await _preferenceRepository.CreateExtractedFromRelationshipAsync(
                        persisted.PreferenceId, msgId, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to create EXTRACTED_FROM for preference '{Id}' → message '{MsgId}'.",
                        persisted.PreferenceId, msgId);
                    RecordFailureAndMaybeThrow(outcomes, failFast, MemoryItemKind.Preference, IngestionStage.Provenance,
                        MemoryErrorCodes.ProvenancePersistenceFailed, sourceKey, persisted.PreferenceId, ex,
                        "Ingestion failed fast: provenance failed for a preference.");
                }
            }

            writtenPreferences.Add((sourceKey, persisted));

            persistedPrefCount++;
            _logger.LogDebug("Persisted preference in category '{Category}'.", persisted.Category);
        }

        // A marked correction closes what it names; a current preference stating a single-valued relation ("Favourite
        // band is Arcade Fire") closes the other values of it, marked or not.
        async Task CloseCorrectedPreferencesAsync(Preference winner, string? replaced, bool sameRelation)
        {
            if (!(_options.SupersedeReplacedFacts && !writtenByWriter)) return;
            var readScope = SharedScopes.OwnedOrShared(ownerId);
            var writeScope = string.IsNullOrEmpty(ownerId) ? null : MemoryScope.For(ownerId, includeShared: false);
            try
            {
                var candidates = (await _preferenceRepository.GetByCategoryAsync(winner.Category, readScope, cancellationToken)
                    .ConfigureAwait(false)).Where(candidate => !currentPreferenceIds.Contains(candidate.PreferenceId));
                foreach (var loser in candidates.Where(candidate =>
                             (replaced is not null && Corrections.Closes(candidate, winner, replaced)) ||
                             (sameRelation && Corrections.Replaces(winner, candidate))))
                {
                    await _preferenceRepository.SupersedeAsync(loser.PreferenceId, winner.PreferenceId, writeScope, cancellationToken)
                        .ConfigureAwait(false);
                    _logger.LogDebug("Correction '{Winner}' closed preference '{Loser}' (replaces '{Replaced}').",
                        winner.PreferenceId, loser.PreferenceId, replaced);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Closing what preference '{Id}' corrects failed; it remains stored alongside it.", winner.PreferenceId);
            }
        }

        async Task PersistPreferenceIndividuallyAsync(Preference item, string sourceKey)
        {
            try
            {
                var persisted = await _preferenceRepository.UpsertAsync(item, cancellationToken).ConfigureAwait(false);
                await RecordPersistedPreferenceAsync(
                    sourceKey, persisted, item.SourceMessageIds, created: persisted.PreferenceId == item.PreferenceId).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (MemoryIngestionException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error persisting preference '{Text}'.", sourceKey);
                RecordFailureAndMaybeThrow(outcomes, failFast, MemoryItemKind.Preference, IngestionStage.Persistence,
                    MemoryErrorCodes.PreferencePersistenceFailed, sourceKey, null, ex,
                    "Ingestion failed fast: persistence failed for a preference.");
            }
        }

        Dictionary<string, Preference>? batchedPreferencesById = null;
        var fusedPreferenceRepository = _options.UseCoalescedPersistenceTransactions
            ? _preferenceRepository as IFusedBatchMemoryRepository<Preference> : null;
        var batchPreferenceRepository = _preferenceRepository as IBatchMemoryRepository<Preference>;
        var canBatchPreferences = _options.EnableBatchMemoryUpserts && !failFast &&
            preferenceInputs.Count > 0 &&
            preferenceInputs.Select(input => input.Item.PreferenceId).Distinct(StringComparer.Ordinal).Count() == preferenceInputs.Count &&
            (fusedPreferenceRepository is not null ||
             (preferenceInputs.Count > 1 && batchPreferenceRepository is not null));
        if (canBatchPreferences)
        {
            try
            {
                var items = preferenceInputs.Select(input => input.Item).ToList();
                var persisted = fusedPreferenceRepository is not null
                    ? await fusedPreferenceRepository.UpsertFusedBatchAsync(items, cancellationToken).ConfigureAwait(false)
                    : await batchPreferenceRepository!.UpsertBatchAsync(items, cancellationToken).ConfigureAwait(false);
                batchedPreferencesById = persisted.ToDictionary(
                    preference => preference.PreferenceId, StringComparer.Ordinal);
                if (preferenceInputs.Any(input => !batchedPreferencesById.ContainsKey(input.Item.PreferenceId)))
                    throw new InvalidOperationException("The preference batch result omitted one or more input identifiers.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Atomic preference batch failed; replaying {Count} preferences through the item path.",
                    preferenceInputs.Count);
                batchedPreferencesById = null;
            }
        }

        void PreferenceWritten(string sourceKey, Preference persisted)
        {
            preferencesByKey.TryAdd(sourceKey, persisted);
            if (!replacedPreferences.ContainsKey(sourceKey) && Corrections.SingleValuedRelation(persisted.PreferenceText) is not null)
                currentPreferenceIds.Add(persisted.PreferenceId);
        }

        if (batchedPreferencesById is not null)
        {
            foreach (var input in preferenceInputs)
                PreferenceWritten(input.SourceKey, batchedPreferencesById[input.Item.PreferenceId]);
            foreach (var input in preferenceInputs)
                await RecordPersistedPreferenceAsync(
                    input.SourceKey, batchedPreferencesById[input.Item.PreferenceId],
                    input.Item.SourceMessageIds,
                    created: batchedPreferencesById[input.Item.PreferenceId].PreferenceId == input.Item.PreferenceId).ConfigureAwait(false);
        }
        else
        {
            foreach (var input in preferenceInputs)
                await PersistPreferenceIndividuallyAsync(input.Item, input.SourceKey).ConfigureAwait(false);
        }
        if (_options.SupersedeReplacedFacts && !writtenByWriter)
        {
            // As for facts, once every preference is written: what each one closes (only a current one replaces the
            // other values of its relation; a replaced one still closes what it names, older than both), then each
            // replaced one closed by its current one, a failed current stood in for by the latest one not corrected away.
            var once = writtenPreferences.DistinctBy(written => written.Key, StringComparer.Ordinal).ToList();
            foreach (var (key, persisted) in once)
            {
                var isCurrent = !replacedPreferences.ContainsKey(key);
                preferenceCorrections.TryGetValue(key, out var replaced);
                if (replaced is not null || (isCurrent && Corrections.SingleValuedRelation(persisted.PreferenceText) is not null))
                    await CloseCorrectedPreferencesAsync(persisted, replaced, sameRelation: isCurrent).ConfigureAwait(false);
            }
            var writeScope = string.IsNullOrEmpty(ownerId) ? null : MemoryScope.For(ownerId, includeShared: false);
            foreach (var group in once.Where(w => replacedPreferences.ContainsKey(w.Key)).GroupBy(w => replacedPreferences[w.Key], StringComparer.Ordinal))
            {
                if (!preferencesByKey.TryGetValue(group.Key, out var current))
                {
                    var standIn = group.Where(member => !preferenceDecision.Old.Contains(member.Key))
                        .OrderBy(member => preferenceIndex.GetValueOrDefault(member.Key))
                        .Select(member => member.Preference)
                        .LastOrDefault();
                    if (standIn is null) continue;
                    current = standIn;
                    currentPreferenceIds.Add(current.PreferenceId);
                    await CloseCorrectedPreferencesAsync(current, replaced: null, sameRelation: true).ConfigureAwait(false);
                }
                foreach (var member in group.Select(member => member.Preference).Where(member => member.PreferenceId != current.PreferenceId))
                {
                    try
                    {
                        if (await _preferenceRepository.GetByIdAsync(member.PreferenceId, cancellationToken).ConfigureAwait(false) is not
                            { InvalidatedAtUtc: null })
                            continue;
                        await _preferenceRepository.SupersedeAsync(member.PreferenceId, current.PreferenceId, writeScope, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Closing replaced preference '{Id}' failed; it remains stored alongside the current one.", member.PreferenceId);
                    }
                }
            }
        }

        return new PreferencePersistenceResult
        {
            BySourceKey = preferencesByKey,
            CreatedIds = preferencesCreatedHere,
            Count = persistedPrefCount,
        };
    }
}

internal sealed record PreferencePersistenceResult
{
    public required IReadOnlyDictionary<string, Preference> BySourceKey { get; init; }
    public required IReadOnlySet<string> CreatedIds { get; init; }
    public required int Count { get; init; }
}
