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
            var state = identity ? await ExportIdentityAsync(runner, All, ownerId, cancellationToken).ConfigureAwait(false) : null;
            if (state is { Nodes.Count: 0, Edges.Count: 0, EntityProperties.Count: 0, RetiredEntities.Count: 0 }) state = null;

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
                IdentityState = state,
                // REV2-13: format 2 only when there is identity state to carry, so a store with none stays readable by 2.2.
                Format = state is not null ? OwnerMemoryExport.IdentityFormat : OwnerMemoryExport.CurrentFormat,
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OwnerImportResult> ImportAsync(OwnerMemoryExport export, string ownerId, CancellationToken cancellationToken = default)
    {
        // REV3-6: an import writes in many transactions. When one fails, what this import wrote of its own is removed again
        // before the failure is reported, so a retry starts from the store as it was rather than next to a half copy.
        var created = new List<(string Label, string Id, string? From)>();
        try
        {
            return await ImportCoreAsync(export, ownerId, created, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (created.Count > 0)
        {
            await RemovePartialImportAsync(created, ownerId, failure).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// REV3-6: removes what a failed import created (its entities, facts, preferences, relationships and identity
    /// records, with their links), only the importing owner's. A fact the import merged into one the owner already had is
    /// not undone. If the removal fails too, the original failure is still the one reported, and its <c>Data</c> says so.
    /// </summary>
    private async Task RemovePartialImportAsync(List<(string Label, string Id, string? From)> created, string ownerId, Exception failure)
    {
        try
        {
            foreach (var group in created.GroupBy(item => item.Label).OrderBy(group => group.Key == "RELATED_TO" ? 0 : 1))
                foreach (var batch in group.Chunk(500))
                    await tx.WriteAsync(async runner =>
                    {
                        var cursor = await runner.RunAsync(OwnerDataQueries.RemoveImported[group.Key], new Dictionary<string, object?>
                        {
                            ["rows"] = batch.Select(item => new Dictionary<string, object?> { ["id"] = item.Id, ["from"] = item.From }).ToArray(),
                            ["ownerId"] = ownerId,
                        }).ConfigureAwait(false);
                        await cursor.ConsumeAsync().ConfigureAwait(false);
                    }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception cleanup) when (cleanup is not OutOfMemoryException)
        {
            failure.Data["AgentMemory.ImportCleanupFailed"] = cleanup.Message;
        }
    }

    private async Task<OwnerImportResult> ImportCoreAsync(OwnerMemoryExport export, string ownerId,
        List<(string Label, string Id, string? From)> created, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(export);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        if (export.Format is not (OwnerMemoryExport.CurrentFormat or OwnerMemoryExport.IdentityFormat))
            throw new ArgumentException(
                $"export format '{export.Format}' is not '{OwnerMemoryExport.CurrentFormat}' or '{OwnerMemoryExport.IdentityFormat}'", nameof(export));
        // REV1-H1: a record must belong to the exported owner, so a file that names no owner names nobody it could check.
        if (string.IsNullOrWhiteSpace(export.OwnerId))
            throw new ArgumentException("The export names no owner; nothing was imported.", nameof(export));

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
        // REV1-L8: without identity storage here the store comes back as its current state (ILC-26); say so.
        if (export.IdentityState is not null && state is null) written["identity:state-not-imported"] = 1;
        // PR260-F5: every import is its own copy, with its own keys and ids, even of the same file into the same owner.
        var importId = ids.GenerateId();
        var keys = state is not null ? ObservationKeys(state, ownerId, importId) : new Dictionary<string, string>(StringComparer.Ordinal);
        // PR260-F8, REV1-H2: the export carries no messages. A source message id stays as it is only when it is a message of
        // the importing owner in this store (a restore into the same owner); any other becomes the importing owner's marker,
        // in every format, so imported memories never point at, quote or link to another owner's messages.
        var messages = await MarkersAsync(SourceMessageIds(export, state), ownerId, cancellationToken).ConfigureAwait(false);
        // REV2-10: markers are predictable; one that already exists as a message was planted, and links made to it would
        // reach someone else's conversation.
        if (await AnyMessageAsync(messages.Values, cancellationToken).ConfigureAwait(false))
            throw new ArgumentException("A message id this import would use already exists in this store; nothing was imported.", nameof(export));
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
            created.Add(("Entity", stored.EntityId, null));
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
            created.Add(("Entity", stored.EntityId, null));
            Count("identity:retired-entities");
        }
        // REV1-M5: retired again at once, before anything else is written, so a failure later never leaves them live.
        if (state is not null) await RestoreRetirementsAsync(state, map, ownerId, written, cancellationToken).ConfigureAwait(false);
        // An identity not in the export is a shared entity (checked above): the mention goes to it as it is.
        foreach (var (mention, identity) in joined) map[mention] = map.GetValueOrDefault(identity, identity);
        var held = new HashSet<string>(StringComparer.Ordinal);
        // REV3-1: a replaced value is a closed period in the store (its period key is its own id), so the same value said
        // again later starts a new period. The file's supersession losers get that back as they are closed; otherwise a later
        // live copy of the same fact in the file would land on the closed one.
        var losers = export.Supersessions.Select(link => link.From).ToHashSet(StringComparer.Ordinal);
        var importedFacts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fact in export.Facts)
        {
            // REV2-14: the destination owner already holds this fact in its open period (a restore into a non-empty owner):
            // the import points at it and leaves it as it is. Upserting would replace its sources, reopen it, or close it.
            if (await HeldFactAsync(fact, ownerId, cancellationToken).ConfigureAwait(false) is { } existing && !importedFacts.Contains(existing))
            {
                map[fact.FactId] = existing;
                held.Add(fact.FactId);
                Count("facts:already-held");
                continue;
            }
            var requestedFact = ids.GenerateId();
            var stored = await facts.UpsertAsync(fact with
            {
                FactId = requestedFact, OwnerId = ownerId, Metadata = Plain(fact.Metadata), SourceMessageIds = Sources(fact.SourceMessageIds),
                Embedding = await embeddings.EmbedFactAsync(fact.Subject, fact.Predicate, fact.Object, cancellationToken).ConfigureAwait(false),
            }, cancellationToken).ConfigureAwait(false);
            map[fact.FactId] = stored.FactId;
            importedFacts.Add(stored.FactId);
            if (stored.FactId == requestedFact) created.Add(("Fact", requestedFact, null));
            if (fact.InvalidatedAtUtc is { } closedAt)
                await RunAsync(OwnerDataQueries.RestoreFactClosing, new()
                {
                    ["id"] = stored.FactId, ["invalidatedAt"] = Instant(closedAt), ["reason"] = fact.InvalidatedReason,
                    ["closesPeriod"] = losers.Contains(fact.FactId),
                }, cancellationToken).ConfigureAwait(false);
            Count("facts");
        }
        foreach (var preference in export.Preferences)
        {
            var requestedPreference = ids.GenerateId();
            var stored = await preferences.UpsertAsync(preference with
            {
                PreferenceId = requestedPreference, OwnerId = ownerId, Metadata = Plain(preference.Metadata), SourceMessageIds = Sources(preference.SourceMessageIds),
                Embedding = await embeddings.EmbedAsync(preference.PreferenceText, cancellationToken).ConfigureAwait(false),
            }, cancellationToken).ConfigureAwait(false);
            map[preference.PreferenceId] = stored.PreferenceId;
            if (stored.PreferenceId == requestedPreference) created.Add(("Preference", requestedPreference, null));
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
            created.Add(("RELATED_TO", stored.RelationshipId, source));
            Count("relationships");
        }
        foreach (var link in export.Supersessions)
        {
            if (held.Contains(link.From) || held.Contains(link.To)) continue; // REV2-14: the destination's own facts are not relinked.
            if (!map.TryGetValue(link.From, out var loser) || !map.TryGetValue(link.To, out var winner)) continue;
            await RunAsync(OwnerDataQueries.LinkSupersession, new() { ["from"] = loser, ["to"] = winner }, cancellationToken).ConfigureAwait(false);
            Count("supersessions");
        }
        foreach (var link in export.About)
        {
            if (held.Contains(link.From) || !map.TryGetValue(link.From, out var fact)) continue;
            // An entity not in the export is a shared one (checked above): the link goes to it as it is, if this store has it.
            var entity = map.GetValueOrDefault(link.To, link.To);
            if (missing.Contains(entity)) { Count("skipped:about"); continue; }
            await facts.CreateAboutRelationshipAsync(fact, entity, cancellationToken).ConfigureAwait(false);
            Count("about");
        }
        if (state is not null)
            await ImportIdentityAsync(state, export, ownerId, map, keys, messages, missing, written, created, cancellationToken).ConfigureAwait(false);
        return new OwnerImportResult { OwnerId = ownerId, Written = written, Ids = map };
    }

    /// <summary>
    /// PR260-F9/F10: what a file names beyond what the import creates. Only a shared entity in this store is linked to.
    /// REV3-8: one that belongs to an owner is returned with the missing ones, and its links are left out and counted the
    /// same way, so an import never tells whether another owner holds an id. An identity link's fact end, and each identity record a record or a link names, must be
    /// in the file. A fold target that is missing is not folded into: the mention comes back as its own entity.
    /// </summary>
    private async Task<HashSet<string>> CheckEndpointsAsync(OwnerMemoryExport export, OwnerIdentityState? state,
        Dictionary<string, string> joined, CancellationToken cancellationToken)
    {
        var created = export.Entities.Select(entity => entity.EntityId).Concat(state?.RetiredEntities.Select(entity => entity.EntityId) ?? [])
            .ToHashSet(StringComparer.Ordinal);
        // REV2-1: a fold target the file does not carry is folded into only when it is shared here. One that is missing, or
        // an owner's (a retired identity an older export still names), leaves the mention as its own entity: nothing is
        // refused, and nothing is joined to an entity the import does not bring.
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
            // REV2-8: only identity records, each key once and none shared with a memory the file carries, and each decision
            // with its own observation key as text. Anything else would collide or be overwritten half-way through.
            var memoryKeys = created.Concat(export.Facts.Select(fact => fact.FactId)).Concat(export.Preferences.Select(p => p.PreferenceId))
                .Concat(export.Relationships.Select(r => r.RelationshipId)).ToHashSet(StringComparer.Ordinal);
            var decisionKeys = state.Nodes.Where(node => node.Label == "IdentityDecision")
                .Select(node => node.Properties.TryGetValue("identity_observation_key", out var key) && key.Kind == OwnerGraphValue.String ? key.Text : null)
                .ToList();
            if (state.Edges.Any(edge => !Known(edge.FromLabel, edge.From) || !Known(edge.ToLabel, edge.To)) ||
                state.Nodes.Any(node => !OwnerDataQueries.IdentityRecords.ContainsKey(node.Label)) ||
                state.Nodes.GroupBy(node => node.Key, StringComparer.Ordinal).Any(group => group.Count() > 1) ||
                state.Nodes.Any(node => memoryKeys.Contains(node.Key)) ||
                decisionKeys.Any(string.IsNullOrWhiteSpace) ||
                state.Nodes.Any(node => node.Label is "IdentityDecision" or "IdentityDecisionRevision" or "IdentityRelationshipObservation" &&
                    (!node.Properties.TryGetValue("identity_record_json", out var record) || record.Kind != OwnerGraphValue.String || record.Text is null)) ||
                decisionKeys.GroupBy(key => key, StringComparer.Ordinal).Any(group => group.Count() > 1))
                throw new ArgumentException("The export's identity state names records it does not carry, or carries one twice; nothing was imported.",
                    nameof(export));
            var decisions = records.GetValueOrDefault("IdentityDecision") ?? [];
            foreach (var node in state.Nodes)
                foreach (var property in new[] { "identity_decision_id", "identity_source_decision_id", "identity_target_decision_id" })
                    if (node.Properties.TryGetValue(property, out var value) && value.Text is { } decision && !decisions.Contains(decision))
                        throw new ArgumentException("The export's identity state names a decision it does not carry; nothing was imported.", nameof(export));
            ends = ends.Concat(state.Edges.Where(edge => edge.FromLabel == "Entity").Select(edge => edge.From))
                .Concat(state.Edges.Where(edge => edge.ToLabel == "Entity").Select(edge => edge.To));
            // A current decision's stored record names entities too, with or without a link to them: its confirmed target,
            // its pending candidates and its observation (the entity its key reserves, or an exported one). Export carries
            // every retired one of these, so an honest file passes. Revisions are history and are not checked.
            var observationKeys = state.Nodes.Where(node => node.Label == "IdentityDecision")
                .Select(node => node.Properties.TryGetValue("identity_observation_key", out var key) ? key.Text : null)
                .OfType<string>().ToHashSet(StringComparer.Ordinal);
            foreach (var node in state.Nodes.Where(node => node.Label == "IdentityDecision"))
            {
                if (!node.Properties.TryGetValue("identity_record_json", out var json) || json.Text is null) continue;
                var decision = IdentityDecisionPayload.Decode(json.Text);
                var observation = decision.Observation.EntityId;
                var reserved = observation.StartsWith(OwnerIdentityPortability.ObservationPrefix, StringComparison.Ordinal) &&
                    observationKeys.Contains(observation[OwnerIdentityPortability.ObservationPrefix.Length..]);
                ends = ends.Concat(decision.PendingEntityIds)
                    .Concat(decision.ConfirmedEntityId is { } target ? new[] { target } : Array.Empty<string>())
                    .Concat(reserved ? Array.Empty<string>() : new[] { observation });
            }
            // REV1-M5: every typed value is checked here (a retirement date among them), so a malformed one cannot stop the
            // import half-way.
            if (state.Nodes.Concat(state.EntityProperties).Concat(state.Retirements).SelectMany(node => node.Properties.Values)
                    .Concat(state.Edges.SelectMany(edge => edge.Properties.Values)).Any(value => !Valid(value)) ||
                state.Retirements.Any(node => node.Properties.TryGetValue("invalidated_at", out var at) && at.Kind != OwnerGraphValue.DateTime))
                throw new ArgumentException("The export holds a value of an unknown kind or an invalid date; nothing was imported.", nameof(export));
        }
        return await ForeignAsync(ends.Select(Resolve).Where(id => !Created(id)).ToHashSet(StringComparer.Ordinal)).ConfigureAwait(false);

        static bool Valid(OwnerGraphValue value) => value.Kind switch
        {
            OwnerGraphValue.String => value.Text is not null,
            OwnerGraphValue.Integer => value.Long is not null,
            OwnerGraphValue.Number => value.Double is not null,
            OwnerGraphValue.Boolean => value.Flag is not null,
            OwnerGraphValue.DateTime => DateTimeOffset.TryParse(value.Text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _),
            OwnerGraphValue.List => (value.Items ?? []).All(Valid),
            _ => false,
        };

        async Task<HashSet<string>> ForeignAsync(HashSet<string> candidates)
        {
            if (candidates.Count == 0) return new HashSet<string>(StringComparer.Ordinal);
            var rows = await tx.ReadAsync(async runner =>
            {
                var cursor = await runner.RunAsync(OwnerDataQueries.ForeignEndpoints,
                    new Dictionary<string, object?> { ["ids"] = candidates.ToArray() }).ConfigureAwait(false);
                return await cursor.ToListAsync().ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
            // Returned: what is missing here and what an owner holds; neither is linked or folded into.
            return rows.Where(row => !row["shared"].As<bool>()).Select(row => row["id"].As<string>()).ToHashSet(StringComparer.Ordinal);
        }
    }

    private static async Task<OwnerIdentityState> ExportIdentityAsync(IAsyncQueryRunner runner, Func<string, Task<List<IRecord>>> all,
        string ownerId, CancellationToken cancellationToken)
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
        // Retired entities a current decision's record names without a link to them (an entity-only confirmation's target).
        var named = nodes.Where(node => node.Label == "IdentityDecision")
            .Select(node => node.Properties.TryGetValue("identity_record_json", out var json) ? json.Text : null).OfType<string>()
            .Select(IdentityDecisionPayload.Decode)
            .SelectMany(decision => decision.PendingEntityIds.Append(decision.Observation.EntityId)
                .Concat(decision.ConfirmedEntityId is { } target ? new[] { target } : Array.Empty<string>()))
            .Except(retired.Select(row => row["n"].As<INode>()["id"].As<string>()), StringComparer.Ordinal)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (named.Length > 0)
            retired.AddRange(await (await runner.RunAsync(OwnerDataQueries.ExportRetiredEntitiesById,
                new Dictionary<string, object?> { ["ownerId"] = ownerId, ["ids"] = named }).ConfigureAwait(false)).ToListAsync().ConfigureAwait(false));
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
    private static HashSet<string> SourceMessageIds(OwnerMemoryExport export, OwnerIdentityState? state)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        void Add(IEnumerable<string?>? values) { foreach (var value in values ?? []) if (!string.IsNullOrWhiteSpace(value)) found.Add(value); }
        foreach (var entity in export.Entities.Concat(state?.RetiredEntities ?? [])) Add(entity.SourceMessageIds);
        foreach (var fact in export.Facts) Add(fact.SourceMessageIds);
        foreach (var preference in export.Preferences) Add(preference.SourceMessageIds);
        foreach (var relationship in export.Relationships) Add(relationship.SourceMessageIds);
        foreach (var node in state?.Nodes ?? [])
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
        return found;
    }

    /// <summary>
    /// REV1-H2: the importing owner's marker for every source message id that is not one of its own messages in this
    /// store. Ids that are its own (a restore into the same owner) are left out, so they stay as they are.
    /// </summary>
    private async Task<Dictionary<string, string>> MarkersAsync(HashSet<string> sourceIds, string ownerId, CancellationToken cancellationToken)
    {
        var own = new HashSet<string>(StringComparer.Ordinal);
        foreach (var batch in sourceIds.Chunk(1000))
            own.UnionWith(await tx.ReadAsync(async runner =>
            {
                var cursor = await runner.RunAsync(OwnerDataQueries.OwnMessages,
                    new Dictionary<string, object?> { ["ids"] = batch, ["ownerId"] = ownerId }).ConfigureAwait(false);
                return (await cursor.ToListAsync().ConfigureAwait(false)).Select(row => row["id"].As<string>()).ToList();
            }, cancellationToken).ConfigureAwait(false));
        return sourceIds.Where(id => !own.Contains(id))
            .ToDictionary(id => id, id => OwnerIdentityPortability.NewMessageId(ownerId, id), StringComparer.Ordinal);
    }

    /// <summary>REV2-10: whether any of these ids already exists as a message.</summary>
    private async Task<bool> AnyMessageAsync(IEnumerable<string> messageIds, CancellationToken cancellationToken)
    {
        foreach (var batch in messageIds.Chunk(1000))
            if (await tx.ReadAsync(async runner => (await (await runner.RunAsync(OwnerDataQueries.ExistingMessages,
                    new Dictionary<string, object?> { ["ids"] = batch }).ConfigureAwait(false)).SingleAsync().ConfigureAwait(false))["taken"].As<long>(),
                    cancellationToken).ConfigureAwait(false) > 0)
                return true;
        return false;
    }

    /// <summary>REV2-14: the destination owner's fact a write of this one would merge onto (the same keys, its open period).</summary>
    private Task<string?> HeldFactAsync(Fact fact, string ownerId, CancellationToken cancellationToken) =>
        tx.ReadAsync(async runner =>
        {
            var rows = await (await runner.RunAsync(OwnerDataQueries.HeldFact, new Dictionary<string, object?>
            {
                ["subjectKey"] = AgentMemory.Core.Memory.MemoryTripleCanonicalizer.CanonicalValue(fact.Subject),
                ["predicateKey"] = AgentMemory.Core.Memory.MemoryTripleCanonicalizer.Canonical(fact.Predicate),
                ["objectKey"] = AgentMemory.Core.Memory.MemoryTripleCanonicalizer.CanonicalValue(fact.Object),
                ["ownerKey"] = OwnerStorageKey.Encode(ownerId), ["ownerId"] = ownerId,
            }).ConfigureAwait(false)).ToListAsync().ConfigureAwait(false);
            return rows.Count == 0 ? null : rows[0]["id"].As<string>();
        }, cancellationToken);

    /// <summary>REV1-M5: each imported retired entity's retirement, written as soon as the entities are.</summary>
    private async Task RestoreRetirementsAsync(OwnerIdentityState state, IReadOnlyDictionary<string, string> map, string ownerId,
        Dictionary<string, int> written, CancellationToken cancellationToken)
    {
        var rows = state.Retirements.Where(node => map.ContainsKey(node.Key)).Select(node => (object)new Dictionary<string, object?>
        {
            ["id"] = map[node.Key],
            ["invalidatedAt"] = node.Properties.TryGetValue("invalidated_at", out var at) ? at.Text : null,
            // Merged into something this import does not carry: kept retired, merged into itself rather than into nothing.
            ["mergedInto"] = node.Properties.TryGetValue("merged_into", out var into) && into.Text is { } target
                ? map.GetValueOrDefault(target, map[node.Key]) : null,
        }).ToList();
        foreach (var batch in rows.Chunk(500))
        {
            var count = await tx.WriteAsync(async runner =>
            {
                var cursor = await runner.RunAsync(OwnerDataQueries.RestoreEntityRetirement,
                    new Dictionary<string, object?> { ["rows"] = batch, ["ownerId"] = ownerId }).ConfigureAwait(false);
                return (await cursor.SingleAsync().ConfigureAwait(false))["written"].As<long>();
            }, cancellationToken).ConfigureAwait(false);
            if (count > 0) written["identity:retirements"] = written.GetValueOrDefault("identity:retirements") + (int)count;
        }
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
    private async Task ImportIdentityAsync(OwnerIdentityState state, OwnerMemoryExport export, string ownerId,
        Dictionary<string, string> map, IReadOnlyDictionary<string, string> keys,
        IReadOnlyDictionary<string, string> messages, IReadOnlySet<string> missing, Dictionary<string, int> written,
        List<(string Label, string Id, string? From)> created, CancellationToken cancellationToken)
    {
        var oldOwner = export.OwnerId;
        var entityKeys = export.Entities.Select(entity => entity.EntityId).ToList();
        // REV2-12: each link found by its kind and end once, not by a scan per record.
        var linkTo = state.Edges.GroupBy(edge => (edge.Type, edge.To)).ToDictionary(group => group.Key, group => group.First());
        var revisionOf = state.Edges.Where(edge => edge.Type == "HAS_IDENTITY_REVISION")
            .GroupBy(edge => edge.To, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().From, StringComparer.Ordinal);
        foreach (var node in state.Nodes.Where(node => node.Label == "IdentityDecision"))
            map[node.Key] = node.Key.StartsWith(OwnerIdentityPortability.DecisionPrefix, StringComparison.Ordinal) &&
                            keys.TryGetValue(node.Key[OwnerIdentityPortability.DecisionPrefix.Length..], out var key)
                ? OwnerIdentityPortability.DecisionPrefix + key
                : ids.GenerateId();
        foreach (var node in state.Nodes.Where(node => node.Label != "IdentityDecision"))
            map[node.Key] = ids.GenerateId();
        // PR260-F6: an observation's entity id follows its key whether or not the entity exists yet: a confirmed decision
        // reserves it, and a later revocation creates it.
        foreach (var (old, renewed) in keys)
            map.TryAdd(OwnerIdentityPortability.ObservationPrefix + old, OwnerIdentityPortability.ObservationPrefix + renewed);
        var references = new Dictionary<string, string>(map, StringComparer.Ordinal);
        foreach (var (old, renewed) in messages) references.TryAdd(old, renewed);
        var remap = new OwnerIdentityPortability.Remap(oldOwner, ownerId, references, keys);
        // PR260-N5: an evidence assertion's id follows its content, as asserting makes it, so the same key or alias asserted
        // again after the import is the same record: no duplicate, and no new evidence version for its entity.
        foreach (var node in state.Nodes.Where(node => node.Label == "IdentityEvidenceAssertion"))
        {
            if (!linkTo.TryGetValue(("HAS_IDENTITY_EVIDENCE", node.Key), out var evidence)) continue;
            var content = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (name, value) in node.Properties)
                if (IdentityProperties[node.Label].Contains(name) && name is not ("id" or "owner_id" or "created_at"))
                    content[name] = OwnerIdentityPortability.FromValue(remap.Value(name, value));
            content["owner_scope"] = "owned:" + ownerId;
            var assertionId = Neo4jIdentityEvidenceRepository.Hash(JsonSerializer.Serialize(new { entityId = remap.Id(evidence.From), properties = content }));
            map[node.Key] = assertionId;
            references[node.Key] = assertionId;
        }
        // PR260-N2: an observation's vector is the source model's. The copy's current decisions get the destination model's,
        // and their history none, so a later revocation never writes a vector from another model.
        var vectors = new Dictionary<string, float[]?>(StringComparer.Ordinal);
        foreach (var node in state.Nodes.Where(node => node.Label == "IdentityDecision"))
            if (node.Properties.TryGetValue("identity_record_json", out var json) && json.Text is { } text &&
                IdentityDecisionPayload.Decode(text).Observation is { Embedding: not null } observation)
                vectors[node.Key] = await embeddings.EmbedAsync(observation.Name, cancellationToken).ConfigureAwait(false) is { Length: > 0 } vector
                    ? vector : null;
        // REV1-S1: a binding's id follows its decision, fact and role, as binding makes it. REV3-1: two bindings that come to
        // the same id (one decision, one fact, one role) are one binding in the store; the second is left out.
        var bindingIds = new HashSet<string>(StringComparer.Ordinal);
        var duplicateBindings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in state.Nodes.Where(node => node.Label == "IdentityFactBinding"))
            if (linkTo.TryGetValue(("HAS_IDENTITY_BINDING", node.Key), out var bound) &&
                node.Properties.TryGetValue("identity_decision_id", out var decisionId) && decisionId.Text is { } decided && map.TryGetValue(decided, out var newDecision) &&
                node.Properties.TryGetValue("identity_fact_role", out var role) && role.Text is { } boundRole && map.TryGetValue(bound.From, out var newFact))
            {
                var bindingId = Neo4jIdentityDecisionRepository.BindingId(newDecision, newFact, boundRole);
                if (!bindingIds.Add(bindingId)) { duplicateBindings.Add(node.Key); continue; }
                map[node.Key] = bindingId;
                references[node.Key] = bindingId;
            }
        // REV1-H1: a decision record's own identifiers and owner are the copy's, whatever the file says: its decision id,
        // its observation key, its observer, and its observation entity (an imported one, or the one its key reserves).
        var importedEntities = entityKeys.Concat(state.RetiredEntities.Select(entity => entity.EntityId))
            .Where(map.ContainsKey).Select(id => map[id]).ToHashSet(StringComparer.Ordinal);
        var decisionKeys = state.Nodes.Where(node => node.Label == "IdentityDecision")
            .ToDictionary(node => node.Key, node => keys[node.Properties["identity_observation_key"].Text!], StringComparer.Ordinal);
        // REV3-3: confirmations whose target this store does not hold, and the decisions they become.
        var standalone = new Dictionary<string, IdentityDecision>(StringComparer.Ordinal);
        string Owned(OwnerGraphNode node, string record)
        {
            if (node.Label == "IdentityRelationshipObservation")
            {
                var observed = IdentityRelationshipPayload.Decode(record);
                return IdentityRelationshipPayload.Encode(observed with { OwnerId = ownerId });
            }
            var decisionNode = node.Label == "IdentityDecision" ? node.Key : revisionOf.GetValueOrDefault(node.Key);
            var decision = IdentityDecisionPayload.Decode(record);
            if (decisionNode is null || !decisionKeys.TryGetValue(decisionNode, out var key))
                return IdentityDecisionPayload.Encode(decision with { Observation = decision.Observation with { OwnerId = ownerId, Embedding = null } });
            var observationId = importedEntities.Contains(decision.Observation.EntityId)
                ? decision.Observation.EntityId : OwnerIdentityPortability.ObservationPrefix + key;
            if (node.Label == "IdentityDecision" && decision.Status == IdentityDecisionKind.Pending &&
                decision.PendingEntityIds.Any(missing.Contains))
            {
                // REV2-3: a candidate this store does not hold cannot be proposed here; with none left the observation stands
                // on its own (New), as identity itself decides when no candidate exists, and the diagnostic says why.
                var kept = decision.PendingEntityIds.Where(id => !missing.Contains(id)).ToArray();
                written["identity:candidates-not-in-store"] = written.GetValueOrDefault("identity:candidates-not-in-store") +
                    decision.PendingEntityIds.Count - kept.Length;
                if (kept.Length == 0) written["identity:proposals-without-candidates"] = written.GetValueOrDefault("identity:proposals-without-candidates") + 1;
                decision = decision with
                {
                    Status = kept.Length > 0 ? IdentityDecisionKind.Pending : IdentityDecisionKind.New, PendingEntityIds = kept,
                    Diagnostics = [.. decision.Diagnostics, "identity_import_candidates_not_in_store"],
                };
            }
            var standsAlone = node.Label == "IdentityDecision" && decision.Status == IdentityDecisionKind.Confirmed &&
                decision.ConfirmedEntityId is { } target && missing.Contains(target);
            if (standsAlone)
            {
                // REV3-3: the confirmed identity (a shared entity) stayed behind. A confirmation without its target cannot be
                // reviewed, so the copy holds what a revocation leaves: the observation as its own entity (made below),
                // with the decision and its bindings resolved to it, and a diagnostic saying why.
                written["identity:confirmations-without-target"] = written.GetValueOrDefault("identity:confirmations-without-target") + 1;
                decision = decision with
                {
                    Status = IdentityDecisionKind.New, ConfirmedEntityId = null, PendingEntityIds = [], ContextualEvidence = null,
                    Diagnostics = [.. decision.Diagnostics, "identity_import_target_not_in_store"],
                };
            }
            var owned = decision with
            {
                DecisionId = map[decisionNode], ObservationKey = key,
                Observation = decision.Observation with { OwnerId = ownerId, EntityId = observationId, Embedding = vectors.GetValueOrDefault(node.Key) },
            };
            if (standsAlone) standalone[node.Key] = owned;
            return IdentityDecisionPayload.Encode(owned);
        }
        var factsById = export.Facts.ToDictionary(fact => fact.FactId, StringComparer.Ordinal);
        var decisionRoots = state.Nodes.Where(node => node.Label == "IdentityDecision").ToDictionary(node => node.Key, node =>
        {
            var decision = IdentityDecisionPayload.Decode(node.Properties["identity_record_json"].Text!);
            return decision.Context.SourceMessageIds.Concat(decision.Observation.SourceMessageIds)
                .Concat(decision.Context.Sources.SelectMany(source => source.OriginIds.Append(source.SourceId))).ToHashSet(StringComparer.Ordinal);
        }, StringComparer.Ordinal);
        var invalid = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in state.Nodes.Where(node => node.Label == "IdentityFactBinding"))
        {
            var consistent = linkTo.TryGetValue(("HAS_IDENTITY_BINDING", node.Key), out var boundFact) && factsById.TryGetValue(boundFact.From, out var fact) &&
                node.Properties.TryGetValue("identity_decision_id", out var decided) && decided.Text is { } decisionKey &&
                decisionRoots.TryGetValue(decisionKey, out var roots) &&
                node.Properties.TryGetValue("identity_source_ids", out var sources) && (sources.Items ?? []).Any(item => item.Text is { } id && roots.Contains(id)) &&
                node.Properties.TryGetValue("identity_observed_surface", out var surface) && surface.Text is { } observed &&
                node.Properties.TryGetValue("identity_fact_role", out var role) &&
                AgentMemory.Core.Memory.MemoryTripleCanonicalizer.CanonicalValue(role.Text == "Object" ? fact.Object : fact.Subject) ==
                    AgentMemory.Core.Memory.MemoryTripleCanonicalizer.CanonicalValue(observed);
            if (!consistent) invalid.Add(node.Key);
        }
        invalid.UnionWith(duplicateBindings);
        if (invalid.Count > 0) written["identity:bindings-dropped"] = invalid.Count;
        var dropped = 0;
        Dictionary<string, object?> Record(OwnerGraphNode node)
        {
            var declared = IdentityProperties[node.Label];
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (name, value) in node.Properties)
                if (declared.Contains(name)) values[name] = OwnerIdentityPortability.FromValue(remap.Value(name, value));
                else dropped++;
            if (node.Label is "IdentityDecision" or "IdentityDecisionRevision" or "IdentityRelationshipObservation" &&
                values.GetValueOrDefault("identity_record_json") is string record)
                values["identity_record_json"] = Owned(node, record);
            if (standalone.ContainsKey(node.Key)) values.Remove("identity_target_snapshot");
            values["id"] = map[node.Key];
            values["owner_id"] = ownerId;
            values["owner_scope"] = "owned:" + ownerId;
            return values;
        }

        foreach (var (label, queries) in OwnerDataQueries.IdentityRecords)
        {
            var rows = state.Nodes.Where(node => node.Label == label && !invalid.Contains(node.Key)).Select(Record).ToList();
            created.AddRange(rows.Select(row => (label, (string)row["id"]!, (string?)null)));
            await WriteRowsAsync(queries.Import, "identity:" + label, [.. rows.Cast<object>()]).ConfigureAwait(false);
        }
        if (dropped > 0) written["identity:properties-dropped"] = dropped;
        await WriteRowsAsync(OwnerDataQueries.ImportEntityIdentity, "identity:entity-properties",
            state.EntityProperties.Where(node => map.ContainsKey(node.Key))
                .Select(node => (object)new Dictionary<string, object?>
                {
                    ["key"] = map[node.Key],
                    ["props"] = node.Properties.ToDictionary(pair => pair.Key, pair => OwnerIdentityPortability.FromValue(remap.Value(pair.Key, pair.Value)), StringComparer.Ordinal),
                }).ToList()).ConfigureAwait(false);
        // REV3-3: each confirmation whose target stayed behind stands on its observation, made as a revocation makes it
        // (unless the file brought it), and the decision and its bindings resolve to it.
        var ownTarget = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (decisionNode, decision) in standalone)
        {
            ownTarget[map[decisionNode]] = decision.Observation.EntityId;
            if (importedEntities.Contains(decision.Observation.EntityId)) continue;
            created.Add(("Entity", decision.Observation.EntityId, null));
            await tx.WriteAsync(runner => Neo4jIdentityDecisionRepository.MaterializeObservation(runner, decision.Observation, decision.ObservationKey,
                includeShared: false, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        foreach (var node in state.Nodes.Where(node => node.Label == "IdentityFactBinding" && !invalid.Contains(node.Key)))
            if (node.Properties.TryGetValue("identity_decision_id", out var decided) && decided.Text is { } decisionNode &&
                standalone.TryGetValue(decisionNode, out var decision) && map.TryGetValue(node.Key, out var binding))
                ownTarget[binding] = decision.Observation.EntityId;
        foreach (var link in OwnerDataQueries.IdentityLinks)
        {
            var edges = state.Edges.Where(edge => edge.Type == link.Type && edge.FromLabel == link.FromLabel && edge.ToLabel == link.ToLabel &&
                    !invalid.Contains(edge.From) && !invalid.Contains(edge.To))
                .Select(edge => (From: remap.Id(edge.From), To: remap.Id(edge.To)))
                .Select(edge => link.Type == "RESOLVES_TO" && missing.Contains(edge.To) && ownTarget.TryGetValue(edge.From, out var own)
                    ? (edge.From, To: own) : edge).ToList();
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
