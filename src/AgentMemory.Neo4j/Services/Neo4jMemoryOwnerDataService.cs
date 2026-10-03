using System.Globalization;
using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Queries;
using AgentMemory.Neo4j.Repositories;
using Neo4j.Driver;

namespace AgentMemory.Neo4j.Services;

/// <summary>G3 (PLAN 40.47): erase, export and import an owner's data.</summary>
internal sealed class Neo4jMemoryOwnerDataService(
    INeo4jTransactionRunner tx,
    IEntityRepository entities,
    IFactRepository facts,
    IPreferenceRepository preferences,
    IRelationshipRepository relationships,
    IEmbeddingOrchestrator embeddings,
    IIdGenerator ids,
    IClock clock) : IMemoryOwnerDataService
{
    public async Task<OwnerErasure> EraseAsync(string ownerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        var deleted = new Dictionary<string, long>(StringComparer.Ordinal);
        // Memories first, then the transcript: a batch at a time, so one owner's store never becomes one transaction.
        foreach (var query in new[] { OwnerDataQueries.EraseOwnedBatch, OwnerDataQueries.EraseMessagesBatch, OwnerDataQueries.EraseConversationsBatch })
        {
            while (true)
            {
                var batch = await tx.WriteAsync(async runner =>
                {
                    var cursor = await runner.RunAsync(query, new Dictionary<string, object?> { ["ownerId"] = ownerId }).ConfigureAwait(false);
                    return (await cursor.ToListAsync().ConfigureAwait(false))
                        .Select(r => (Label: r["label"].As<string>() ?? "?", Count: r["deleted"].As<long>()))
                        .Where(row => row.Count > 0)
                        .ToList();
                }, cancellationToken).ConfigureAwait(false);
                if (batch.Count == 0) break;
                foreach (var (label, count) in batch) deleted[label] = deleted.GetValueOrDefault(label) + count;
            }
        }
        return new OwnerErasure { OwnerId = ownerId, Deleted = deleted };
    }

    public async Task<OwnerMemoryExport> ExportAsync(string ownerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        var parameters = new Dictionary<string, object?> { ["ownerId"] = ownerId };
        return await tx.ReadAsync(async runner =>
        {
            async Task<List<IRecord>> All(string query) =>
                await (await runner.RunAsync(query, parameters).ConfigureAwait(false)).ToListAsync().ConfigureAwait(false);
            static MemoryLink Link(IRecord r) => new(r["from"].As<string>(), r["to"].As<string>());

            return new OwnerMemoryExport
            {
                OwnerId = ownerId,
                ExportedAtUtc = clock.UtcNow,
                // Embeddings are left out: an import embeds again with its own model.
                Entities = [.. (await All(OwnerDataQueries.ExportEntities).ConfigureAwait(false)).Select(r => Neo4jEntityRepository.MapToEntity(r["n"].As<INode>(), null))],
                Facts = [.. (await All(OwnerDataQueries.ExportFacts).ConfigureAwait(false)).Select(r => Neo4jFactRepository.MapToFact(r["n"].As<INode>(), null))],
                Preferences = [.. (await All(OwnerDataQueries.ExportPreferences).ConfigureAwait(false)).Select(r => Neo4jPreferenceRepository.MapToPreference(r["n"].As<INode>(), null))],
                Relationships = [.. (await All(OwnerDataQueries.ExportRelationships).ConfigureAwait(false)).Select(r => Neo4jRelationshipRepository.MapToRelationship(r["r"].As<IRelationship>()))],
                Supersessions = [.. (await All(OwnerDataQueries.ExportSupersessions).ConfigureAwait(false)).Select(Link)],
                About = [.. (await All(OwnerDataQueries.ExportAbout).ConfigureAwait(false)).Select(Link)],
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OwnerImportResult> ImportAsync(OwnerMemoryExport export, string ownerId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(export);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        if (export.Format != OwnerMemoryExport.CurrentFormat)
            throw new ArgumentException($"export format '{export.Format}' is not '{OwnerMemoryExport.CurrentFormat}'", nameof(export));

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var written = new Dictionary<string, int>(StringComparer.Ordinal);
        void Count(string kind) => written[kind] = written.GetValueOrDefault(kind) + 1;

        foreach (var entity in export.Entities)
        {
            var stored = await entities.UpsertAsync(entity with
            {
                EntityId = ids.GenerateId(), OwnerId = ownerId, Metadata = Plain(entity.Metadata),
                Embedding = await embeddings.EmbedAsync(entity.Name, cancellationToken).ConfigureAwait(false),
            }, cancellationToken).ConfigureAwait(false);
            map[entity.EntityId] = stored.EntityId;
            Count("entities");
        }
        foreach (var fact in export.Facts)
        {
            var stored = await facts.UpsertAsync(fact with
            {
                FactId = ids.GenerateId(), OwnerId = ownerId, Metadata = Plain(fact.Metadata),
                Embedding = await embeddings.EmbedFactAsync(fact.Subject, fact.Predicate, fact.Object, cancellationToken).ConfigureAwait(false),
            }, cancellationToken).ConfigureAwait(false);
            map[fact.FactId] = stored.FactId;
            if (fact.InvalidatedAtUtc is { } closedAt)
                await RunAsync(OwnerDataQueries.RestoreFactClosing, new() { ["id"] = stored.FactId, ["invalidatedAt"] = Instant(closedAt), ["reason"] = fact.InvalidatedReason }, cancellationToken).ConfigureAwait(false);
            Count("facts");
        }
        foreach (var preference in export.Preferences)
        {
            var stored = await preferences.UpsertAsync(preference with
            {
                PreferenceId = ids.GenerateId(), OwnerId = ownerId, Metadata = Plain(preference.Metadata),
                Embedding = await embeddings.EmbedAsync(preference.PreferenceText, cancellationToken).ConfigureAwait(false),
            }, cancellationToken).ConfigureAwait(false);
            map[preference.PreferenceId] = stored.PreferenceId;
            if (preference.InvalidatedAtUtc is { } closedAt)
                await RunAsync(OwnerDataQueries.RestorePreferenceClosing, new() { ["id"] = stored.PreferenceId, ["invalidatedAt"] = Instant(closedAt) }, cancellationToken).ConfigureAwait(false);
            Count("preferences");
        }
        foreach (var relationship in export.Relationships)
        {
            // An edge whose ends were not exported (another owner's entity) cannot be placed, and is left out.
            if (!map.TryGetValue(relationship.SourceEntityId, out var source) || !map.TryGetValue(relationship.TargetEntityId, out var target)) continue;
            var stored = await relationships.UpsertAsync(relationship with
            {
                RelationshipId = ids.GenerateId(), SourceEntityId = source, TargetEntityId = target, OwnerId = ownerId,
                Metadata = Plain(relationship.Metadata),
            }, cancellationToken).ConfigureAwait(false);
            map[relationship.RelationshipId] = stored.RelationshipId;
            Count("relationships");
        }
        foreach (var link in export.Supersessions)
        {
            if (!map.TryGetValue(link.From, out var loser) || !map.TryGetValue(link.To, out var winner)) continue;
            await RunAsync(OwnerDataQueries.LinkSupersession, new() { ["from"] = loser, ["to"] = winner }, cancellationToken).ConfigureAwait(false);
            Count("supersessions");
        }
        foreach (var link in export.About)
        {
            if (!map.TryGetValue(link.From, out var fact)) continue;
            // An entity not in the export is a shared one: the link goes to it as it is.
            await facts.CreateAboutRelationshipAsync(fact, map.GetValueOrDefault(link.To, link.To), cancellationToken).ConfigureAwait(false);
            Count("about");
        }
        return new OwnerImportResult { OwnerId = ownerId, Written = written, Ids = map };
    }

    private Task RunAsync(string query, Dictionary<string, object?> parameters, CancellationToken cancellationToken) =>
        tx.WriteAsync(async runner => await runner.RunAsync(query, parameters).ConfigureAwait(false), cancellationToken);

    private static string Instant(DateTimeOffset at) => at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// Metadata as the store takes it: an export read back from JSON carries <see cref="JsonElement"/> values, which the
    /// driver cannot write; they become strings, numbers and booleans again.
    /// </summary>
    internal static IReadOnlyDictionary<string, object> Plain(IReadOnlyDictionary<string, object> metadata) =>
        metadata.Values.Any(v => v is JsonElement)
            ? metadata.ToDictionary(pair => pair.Key, pair => pair.Value is JsonElement element ? Plain(element) : pair.Value, StringComparer.Ordinal)
            : metadata;

    private static object Plain(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString()!,
        JsonValueKind.Number => element.TryGetInt64(out var whole) ? whole : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => element.EnumerateArray().Select(Plain).ToList(),
        _ => element.GetRawText(),
    };
}
