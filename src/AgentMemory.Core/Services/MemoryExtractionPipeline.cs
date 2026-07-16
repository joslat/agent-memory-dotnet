using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;

namespace AgentMemory.Core.Services;

/// <summary>
/// Orchestrates the two-stage extraction pipeline: <see cref="IExtractionStage"/> (run extractors,
/// merge, filter, validate, resolve) followed by <see cref="IPersistenceStage"/> (embed, upsert,
/// wire provenance).  Implements the public <see cref="IMemoryExtractionPipeline"/> interface.
/// </summary>
internal sealed class MemoryExtractionPipeline : IMemoryExtractionPipeline
{
    private readonly IExtractionStage _extractionStage;
    private readonly IPersistenceStage _persistenceStage;
    private readonly ILogger<MemoryExtractionPipeline> _logger;
    private readonly IMemoryIsolationPolicy _isolationPolicy;
    private readonly ExtractionOptions _options;

    // Internal ctor: the stage interfaces are internal to Core, so this type is activated by an
    // explicit factory in AddAgentMemoryCore (the default DI activator only selects public ctors).
    internal MemoryExtractionPipeline(
        IExtractionStage extractionStage,
        IPersistenceStage persistenceStage,
        ILogger<MemoryExtractionPipeline> logger,
        IMemoryIsolationPolicy isolationPolicy,
        IOptions<ExtractionOptions>? extractionOptions = null)
    {
        _extractionStage = extractionStage;
        _persistenceStage = persistenceStage;
        _logger = logger;
        _isolationPolicy = isolationPolicy;
        _options = extractionOptions?.Value ?? new ExtractionOptions();
    }

    /// <inheritdoc/>
    public async Task<ExtractionResult> ExtractAsync(
        ExtractionRequest request,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogDebug(
            "Starting extraction for session {SessionId}, {MessageCount} messages.",
            request.SessionId, request.Messages.Count);

        // Owner-scope entity resolution (R1): the resolver's candidate set is confined to this owner's
        // own + shared entities, so an incoming entity can't resolve onto another owner's private entity.
        // Resolved through the central isolation policy (#100) so SingleTenant/WarnOnUnscoped/
        // StrictMultiTenant behave identically for extraction as for every other tenant operation.
        var scope = _isolationPolicy.ResolveReadScope(
            explicitScope: null, request.UserId, nameof(ExtractAsync), MemoryOperationAccess.Tenant);

        var staged = await _extractionStage.ExtractAsync(
            request.Messages, request.TypesToExtract, scope, cancellationToken).ConfigureAwait(false);

        var ownerId = _isolationPolicy.ResolveWriteOwner(request.UserId, nameof(ExtractAsync), MemoryOperationAccess.Tenant);
        // #92 Phase 3: a per-request TrustLevel override wins; otherwise fall back to the configured default.
        var trustLevel = request.TrustLevel ?? _options.DefaultTrustLevel;
        var persisted = await _persistenceStage.PersistAsync(staged, ownerId, trustLevel, cancellationToken).ConfigureAwait(false);

        sw.Stop();
        _logger.LogInformation(
            "Extraction complete for session {SessionId}: {EntityCount} entities, {FactCount} facts, " +
            "{PrefCount} preferences, {RelCount} relationships in {ElapsedMs}ms.",
            request.SessionId,
            persisted.EntityCount,
            persisted.FactCount,
            persisted.PreferenceCount,
            persisted.RelationshipCount,
            sw.ElapsedMilliseconds);

        return new ExtractionResult
        {
            Entities = staged.RawEntities,
            Facts = staged.RawFacts,
            Preferences = staged.RawPreferences,
            Relationships = staged.RawRelationships,
            SourceMessageIds = staged.SourceMessageIds,
            Status = ComputeStatus(persisted.Outcomes),
            Outcomes = persisted.Outcomes,
            Metadata = new Dictionary<string, object>
            {
                ["sessionId"] = request.SessionId,
                ["extractionTimeMs"] = sw.ElapsedMilliseconds,
                ["entityCount"] = persisted.EntityCount,
                ["factCount"] = persisted.FactCount,
                ["preferenceCount"] = persisted.PreferenceCount,
                ["relationshipCount"] = persisted.RelationshipCount
            }
        };
    }

    /// <summary>
    /// Derives the overall <see cref="IngestionStatus"/> (#101) from item outcomes: any failure makes
    /// it at best partial; a total loss (failures present, nothing succeeded) is Failed; no failures at
    /// all — including the trivial case of nothing to ingest — is Succeeded.
    /// </summary>
    private static IngestionStatus ComputeStatus(IReadOnlyList<IngestionItemOutcome> outcomes)
    {
        var failed = 0;
        var succeeded = 0;
        foreach (var outcome in outcomes)
        {
            if (outcome.Status == IngestionItemStatus.Failed) failed++;
            else if (outcome.Status == IngestionItemStatus.Succeeded) succeeded++;
        }

        if (failed == 0) return IngestionStatus.Succeeded;
        return succeeded > 0 ? IngestionStatus.PartiallySucceeded : IngestionStatus.Failed;
    }
}
