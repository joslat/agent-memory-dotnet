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
internal sealed partial class MemoryExtractionPipeline : IMemoryExtractionPipeline
{
    private readonly IExtractionStage _extractionStage;
    private readonly IPersistenceStage _persistenceStage;
    private readonly ILogger<MemoryExtractionPipeline> _logger;
    private readonly IMemoryIsolationPolicy _isolationPolicy;
    private readonly ExtractionOptions _options;
    private readonly IReadOnlyList<IMultiSessionUnifiedMemoryExtractor> _multiSessionExtractors;
    // Nullable so a host that builds this pipeline by hand -- or a container assembled before 30.6 --
    // keeps working. The accountant is an enrichment; its absence must degrade to "no aggregates", not
    // to a failed ingestion.
    private readonly Extraction.Derivation.IDerivedMemoryAccountant? _accountant;

    // Internal ctor: the stage interfaces are internal to Core, so this type is activated by an
    // explicit factory in AddAgentMemoryCore (the default DI activator only selects public ctors).
    internal MemoryExtractionPipeline(
        IExtractionStage extractionStage,
        IPersistenceStage persistenceStage,
        ILogger<MemoryExtractionPipeline> logger,
        IMemoryIsolationPolicy isolationPolicy,
        IOptions<ExtractionOptions>? extractionOptions = null,
        IEnumerable<IMultiSessionUnifiedMemoryExtractor>? multiSessionExtractors = null,
        Extraction.Derivation.IDerivedMemoryAccountant? accountant = null)
    {
        _extractionStage = extractionStage;
        _persistenceStage = persistenceStage;
        _logger = logger;
        _isolationPolicy = isolationPolicy;
        _options = extractionOptions?.Value ?? new ExtractionOptions();
        _multiSessionExtractors = (multiSessionExtractors ?? [])
            .ToList().AsReadOnly();
        _accountant = accountant;
    }

    /// <summary>
    /// Runs the session accountant over what this batch just persisted (30.6).
    /// </summary>
    /// <remarks>
    /// <para>
    /// After <c>PersistAsync</c>, never before: an aggregate has to be computed from facts that are
    /// actually in the graph, and computing it from staged candidates would produce a number describing
    /// a state that might never commit.
    /// </para>
    /// <para>
    /// Nothing about the outcome reaches <c>ExtractionResult</c>. The accountant is best-effort by
    /// design, and threading a "derived count" into the result would tempt a caller into treating it as
    /// part of the extraction contract — at which point a failure to compute an aggregate would start
    /// failing ingestions.
    /// </para>
    /// </remarks>
    private async Task AccountAsync(
        ExtractionStageResult staged, string? ownerId, CancellationToken cancellationToken)
    {
        if (_accountant is null || !_options.DerivedMemory.Enabled) return;

        try
        {
            await _accountant.AccountAsync(staged, ownerId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Derived-memory accounting failed; the batch's facts are stored.");
        }
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

        // E2. Context reaches the extractors as context; the window keeps it out of provenance and
        // out of the extraction targets, which is what stops it inflating S2 confidence and R7
        // mention counts on facts that merely stayed inside the window.
        // Without context, the original call — the off state stays identical at the call itself, not
        // merely in what the extractors end up seeing.
        var staged = request.ContextMessages.Count == 0
            ? await _extractionStage.ExtractAsync(
                request.Messages, request.TypesToExtract, scope, cancellationToken).ConfigureAwait(false)
            : await _extractionStage.ExtractWithContextAsync(
                new ExtractionWindow { Targets = request.Messages, Context = request.ContextMessages },
                request.TypesToExtract, scope, cancellationToken).ConfigureAwait(false);

        var ownerId = _isolationPolicy.ResolveWriteOwner(request.UserId, nameof(ExtractAsync), MemoryOperationAccess.Tenant);
        // #92 Phase 3: a per-request TrustLevel override wins; otherwise fall back to the configured default.
        var trustLevel = request.TrustLevel ?? _options.DefaultTrustLevel;
        var persisted = await _persistenceStage.PersistAsync(staged, ownerId, trustLevel, cancellationToken).ConfigureAwait(false);
        await AccountAsync(staged, ownerId, cancellationToken).ConfigureAwait(false);

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
