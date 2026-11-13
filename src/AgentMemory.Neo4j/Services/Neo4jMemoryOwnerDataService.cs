using System.Globalization;
using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Queries;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Neo4j.Schema.Extensions;
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
    TimeProvider clock,
    Microsoft.Extensions.Options.IOptions<Neo4jOptions>? neo4jOptions = null) : IMemoryOwnerDataService
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
        cancellationToken.ThrowIfCancellationRequested();
        // ILC-30: with identity storage the export carries the identity state too (format 2), so an import can rebuild it.
        var identity = IdentityStorageEnabled;
        var parameters = new Dictionary<string, object?> { ["ownerId"] = ownerId };
        return await tx.ReadAsync(async runner =>
        {
            async Task<List<IRecord>> All(string query) =>
                await (await runner.RunAsync(query, parameters).ConfigureAwait(false)).ToListAsync().ConfigureAwait(false);
            static MemoryLink Link(IRecord r) => new(r["from"].As<string>(), r["to"].As<string>());

            return new OwnerMemoryExport
            {
                OwnerId = ownerId,
                ExportedAtUtc = clock.GetUtcNow(),
                // Embeddings are left out: an import embeds again with its own model.
                Entities = [.. (await All(OwnerDataQueries.ExportEntities).ConfigureAwait(false)).Select(r => Neo4jEntityRepository.MapToEntity(r["n"].As<INode>(), null))],
                Facts = [.. (await All(OwnerDataQueries.ExportFacts).ConfigureAwait(false)).Select(r => Neo4jFactRepository.MapToFact(r["n"].As<INode>(), null))],
                Preferences = [.. (await All(OwnerDataQueries.ExportPreferences).ConfigureAwait(false)).Select(r => Neo4jPreferenceRepository.MapToPreference(r["n"].As<INode>(), null))],
                Relationships = [.. (await All(OwnerDataQueries.ExportRelationships).ConfigureAwait(false)).Select(r => Neo4jRelationshipRepository.MapToRelationship(r["r"].As<IRelationship>()))],
                Supersessions = [.. (await All(OwnerDataQueries.ExportSupersessions).ConfigureAwait(false)).Select(Link)],
                About = [.. (await All(OwnerDataQueries.ExportAbout).ConfigureAwait(false)).Select(Link)],
                IdentityResolutions = identity ? [.. (await All(OwnerDataQueries.ExportIdentityResolutions).ConfigureAwait(false)).Select(Link)] : null,
                IdentityState = identity ? await ExportIdentityAsync(All).ConfigureAwait(false) : null,
                Format = identity ? OwnerMemoryExport.IdentityFormat : OwnerMemoryExport.CurrentFormat,
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OwnerImportResult> ImportAsync(OwnerMemoryExport export, string ownerId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(export);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        if (export.Format is not (OwnerMemoryExport.CurrentFormat or OwnerMemoryExport.IdentityFormat))
            throw new ArgumentException(
                $"export format '{export.Format}' is not '{OwnerMemoryExport.CurrentFormat}' or '{OwnerMemoryExport.IdentityFormat}'", nameof(export));

        cancellationToken.ThrowIfCancellationRequested();

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var written = new Dictionary<string, int>(StringComparer.Ordinal);
        void Count(string kind) => written[kind] = written.GetValueOrDefault(kind) + 1;

        // ILC-26: an identity store's export keeps each mention's own entity next to the identity a confirmed decision
        // resolved it to (IdentityResolutions). Each is folded into that identity, which keeps the mention's name as an
        // alias, so a confirmed identity comes back as one entity. Decision history, evidence and open proposals stay
        // behind: what comes back is the current state. Imported entities are written as they are, never joined to what
        // the destination already holds, so an import cannot merge two different things.
        // ILC-30: with the identity state in the export and identity storage here, the store comes back whole instead:
        // nothing is folded, each observation keeps its own entity under a new key, and the decisions come back with it.
        var full = export.IdentityState is not null && IdentityStorageEnabled;
        var keys = full ? ObservationKeys(export.IdentityState!, ownerId) : new Dictionary<string, string>(StringComparer.Ordinal);
        var joined = full ? new Dictionary<string, string>(StringComparer.Ordinal) : Joined(export);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entity in export.Entities) names.TryAdd(entity.EntityId, entity.Name);
        var mentionNames = joined.GroupBy(pair => pair.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(pair => names[pair.Key]).ToList(), StringComparer.Ordinal);

        foreach (var entity in export.Entities)
        {
            if (joined.ContainsKey(entity.EntityId)) continue;
            var aliases = mentionNames.TryGetValue(entity.EntityId, out var mentioned)
                ? [.. entity.Aliases, .. mentioned.Where(name => !string.Equals(name, entity.Name, StringComparison.OrdinalIgnoreCase) &&
                    !entity.Aliases.Contains(name, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase)]
                : entity.Aliases;
            var stored = await entities.UpsertAsync(entity with
            {
                EntityId = ObservationId(entity.EntityId, keys) ?? ids.GenerateId(), OwnerId = ownerId, Metadata = Plain(entity.Metadata), Aliases = aliases,
                Embedding = await embeddings.EmbedAsync(entity.Name, cancellationToken).ConfigureAwait(false),
            }, cancellationToken).ConfigureAwait(false);
            map[entity.EntityId] = stored.EntityId;
            Count("entities");
        }
        // An identity not in the export is a shared entity: the mention goes to it as it is.
        foreach (var (mention, identity) in joined) map[mention] = map.GetValueOrDefault(identity, identity);
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
            // An end not in the export is a shared entity (the export holds nothing else): the edge goes to it as it is.
            var source = map.GetValueOrDefault(relationship.SourceEntityId, relationship.SourceEntityId);
            var target = map.GetValueOrDefault(relationship.TargetEntityId, relationship.TargetEntityId);
            // A mention related to its own identity becomes no edge once the two are one entity.
            if (string.Equals(source, target, StringComparison.Ordinal) &&
                !string.Equals(relationship.SourceEntityId, relationship.TargetEntityId, StringComparison.Ordinal)) continue;
            var stored = await relationships.UpsertAsync(relationship with
            {
                RelationshipId = ids.GenerateId(),
                SourceEntityId = source,
                TargetEntityId = target,
                OwnerId = ownerId,
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
        if (full) await ImportIdentityAsync(export.IdentityState!, export.OwnerId, ownerId, map, keys, written, cancellationToken).ConfigureAwait(false);
        return new OwnerImportResult { OwnerId = ownerId, Written = written, Ids = map };
    }

    private static async Task<OwnerIdentityState> ExportIdentityAsync(Func<string, Task<List<IRecord>>> all)
    {
        static IReadOnlyDictionary<string, OwnerGraphValue> Properties(IRecord row) =>
            row["props"].As<IDictionary<string, object>>()
                .Where(pair => pair.Value is not null)
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => OwnerIdentityPortability.ToValue(pair.Value), StringComparer.Ordinal);

        var nodes = new List<OwnerGraphNode>();
        foreach (var (label, queries) in OwnerDataQueries.IdentityRecords)
            nodes.AddRange((await all(queries.Export).ConfigureAwait(false))
                .Select(row => new OwnerGraphNode { Label = label, Key = row["key"].As<string>(), Properties = Properties(row) }));
        var entityProperties = (await all(OwnerDataQueries.ExportEntityIdentity).ConfigureAwait(false))
            .Select(row => new OwnerGraphNode { Label = "Entity", Key = row["key"].As<string>(), Properties = Properties(row) }).ToList();
        var edges = new List<OwnerGraphEdge>();
        foreach (var link in OwnerDataQueries.IdentityLinks)
            edges.AddRange((await all(link.Export).ConfigureAwait(false)).Select(row => new OwnerGraphEdge
            {
                Type = link.Type, FromLabel = link.FromLabel, From = row["from"].As<string>(),
                ToLabel = link.ToLabel, To = row["to"].As<string>(), Properties = Properties(row),
            }));
        return new OwnerIdentityState { Nodes = nodes, Edges = edges, EntityProperties = entityProperties };
    }

    /// <summary>Each exported decision's observation key and the key it gets for the new owner.</summary>
    private static Dictionary<string, string> ObservationKeys(OwnerIdentityState state, string ownerId)
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in state.Nodes.Where(node => node.Label == "IdentityDecision"))
            if (node.Properties.TryGetValue("identity_observation_key", out var key) && key.Text is { Length: > 0 } old)
                keys.TryAdd(old, OwnerIdentityPortability.NewObservationKey(ownerId, old));
        return keys;
    }

    /// <summary>An observation's entity id follows its key ("identity-observation-" + key), as the ingestion makes it.</summary>
    private static string? ObservationId(string entityId, IReadOnlyDictionary<string, string> keys) =>
        entityId.StartsWith(OwnerIdentityPortability.ObservationPrefix, StringComparison.Ordinal) &&
        keys.TryGetValue(entityId[OwnerIdentityPortability.ObservationPrefix.Length..], out var key)
            ? OwnerIdentityPortability.ObservationPrefix + key
            : null;

    /// <summary>
    /// ILC-30: the identity records for the new owner. A decision's id follows its new key, as the ingestion makes it;
    /// every other record gets a new id. Every reference is rewritten (records, entities, facts, keys and the owner's
    /// scope, inside stored JSON too); a link to an entity that was not imported (a shared one) is made to it as it is.
    /// </summary>
    private async Task ImportIdentityAsync(OwnerIdentityState state, string oldOwner, string ownerId,
        Dictionary<string, string> map, IReadOnlyDictionary<string, string> keys, Dictionary<string, int> written,
        CancellationToken cancellationToken)
    {
        foreach (var node in state.Nodes)
            map[node.Key] = node.Label == "IdentityDecision" && node.Key.StartsWith(OwnerIdentityPortability.DecisionPrefix, StringComparison.Ordinal) &&
                            keys.TryGetValue(node.Key[OwnerIdentityPortability.DecisionPrefix.Length..], out var key)
                ? OwnerIdentityPortability.DecisionPrefix + key
                : ids.GenerateId();
        var remap = new OwnerIdentityPortability.Remap(oldOwner, ownerId, map, keys);
        Dictionary<string, object?> Values(IReadOnlyDictionary<string, OwnerGraphValue> properties) =>
            properties.ToDictionary(pair => pair.Key, pair => OwnerIdentityPortability.FromValue(remap.Value(pair.Key, pair.Value)), StringComparer.Ordinal);

        foreach (var (label, queries) in OwnerDataQueries.IdentityRecords)
            await WriteRowsAsync(queries.Import, "identity:" + label,
                state.Nodes.Where(node => node.Label == label).Select(node => (object)Values(node.Properties)).ToList()).ConfigureAwait(false);
        await WriteRowsAsync(OwnerDataQueries.ImportEntityIdentity, "identity:entity-properties",
            state.EntityProperties.Where(node => map.ContainsKey(node.Key))
                .Select(node => (object)new Dictionary<string, object?> { ["key"] = map[node.Key], ["props"] = Values(node.Properties) }).ToList()).ConfigureAwait(false);
        foreach (var link in OwnerDataQueries.IdentityLinks)
            await WriteRowsAsync(link.Import, "identity:links",
                state.Edges.Where(edge => edge.Type == link.Type && edge.FromLabel == link.FromLabel && edge.ToLabel == link.ToLabel)
                    .Select(edge => (object)new Dictionary<string, object?>
                    {
                        ["from"] = remap.Id(edge.From), ["to"] = remap.Id(edge.To), ["props"] = Values(edge.Properties),
                    }).ToList()).ConfigureAwait(false);

        async Task WriteRowsAsync(string query, string kind, List<object> rows)
        {
            // A bounded batch at a time, so one owner's identity state never becomes one transaction.
            foreach (var batch in rows.Chunk(500))
            {
                var count = await tx.WriteAsync(async runner =>
                {
                    var cursor = await runner.RunAsync(query, new Dictionary<string, object?> { ["rows"] = batch }).ConfigureAwait(false);
                    return (await cursor.SingleAsync().ConfigureAwait(false))["written"].As<long>();
                }, cancellationToken).ConfigureAwait(false);
                if (count > 0) written[kind] = written.GetValueOrDefault(kind) + (int)count;
            }
        }
    }

    private bool IdentityStorageEnabled => neo4jOptions?.Value.Extensions.Contains(IdentityEvidenceSchemaExtension.ExtensionId) == true;

    /// <summary>
    /// Each exported mention and the exported (or shared) identity it was confirmed as, followed to the end of any chain.
    /// An entity that would be folded into itself is kept.
    /// </summary>
    internal static Dictionary<string, string> Joined(OwnerMemoryExport export)
    {
        var exported = export.Entities.Select(e => e.EntityId).ToHashSet(StringComparer.Ordinal);
        var joined = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var link in export.IdentityResolutions ?? [])
            if (!string.Equals(link.From, link.To, StringComparison.Ordinal) && exported.Contains(link.From)) joined.TryAdd(link.From, link.To);
        foreach (var mention in joined.Keys.ToList())
        {
            var identity = joined[mention];
            var seen = new HashSet<string>(StringComparer.Ordinal) { mention };
            while (joined.TryGetValue(identity, out var next) && seen.Add(identity)) identity = next;
            if (seen.Contains(identity)) joined.Remove(mention);
            else joined[mention] = identity;
        }
        return joined;
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
