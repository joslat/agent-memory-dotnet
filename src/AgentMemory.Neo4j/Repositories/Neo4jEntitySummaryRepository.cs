using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Queries;
using Microsoft.Extensions.Logging;
using Neo4j.Driver;

namespace AgentMemory.Neo4j.Repositories;

/// <summary>
/// Neo4j persistence for synthesized entity summaries (S1).
/// </summary>
/// <remarks>
/// The summary node keeps its own <c>:EXTRACTED_FROM</c> edges to the facts it was written from. That
/// is the part a summary column in a relational row cannot do, and it is what makes "which facts is
/// this claim standing on?" a query rather than a comment.
/// </remarks>
internal sealed class Neo4jEntitySummaryRepository : IEntitySummaryRepository
{
    private readonly INeo4jTransactionRunner _tx;
    private readonly ILogger<Neo4jEntitySummaryRepository> _logger;

    /// <summary>Public ctor: the type is internal, and DI can only activate it through one.</summary>
    public Neo4jEntitySummaryRepository(
        INeo4jTransactionRunner tx,
        ILogger<Neo4jEntitySummaryRepository> logger)
    {
        _tx = tx;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<EntitySummary> UpsertAsync(
        EntitySummary summary, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(summary);

        await _tx.WriteAsync(async runner =>
        {
            var cursor = await runner.RunAsync(EntitySummaryQueries.Upsert, new
            {
                summaryId = summary.SummaryId,
                entityId = summary.EntityId,
                content = summary.Content,
                sourceFactIds = summary.SourceFactIds.ToArray(),
                sourceFingerprint = summary.SourceFingerprint,
                ownerId = summary.OwnerId,
                ownerKey = summary.OwnerId ?? Neo4jFactRepository.OwnerKeyShared,
                generatedAt = summary.GeneratedAtUtc.ToString("o"),
            }).ConfigureAwait(false);
            await cursor.ConsumeAsync().ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug(
            "Stored summary for entity {EntityId} over {SourceCount} fact(s).",
            summary.EntityId, summary.SourceFactIds.Count);
        return summary;
    }

    /// <inheritdoc/>
    public Task<EntitySummary?> GetByEntityAsync(
        string entityId, MemoryScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);

        return _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                EntitySummaryQueries.GetByEntity(scope.OwnerId is not null, scope.IncludeShared),
                new { entityId, ownerId = scope.OwnerId }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Count == 0 ? null : Map(records[0]["s"].As<INode>());
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<bool> DeleteByEntityAsync(
        string entityId, MemoryScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);

        return _tx.WriteAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                EntitySummaryQueries.DeleteByEntity(scope.OwnerId is not null, scope.IncludeShared),
                new { entityId, ownerId = scope.OwnerId }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Count > 0;
        }, cancellationToken);
    }

    private static EntitySummary Map(INode node) => new()
    {
        SummaryId = node.Properties.TryGetValue("id", out var id) ? id.As<string>() : string.Empty,
        EntityId = node["entity_id"].As<string>(),
        Content = node["content"].As<string>(),
        SourceFactIds = node.Properties.TryGetValue("source_fact_ids", out var ids)
            ? ids.As<IList<object>>().Select(v => v.ToString()!).ToList()
            : [],
        SourceFingerprint = node["source_fingerprint"].As<string>(),
        OwnerId = node.Properties.TryGetValue("owner_id", out var owner) ? owner.As<string>() : null,
        GeneratedAtUtc = Neo4jDateTimeHelper.ReadNullableDateTimeOffset(node["generated_at"])
            ?? DateTimeOffset.MinValue,
    };
}
