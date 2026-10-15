using static AgentMemory.Core.Extraction.FactNames;
using static AgentMemory.Core.Extraction.PersistenceMetadata;
using static AgentMemory.Core.Extraction.PersistenceOutcomes;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using Microsoft.Extensions.Logging;

namespace AgentMemory.Core.Extraction;

/// <summary>Prepares canonical fact writes with owner-scoped existing trust and per-item provenance.</summary>
internal sealed class FactPreparation
{
    private readonly IFactRepository _factRepository;
    private readonly IIdGenerator _idGenerator;
    private readonly IClock _clock;
    private readonly ILogger _logger;

    public FactPreparation(IFactRepository factRepository, IIdGenerator idGenerator, IClock clock, ILogger logger)
    {
        _factRepository = factRepository;
        _idGenerator = idGenerator;
        _clock = clock;
        _logger = logger;
    }

    public async Task<(Fact Item, string SourceKey)?> PrepareAsync(
        PreparedFact preparedFact, FactNames names, string? ownerId, MemoryTrustLevel trustLevel,
        IReadOnlyList<string> sourceMessageIds, List<IngestionItemOutcome> outcomes, bool failFast,
        CancellationToken cancellationToken = default)
    {
        var extracted = preparedFact.Item;
        var factSourceKey = FactSourceKey(extracted);
        var subject = names.StoredName(extracted, subject: true);
        var @object = names.StoredName(extracted, subject: false);
        // G7 (40.51). Found live: "Rosa | is named | Rosa" was stored and recalled as the first memory, taking a slot
        // and saying nothing. A fact whose subject and object are the same name, after the names are resolved, is
        // skipped with its reason.
        if (string.Equals(subject.Trim(), @object.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            outcomes.Add(new IngestionItemOutcome
            {
                Kind = MemoryItemKind.Fact,
                Stage = IngestionStage.Validation,
                Status = IngestionItemStatus.Skipped,
                SourceKey = factSourceKey,
                ErrorCode = MemoryErrorCodes.FactTautology,
                ErrorMessage = "Subject and object are the same name, so the fact says nothing.",
            });
            _logger.LogDebug("Skipped fact '{Fact}': its subject and object are the same name.", factSourceKey);
            return null;
        }
        try
        {
            // Trust is monotonic for owner-scoped facts. The pre-fetch deliberately excludes shared
            // facts and carries an existing triple's casing forward so an exact MERGE cannot create a
            // casing-only duplicate. Rank 20 will make this read-modify-write atomic; feat-04 leaves
            // that owner boundary and trust behavior unchanged.
            Fact? existingFact = string.IsNullOrEmpty(ownerId)
                ? null
                : await _factRepository.FindByTripleAsync(
                    subject, extracted.Predicate, @object,
                    MemoryScope.For(ownerId, includeShared: false), cancellationToken).ConfigureAwait(false);
            // Per-item refinement before the existing per-batch composition. At defaults SourceRole
            // is null on every item and this is the identity, so the trust a host sees is byte-for-
            // byte what it was; it becomes non-trivial only when assistant content is extracted,
            // which is the exact moment "who claimed this" stops being answerable from the batch.
            var requestTrustLevel = SourceRoleTrust.Refine(trustLevel, extracted.SourceRole);
            // Per-item provenance (L3c). Identity at defaults -- SourceTurn is null unless
            // ExtractionProvenanceMode.PerItem asked for it -- and a reported turn that does not
            // resolve keeps the batch links rather than inventing a narrower wrong one.
            var factMessageIds = SourceTurnProvenance.Resolve(extracted.SourceTurn, sourceMessageIds);
            var effectiveFactTrustLevel = existingFact is null
                ? requestTrustLevel
                : MaxTrustLevel(existingFact.Metadata.GetTrustLevel(), requestTrustLevel);
            var factMetadata = existingFact is null
                ? MemoryTrustMetadataExtensions.CreateWithTrustLevel(effectiveFactTrustLevel)
                : existingFact.Metadata.WithTrustLevel(effectiveFactTrustLevel);
            if (!string.Equals(subject, extracted.Subject, StringComparison.Ordinal) ||
                !string.Equals(@object, extracted.Object, StringComparison.Ordinal))
            {
                var surfaces = new Dictionary<string, object>(factMetadata);
                if (!string.Equals(subject, extracted.Subject, StringComparison.Ordinal)) surfaces["subject_surface"] = extracted.Subject;
                if (!string.Equals(@object, extracted.Object, StringComparison.Ordinal)) surfaces["object_surface"] = extracted.Object;
                factMetadata = surfaces;
            }

            return (new Fact
            {
                FactId = _idGenerator.GenerateId(),
                Subject = existingFact?.Subject ?? subject,
                Predicate = existingFact?.Predicate ?? extracted.Predicate,
                Object = existingFact?.Object ?? @object,
                Confidence = extracted.Confidence,
                ValidFrom = extracted.ValidFrom,
                ValidFromPrecision = extracted.ValidFromPrecision,
                ValidUntil = extracted.ValidUntil,
                ValidUntilPrecision = extracted.ValidUntilPrecision,
                OccurredOn = extracted.OccurredOn,
                OccurredOnPrecision = extracted.OccurredOnPrecision,
                Embedding = preparedFact.Embedding,
                OwnerId = ownerId,
                SourceMessageIds = factMessageIds,
                CreatedAtUtc = _clock.UtcNow,
                Metadata = factMetadata
            }, factSourceKey);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error preparing fact '{Key}' for persistence.", factSourceKey);
            RecordFailureAndMaybeThrow(outcomes, failFast, MemoryItemKind.Fact, IngestionStage.Persistence,
                MemoryErrorCodes.FactPersistenceFailed, factSourceKey, null, ex,
                $"Ingestion failed fast: persistence failed for fact '{factSourceKey}'.");
            return null;
        }
    }
}
