using System.Globalization;
using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Resolution;
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
                IdentityState = identity ? await ExportIdentityAsync(runner, All, cancellationToken).ConfigureAwait(false) : null,
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
        var state = export.IdentityState is not null && IdentityStorageEnabled ? export.IdentityState : null;
        // PR260-F5: every import is its own copy, with its own keys and ids, even of the same file into the same owner.
        var importId = ids.GenerateId();
        var keys = state is not null ? ObservationKeys(state, ownerId, importId) : new Dictionary<string, string>(StringComparer.Ordinal);
        // PR260-F8: the export carries no messages. A copy's sources are markers of its own, never the original owner's
        // messages, which may live in this very store.
        var messages = state is not null ? MessageIds(export, state, ownerId) : new Dictionary<string, string>(StringComparer.Ordinal);
        IReadOnlyList<string> Sources(IReadOnlyList<string> original) =>
            messages.Count == 0 ? original : [.. original.Select(id => messages.GetValueOrDefault(id, id))];
        var joined = state is not null ? new Dictionary<string, string>(StringComparer.Ordinal) : Joined(export);
        // PR260-F9/F10: an entity the file names but the import does not create must be a shared entity here. One that
        // belongs to an owner stops the import before anything is written; one that is missing leaves its link out.
        var missing = await CheckEndpointsAsync(export, state, joined, cancellationToken).ConfigureAwait(false);
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
                SourceMessageIds = Sources(entity.SourceMessageIds),
                Embedding = await embeddings.EmbedAsync(entity.Name, cancellationToken).ConfigureAwait(false),
            }, cancellationToken).ConfigureAwait(false);
            map[entity.EntityId] = stored.EntityId;
            Count("entities");
        }
        // PR260-F1: the retired entities identity records point at come back retired (below), so those links reach them.
        // Retired entities are never searched, so they are not embedded.
        foreach (var entity in state?.RetiredEntities ?? [])
        {
            var stored = await entities.UpsertAsync(entity with
            {
                EntityId = ObservationId(entity.EntityId, keys) ?? ids.GenerateId(), OwnerId = ownerId, Metadata = Plain(entity.Metadata),
                SourceMessageIds = Sources(entity.SourceMessageIds), Embedding = null,
            }, cancellationToken).ConfigureAwait(false);
            map[entity.EntityId] = stored.EntityId;
            Count("identity:retired-entities");
        }
        // An identity not in the export is a shared entity (checked above): the mention goes to it as it is.
        foreach (var (mention, identity) in joined) map[mention] = map.GetValueOrDefault(identity, identity);
        foreach (var fact in export.Facts)
        {
            var stored = await facts.UpsertAsync(fact with
            {
                FactId = ids.GenerateId(), OwnerId = ownerId, Metadata = Plain(fact.Metadata), SourceMessageIds = Sources(fact.SourceMessageIds),
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
                PreferenceId = ids.GenerateId(), OwnerId = ownerId, Metadata = Plain(preference.Metadata), SourceMessageIds = Sources(preference.SourceMessageIds),
                Embedding = await embeddings.EmbedAsync(preference.PreferenceText, cancellationToken).ConfigureAwait(false),
            }, cancellationToken).ConfigureAwait(false);
            map[preference.PreferenceId] = stored.PreferenceId;
            if (preference.InvalidatedAtUtc is { } closedAt)
                await RunAsync(OwnerDataQueries.RestorePreferenceClosing, new() { ["id"] = stored.PreferenceId, ["invalidatedAt"] = Instant(closedAt) }, cancellationToken).ConfigureAwait(false);
            Count("preferences");
        }
        foreach (var relationship in export.Relationships)
        {
            // An end not in the export is a shared entity (checked above): the edge goes to it as it is, or, when this store
            // does not hold it, is left out rather than made to a bare node.
            var source = map.GetValueOrDefault(relationship.SourceEntityId, relationship.SourceEntityId);
            var target = map.GetValueOrDefault(relationship.TargetEntityId, relationship.TargetEntityId);
            if (missing.Contains(source) || missing.Contains(target)) { Count("skipped:relationships"); continue; }
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
                SourceMessageIds = Sources(relationship.SourceMessageIds),
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
            // An entity not in the export is a shared one (checked above): the link goes to it as it is, if this store has it.
            var entity = map.GetValueOrDefault(link.To, link.To);
            if (missing.Contains(entity)) { Count("skipped:about"); continue; }
            await facts.CreateAboutRelationshipAsync(fact, entity, cancellationToken).ConfigureAwait(false);
            Count("about");
        }
        if (state is not null)
            await ImportIdentityAsync(state, export.OwnerId, ownerId, map, keys, messages, missing, written, cancellationToken).ConfigureAwait(false);
        return new OwnerImportResult { OwnerId = ownerId, Written = written, Ids = map };
    }

    /// <summary>
    /// PR260-F9/F10: what a file names beyond what the import creates. An entity end must be a shared entity in this
    /// store; any that belongs to an owner refuses the whole import, before anything is written. A missing one is returned,
    /// and its links are left out. An identity link's fact end, and each identity record a record or a link names, must be
    /// in the file. A fold target that is missing is not folded into: the mention comes back as its own entity.
    /// </summary>
    private async Task<HashSet<string>> CheckEndpointsAsync(OwnerMemoryExport export, OwnerIdentityState? state,
        Dictionary<string, string> joined, CancellationToken cancellationToken)
    {
        var created = export.Entities.Select(entity => entity.EntityId).Concat(state?.RetiredEntities.Select(entity => entity.EntityId) ?? [])
            .ToHashSet(StringComparer.Ordinal);
        var folds = await ForeignAsync(joined.Values.Where(id => !created.Contains(id)).ToHashSet(StringComparer.Ordinal)).ConfigureAwait(false);
        foreach (var (mention, identity) in joined.ToList())
            if (folds.Contains(identity)) joined.Remove(mention);

        string Resolve(string id) => joined.TryGetValue(id, out var identity) ? identity : id;
        bool Created(string id) => created.Contains(id) && !joined.ContainsKey(id);
        var ends = export.Relationships.SelectMany(r => new[] { r.SourceEntityId, r.TargetEntityId }).Concat(export.About.Select(l => l.To));
        if (state is not null)
        {
            var records = state.Nodes.GroupBy(node => node.Label, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(node => node.Key).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
            var exportedFacts = export.Facts.Select(fact => fact.FactId).ToHashSet(StringComparer.Ordinal);
            bool Known(string label, string id) => label switch
            {
                "Entity" => true, // checked below, with the other entity ends
                "Fact" => exportedFacts.Contains(id),
                _ => records.TryGetValue(label, out var keys) && keys.Contains(id),
            };
            if (state.Edges.Any(edge => !Known(edge.FromLabel, edge.From) || !Known(edge.ToLabel, edge.To)) ||
                state.Nodes.Any(node => !IdentityProperties.ContainsKey(node.Label)))
                throw new ArgumentException("The export's identity state names records it does not carry; nothing was imported.", nameof(export));
            var decisions = records.GetValueOrDefault("IdentityDecision") ?? [];
            foreach (var node in state.Nodes)
                foreach (var property in new[] { "identity_decision_id", "identity_source_decision_id", "identity_target_decision_id" })
                    if (node.Properties.TryGetValue(property, out var value) && value.Text is { } decision && !decisions.Contains(decision))
                        throw new ArgumentException("The export's identity state names a decision it does not carry; nothing was imported.", nameof(export));
            ends = ends.Concat(state.Edges.Where(edge => edge.FromLabel == "Entity").Select(edge => edge.From))
                .Concat(state.Edges.Where(edge => edge.ToLabel == "Entity").Select(edge => edge.To));
        }
        return await ForeignAsync(ends.Select(Resolve).Where(id => !Created(id)).ToHashSet(StringComparer.Ordinal)).ConfigureAwait(false);

        async Task<HashSet<string>> ForeignAsync(HashSet<string> candidates)
        {
            if (candidates.Count == 0) return new HashSet<string>(StringComparer.Ordinal);
            var rows = await tx.ReadAsync(async runner =>
            {
                var cursor = await runner.RunAsync(OwnerDataQueries.ForeignEndpoints,
                    new Dictionary<string, object?> { ["ids"] = candidates.ToArray() }).ConfigureAwait(false);
                return await cursor.ToListAsync().ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
            if (rows.Any(row => row["present"].As<bool>() && !row["shared"].As<bool>()))
                throw new ArgumentException("The export links to entities that belong to an owner in this store and are not in the export; " +
                    "nothing was imported.", nameof(export));
            return rows.Where(row => !row["present"].As<bool>()).Select(row => row["id"].As<string>()).ToHashSet(StringComparer.Ordinal);
        }
    }

    private static async Task<OwnerIdentityState> ExportIdentityAsync(IAsyncQueryRunner runner, Func<string, Task<List<IRecord>>> all,
        CancellationToken cancellationToken)
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
        var retired = await all(OwnerDataQueries.ExportRetiredIdentityEntities).ConfigureAwait(false);
        var retirements = retired.Select(row =>
        {
            var properties = new Dictionary<string, OwnerGraphValue>(StringComparer.Ordinal);
            if (row["invalidatedAt"].As<object?>() is { } invalidatedAt) properties["invalidated_at"] = OwnerIdentityPortability.ToValue(invalidatedAt);
            if (row["mergedInto"].As<string?>() is { } mergedInto) properties["merged_into"] = OwnerIdentityPortability.ToValue(mergedInto);
            return new OwnerGraphNode { Label = "Entity", Key = row["n"].As<INode>()["id"].As<string>(), Properties = properties };
        }).ToList();
        return new OwnerIdentityState
        {
            Nodes = nodes, Edges = edges, EntityProperties = entityProperties,
            RetiredEntities = [.. retired.Select(row => Neo4jEntityRepository.MapToEntity(row["n"].As<INode>(), null))],
            Retirements = retirements,
            CurrentConfirmations = await CurrentConfirmationsAsync(runner, nodes, cancellationToken).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// PR260-F7: the confirmed decisions whose target, and contextual candidates, still match their fingerprints. Only
    /// these get their imported target's fingerprints, so a copy needs re-evaluation exactly where the source would.
    /// </summary>
    private static async Task<IReadOnlyList<string>> CurrentConfirmationsAsync(IAsyncQueryRunner runner,
        IReadOnlyList<OwnerGraphNode> nodes, CancellationToken cancellationToken)
    {
        var current = new List<string>();
        foreach (var node in nodes.Where(node => node.Label == "IdentityDecision"))
        {
            if (!node.Properties.TryGetValue("identity_record_json", out var json) || json.Text is null ||
                !node.Properties.TryGetValue("identity_target_snapshot", out var stored) || stored.Text is null) continue;
            var decision = IdentityDecisionPayload.Decode(json.Text);
            if (decision.Status != IdentityDecisionKind.Confirmed || decision.ConfirmedEntityId is not { } target) continue;
            if (await Neo4jIdentityDecisionRepository.TargetSnapshotAsync(runner, decision, target, cancellationToken).ConfigureAwait(false) != stored.Text) continue;
            if (decision.ContextualEvidence is not null)
            {
                var contextual = Neo4jIdentityDecisionRepository.ReadContextualSnapshots(decision);
                var now = contextual is null ? null
                    : await Neo4jIdentityDecisionRepository.CandidateSnapshotsAsync(runner, decision, contextual.Keys, cancellationToken).ConfigureAwait(false);
                if (now is null || now.Count != contextual!.Count ||
                    now.Any(pair => !contextual.TryGetValue(pair.Key, out var hash) || hash != pair.Value)) continue;
            }
            current.Add(node.Key);
        }
        return current;
    }

    /// <summary>Each exported decision's observation key and the key it gets in this import.</summary>
    private static Dictionary<string, string> ObservationKeys(OwnerIdentityState state, string ownerId, string importId)
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in state.Nodes.Where(node => node.Label == "IdentityDecision"))
            if (node.Properties.TryGetValue("identity_observation_key", out var key) && key.Text is { Length: > 0 } old)
                keys.TryAdd(old, OwnerIdentityPortability.NewObservationKey(ownerId, importId, old));
        return keys;
    }

    /// <summary>
    /// PR260-F8: every source message id the exported memories and identity records name, and the destination's own marker
    /// for it. The messages themselves are not exported.
    /// </summary>
    private static Dictionary<string, string> MessageIds(OwnerMemoryExport export, OwnerIdentityState state, string ownerId)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        void Add(IEnumerable<string?>? values) { foreach (var value in values ?? []) if (!string.IsNullOrWhiteSpace(value)) found.Add(value); }
        foreach (var entity in export.Entities.Concat(state.RetiredEntities)) Add(entity.SourceMessageIds);
        foreach (var fact in export.Facts) Add(fact.SourceMessageIds);
        foreach (var preference in export.Preferences) Add(preference.SourceMessageIds);
        foreach (var relationship in export.Relationships) Add(relationship.SourceMessageIds);
        foreach (var node in state.Nodes)
        {
            if (node.Properties.TryGetValue("identity_source_ids", out var list)) Add(list.Items?.Select(item => item.Text));
            if (!node.Properties.TryGetValue("identity_record_json", out var json) || json.Text is null) continue;
            IdentityDecision? decision = null;
            IdentityRelationshipObservation? relationship = null;
            try
            {
                if (node.Label is "IdentityDecision" or "IdentityDecisionRevision") decision = IdentityDecisionPayload.Decode(json.Text);
                else if (node.Label == "IdentityRelationshipObservation") relationship = IdentityRelationshipPayload.Decode(json.Text);
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or FormatException)
            {
                throw new ArgumentException("An identity record in the export is not valid; nothing was imported.", nameof(export), exception);
            }
            // PR260-F9: a record names its owner inside its JSON too, and import rewrites only the exported owner there. A
            // record about anyone else would make later reviews of the copy read that owner's scope.
            if ((decision is not null && decision.Observation.OwnerId != export.OwnerId) ||
                (relationship is not null && relationship.OwnerId != export.OwnerId))
                throw new ArgumentException("An identity record in the export belongs to another owner; nothing was imported.", nameof(export));
            if (decision is not null)
            {
                Add(decision.Context.SourceMessageIds); Add(decision.Observation.SourceMessageIds);
                foreach (var source in decision.Context.Sources) { Add([source.SourceId]); Add(source.OriginIds); }
            }
            if (relationship is not null) Add(relationship.SourceMessageIds);
        }
        return found.ToDictionary(id => id, id => OwnerIdentityPortability.NewMessageId(ownerId, id), StringComparer.Ordinal);
    }

    /// <summary>An observation's entity id follows its key ("identity-observation-" + key), as the ingestion makes it.</summary>
    private static string? ObservationId(string entityId, IReadOnlyDictionary<string, string> keys) =>
        entityId.StartsWith(OwnerIdentityPortability.ObservationPrefix, StringComparison.Ordinal) &&
        keys.TryGetValue(entityId[OwnerIdentityPortability.ObservationPrefix.Length..], out var key)
            ? OwnerIdentityPortability.ObservationPrefix + key
            : null;

    /// <summary>What the identity-evidence schema declares for each record: the only properties an import writes.</summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> IdentityProperties =
        new IdentityEvidenceSchemaExtension().DeclaredProperties;

    /// <summary>
    /// ILC-30: the identity records for the new owner. A decision's id follows its new key, as the ingestion makes it;
    /// every other record gets a new id. Every reference is rewritten (records, entities, facts, keys, source messages and
    /// the owner's scope, inside stored JSON too). PR260-F9: each record is the importing owner's whatever the file says,
    /// and carries only the properties its schema declares; each link's ends are checked again where it is made.
    /// </summary>
    private async Task ImportIdentityAsync(OwnerIdentityState state, string oldOwner, string ownerId,
        Dictionary<string, string> map, IReadOnlyDictionary<string, string> keys, IReadOnlyDictionary<string, string> messages,
        IReadOnlySet<string> missing, Dictionary<string, int> written, CancellationToken cancellationToken)
    {
        foreach (var node in state.Nodes)
            map[node.Key] = node.Label == "IdentityDecision" && node.Key.StartsWith(OwnerIdentityPortability.DecisionPrefix, StringComparison.Ordinal) &&
                            keys.TryGetValue(node.Key[OwnerIdentityPortability.DecisionPrefix.Length..], out var key)
                ? OwnerIdentityPortability.DecisionPrefix + key
                : ids.GenerateId();
        // PR260-F6: an observation's entity id follows its key whether or not the entity exists yet: a confirmed decision
        // reserves it, and a later revocation creates it.
        foreach (var (old, renewed) in keys)
            map.TryAdd(OwnerIdentityPortability.ObservationPrefix + old, OwnerIdentityPortability.ObservationPrefix + renewed);
        var references = new Dictionary<string, string>(map, StringComparer.Ordinal);
        foreach (var (old, renewed) in messages) references.TryAdd(old, renewed);
        var remap = new OwnerIdentityPortability.Remap(oldOwner, ownerId, references, keys);
        var dropped = 0;
        Dictionary<string, object?> Record(OwnerGraphNode node)
        {
            var declared = IdentityProperties[node.Label];
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (name, value) in node.Properties)
                if (declared.Contains(name)) values[name] = OwnerIdentityPortability.FromValue(remap.Value(name, value));
                else dropped++;
            values["id"] = map[node.Key];
            values["owner_id"] = ownerId;
            values["owner_scope"] = "owned:" + ownerId;
            return values;
        }

        foreach (var (label, queries) in OwnerDataQueries.IdentityRecords)
            await WriteRowsAsync(queries.Import, "identity:" + label,
                state.Nodes.Where(node => node.Label == label).Select(node => (object)Record(node)).ToList()).ConfigureAwait(false);
        if (dropped > 0) written["identity:properties-dropped"] = dropped;
        await WriteRowsAsync(OwnerDataQueries.ImportEntityIdentity, "identity:entity-properties",
            state.EntityProperties.Where(node => map.ContainsKey(node.Key))
                .Select(node => (object)new Dictionary<string, object?>
                {
                    ["key"] = map[node.Key],
                    ["props"] = node.Properties.ToDictionary(pair => pair.Key, pair => OwnerIdentityPortability.FromValue(remap.Value(pair.Key, pair.Value)), StringComparer.Ordinal),
                }).ToList()).ConfigureAwait(false);
        await WriteRowsAsync(OwnerDataQueries.RestoreEntityRetirement, "identity:retirements",
            state.Retirements.Where(node => map.ContainsKey(node.Key)).Select(node => (object)new Dictionary<string, object?>
            {
                ["id"] = map[node.Key],
                ["invalidatedAt"] = node.Properties.TryGetValue("invalidated_at", out var at) ? at.Text : null,
                // Merged into something this import does not carry: kept retired, merged into itself rather than into nothing.
                ["mergedInto"] = node.Properties.TryGetValue("merged_into", out var into) && into.Text is { } target
                    ? map.GetValueOrDefault(target, map[node.Key]) : null,
            }).ToList()).ConfigureAwait(false);
        foreach (var link in OwnerDataQueries.IdentityLinks)
        {
            var edges = state.Edges.Where(edge => edge.Type == link.Type && edge.FromLabel == link.FromLabel && edge.ToLabel == link.ToLabel)
                .Select(edge => (From: remap.Id(edge.From), To: remap.Id(edge.To))).ToList();
            var left = edges.Count(edge => missing.Contains(edge.From) || missing.Contains(edge.To));
            if (left > 0) written["skipped:identity-links"] = written.GetValueOrDefault("skipped:identity-links") + left;
            await WriteRowsAsync(link.Import, "identity:links", edges.Where(edge => !missing.Contains(edge.From) && !missing.Contains(edge.To))
                .Select(edge => (object)new Dictionary<string, object?> { ["from"] = edge.From, ["to"] = edge.To }).ToList()).ConfigureAwait(false);
        }
        foreach (var decision in state.CurrentConfirmations)
            if (map.TryGetValue(decision, out var id))
                await RefreshConfirmationAsync(id, ownerId, cancellationToken).ConfigureAwait(false);

        async Task WriteRowsAsync(string query, string kind, List<object> rows)
        {
            // A bounded batch at a time, so one owner's identity state never becomes one transaction.
            foreach (var batch in rows.Chunk(500))
            {
                var count = await tx.WriteAsync(async runner =>
                {
                    var cursor = await runner.RunAsync(query, new Dictionary<string, object?> { ["rows"] = batch, ["ownerId"] = ownerId }).ConfigureAwait(false);
                    return (await cursor.SingleAsync().ConfigureAwait(false))["written"].As<long>();
                }, cancellationToken).ConfigureAwait(false);
                if (count > 0) written[kind] = written.GetValueOrDefault(kind) + (int)count;
            }
        }
    }

    /// <summary>
    /// PR260-F7: a confirmation that was current at export gets the fingerprint of its imported target, and a contextual
    /// one its imported candidates' fingerprints, computed the way a decision computes them.
    /// </summary>
    private Task RefreshConfirmationAsync(string decisionId, string ownerId, CancellationToken cancellationToken) =>
        tx.WriteAsync(async runner =>
        {
            var parameters = new Dictionary<string, object?> { ["id"] = decisionId, ["ownerId"] = ownerId };
            var rows = await (await runner.RunAsync(OwnerDataQueries.ReadImportedDecision, parameters).ConfigureAwait(false)).ToListAsync().ConfigureAwait(false);
            if (rows.Count != 1) return;
            var decision = IdentityDecisionPayload.Decode(rows[0]["recordJson"].As<string>());
            if (decision.Status != IdentityDecisionKind.Confirmed || decision.ConfirmedEntityId is not { } target) return;
            var snapshot = await Neo4jIdentityDecisionRepository.TargetSnapshotAsync(runner, decision, target, cancellationToken).ConfigureAwait(false);
            if (decision.ContextualEvidence is not null && Neo4jIdentityDecisionRepository.ReadContextualSnapshots(decision) is { } contextual &&
                await Neo4jIdentityDecisionRepository.CandidateSnapshotsAsync(runner, decision, contextual.Keys, cancellationToken).ConfigureAwait(false) is { } now)
            {
                var receipt = System.Text.Json.Nodes.JsonNode.Parse(decision.EvidenceJson)!.AsObject();
                receipt["contextualCandidateSnapshots"] = new System.Text.Json.Nodes.JsonObject(
                    now.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .Select(pair => KeyValuePair.Create(pair.Key, (System.Text.Json.Nodes.JsonNode?)System.Text.Json.Nodes.JsonValue.Create(pair.Value))));
                decision = decision with { EvidenceJson = receipt.ToJsonString() };
            }
            parameters["recordJson"] = IdentityDecisionPayload.Encode(decision);
            parameters["targetSnapshot"] = snapshot;
            await (await runner.RunAsync(OwnerDataQueries.RefreshImportedDecision, parameters).ConfigureAwait(false)).ConsumeAsync().ConfigureAwait(false);
        }, cancellationToken);

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
