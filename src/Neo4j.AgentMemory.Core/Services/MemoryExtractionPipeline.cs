using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.Core.Extraction;

namespace Neo4j.AgentMemory.Core.Services;

/// <summary>
/// Orchestrates the two-stage extraction pipeline: <see cref="IExtractionStage"/> (run extractors,
/// merge, filter, validate, resolve) followed by <see cref="IPersistenceStage"/> (embed, upsert,
/// wire provenance).  Implements the public <see cref="IMemoryExtractionPipeline"/> interface.
/// </summary>
public sealed class MemoryExtractionPipeline : IMemoryExtractionPipeline
{
    private readonly IExtractionStage _extractionStage;
    private readonly IPersistenceStage _persistenceStage;
    private readonly ILogger<MemoryExtractionPipeline> _logger;

    internal MemoryExtractionPipeline(
        IExtractionStage extractionStage,
        IPersistenceStage persistenceStage,
        ILogger<MemoryExtractionPipeline> logger)
    {
        _extractionStage = extractionStage;
        _persistenceStage = persistenceStage;
        _logger = logger;
    }

    public async Task<ExtractionResult> ExtractAsync(
        ExtractionRequest request,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogDebug(
            "Starting extraction for session {SessionId}, {MessageCount} messages.",
            request.SessionId, request.Messages.Count);

        var staged = await _extractionStage.ExtractAsync(
            request.Messages, request.TypesToExtract, cancellationToken);

        var persisted = await _persistenceStage.PersistAsync(staged, cancellationToken);

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
}
