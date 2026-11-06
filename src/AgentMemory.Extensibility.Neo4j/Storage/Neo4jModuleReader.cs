// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j.Migrations;
using AgentMemory.Extensibility.Neo4j.Queries;
using AgentMemory.Extensibility.Reading;
using AgentMemory.Neo4j.Infrastructure;
using Neo4j.Driver;
using static AgentMemory.Extensibility.Neo4j.Storage.ModuleRecordTransaction;

namespace AgentMemory.Extensibility.Neo4j.Storage;

/// <summary>Explicit module reader bound to a calling module and the host's real isolation policy.</summary>
/// <remarks>Metadata is not activation. The host grants this object to its authorized module; callers
/// cannot change its identity per query. Reads never apply DDL or mutate records. Required anchors
/// are checked before the requested result limit; missing anchors do not consume result slots.</remarks>
[Experimental("AMEXT001")]
public sealed class Neo4jModuleReader : IModuleReader
{
    private readonly INeo4jTransactionRunner _runner;
    private readonly ModuleResolution _resolution;
    private readonly IMemoryIsolationPolicy _isolation;
    private readonly TimeProvider _clock;
    private readonly ModuleMigrator _migrator;
    private readonly ModuleRecordLinks _links;
    private readonly PackMeaning? _meaning;
    /// <inheritdoc/>
    public string CallerModuleId { get; }

    /// <summary>Binds a reader without database work, schema application or automatic registration.</summary>
    public Neo4jModuleReader(INeo4jTransactionRunner runner, ModuleResolution resolution, string callerModuleId,
        IMemoryIsolationPolicy isolationPolicy, TimeProvider? timeProvider = null, IReadOnlyList<ModuleMigrationStep>? scripts = null,
        PackMeaning? meaning = null)
    {
        ArgumentNullException.ThrowIfNull(runner); ArgumentNullException.ThrowIfNull(resolution); ArgumentNullException.ThrowIfNull(isolationPolicy);
        if (!resolution.Succeeded || !resolution.Modules.Any(m => m.Id == callerModuleId))
            throw new ArgumentException("Reader requires a resolved calling module.", nameof(callerModuleId));
        _runner = runner; _resolution = resolution; _isolation = isolationPolicy; _clock = timeProvider ?? TimeProvider.System;
        CallerModuleId = callerModuleId; _migrator = new(runner, resolution, scripts ?? [], null); _links = new(resolution); _meaning = meaning;
    }

    /// <inheritdoc/>
    public async Task<ModuleReadResult> FindAsync(ModuleQuery query, ModuleReadContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query); ArgumentNullException.ThrowIfNull(context); cancellationToken.ThrowIfCancellationRequested();
        MemoryScope scope;
        try { scope = _isolation.ResolveReadScope(context.Scope, null, "module.find", MemoryOperationAccess.Tenant) with { }; }
        catch (MemoryOwnerScopeRequiredException) { return Refused("An owner scope is required by the host isolation policy."); }
        var frozenContext = context with { Scope = scope, SystemAsOfUtc = context.SystemAsOfUtc?.ToUniversalTime() };
        var admission = ModuleQueryAdmission.Admit(_resolution, CallerModuleId, query, frozenContext, _clock.GetUtcNow());
        if (admission.Status != ModuleReadStatus.Succeeded) return new() { Status = admission.Status, Errors = admission.Errors };
        var admitted = admission.Query!; var module = _resolution.Modules.Single(m => m.Id == admitted.ModuleId);
        var type = module.Ontology!.Types.Single(t => t.Name == admitted.Type);
        try
        {
            // By meaning too, where the host asked for it (feature 19): the question is embedded before the transaction. An
            // embedder that fails, or no index yet, leaves the words alone, as a host without meaning search reads.
            var near = await NearAsync(admitted, cancellationToken).ConfigureAwait(false);
            // With a judge, its candidates are read first and judged after the transaction (no model call holds a transaction).
            var judged = near is not null && _meaning!.Judge is not null;
            var take = judged ? Math.Max(admitted.Limit, _meaning!.Options.Judged) : admitted.Limit;
            var read = await _runner.ReadAsync(async transaction =>
            {
                var readiness = await _migrator.PlanInAsync(transaction, cancellationToken).ConfigureAwait(false);
                if (readiness.Problems.Count != 0 || readiness.Pending.Count != 0 || readiness.PendingNamespaces.Count != 0 || readiness.PendingBookkeeping.Count != 0)
                    return Refused("Module migrations are pending or inconsistent; apply them explicitly before reads.");
                var records = new List<ModuleRecord>(); var skip = 0;
                var targetCache = new Dictionary<(string? Owner, string Relation, string Target), string?>();
                var parameters = new Dictionary<string, object?> { ["owner"] = scope.HasOwnerFilter ? scope.OwnerId : null,
                    ["shared"] = scope.IncludeShared, ["key"] = admitted.Key, ["asOf"] = frozenContext.SystemAsOfUtc?.ToString("O"), ["take"] = 128 };
                for (var i = 0; i < admitted.Where.Count; i++) parameters["f" + i] = Parameter(admitted.Where[i].Value);
                var statement = ModuleReadQueries.Records(admitted, type);
                if (admitted.Search is { } search)
                {
                    parameters["index"] = ModuleSchemaObjects.SearchIndexName(type.Name, search.ViewId);
                    parameters["search"] = string.Join(" ", search.Terms);
                    statement = ModuleReadQueries.Search(admitted, type);
                }
                List<IRecord>? fused = null;
                if (near is { } vector)
                {
                    parameters["skip"] = 0;
                    var words = await RowsAsync(transaction, statement, parameters, cancellationToken).ConfigureAwait(false);
                    parameters["vindex"] = PackMeaning.IndexName(vector.Length); parameters["k"] = _meaning!.Options.Candidates;
                    parameters["vector"] = vector; parameters["model"] = _meaning.Model; parameters["floor"] = _meaning.Options.MinimumScore;
                    var close = await RowsAsync(transaction, ModuleReadQueries.Meaning(admitted, type), parameters, cancellationToken).ConfigureAwait(false);
                    fused = !_meaning.Options.KeepWords ? close
                        : _meaning.Options.WordsFirst ? PackMeaning.Fill(words, close) : PackMeaning.Fuse(words, close);
                }
                while (records.Count < take)
                {
                    parameters["skip"] = skip;
                    var rows = fused ?? await RowsAsync(transaction, statement, parameters, cancellationToken).ConfigureAwait(false);
                    foreach (var row in rows)
                    {
                        var data = row["row"].As<Dictionary<string, object>>();
                        var links = await ReadLinksAsync(transaction, module, type, data, scope, frozenContext.SystemAsOfUtc, cancellationToken).ConfigureAwait(false);
                        if (links is null) continue;
                        if (!await MatchesLinksAsync(transaction, module, type, admitted.Links, links, Text(data, "owner_id"),
                            scope, frozenContext.SystemAsOfUtc, targetCache, cancellationToken).ConfigureAwait(false)) continue;
                        // Only a record live at the read's valid time, else now, before the limit (feature 07, R-HOST-058); a history
                        // view lists versions, whatever their life.
                        var record = Record(module.Id, type, data, links);
                        if (admitted.Selection != ModuleRecordSelection.History && !ModuleRecordLife.IsLive(record.Life, frozenContext.ValidAsOfUtc ?? _clock.GetUtcNow())) continue;
                        // Nor one below the host's confidence floor (feature 09, review H4), before the limit too.
                        if (!AgentMemory.Extensibility.Packs.ModuleConfidence.Clears(record.ConfidenceMicro, frozenContext.MinimumConfidenceMicro)) continue;
                        records.Add(record);
                        if (records.Count == take) break;
                    }
                    if (fused is not null || rows.Count < 128) break;
                    skip = checked(skip + rows.Count);
                }
                return new ModuleReadResult { Status = ModuleReadStatus.Succeeded, Records = records.AsReadOnly() };
            }, cancellationToken).ConfigureAwait(false);
            if (!judged || read.Status != ModuleReadStatus.Succeeded) return read;
            var search = admitted.Search!;
            var properties = module.Views.Single(v => v.Id == search.ViewId).Search!.Properties ?? [];
            return read with { Records = await _meaning!.JudgedAsync(search.Meaning ?? string.Join(" ", search.Terms), read.Records, properties,
                admitted.Limit, cancellationToken).ConfigureAwait(false) };
        }
        catch (ModuleRecordRefusal refusal) { return Refused(refusal.Message); }
    }

    private async Task<float[]?> NearAsync(ModuleQuery query, CancellationToken cancellationToken)
    {
        if (_meaning is null || query.Search is not { } search) return null;
        try
        {
            var vector = await _meaning.EmbedAsync(search.Meaning ?? string.Join(" ", search.Terms), cancellationToken).ConfigureAwait(false);
            return await _meaning.HasIndexAsync(_runner, vector.Length, cancellationToken).ConfigureAwait(false) ? vector : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException) { return null; }
    }

    private async Task<bool> MatchesLinksAsync(IAsyncQueryRunner runner, ModuleManifest module, ModuleType type,
        IReadOnlyList<ModuleLinkFilter> filters, IReadOnlyList<ModuleRecordLink> links, string? owner, MemoryScope scope,
        DateTimeOffset? asOfUtc, Dictionary<(string? Owner, string Relation, string Target), string?> cache, CancellationToken cancellationToken)
    {
        foreach (var filter in filters)
        {
            var key = (owner, filter.Relation, filter.TargetId);
            if (!cache.TryGetValue(key, out var target))
            {
                var relation = module.Ontology!.Relations.Single(r => r.From == type.Name && r.Name == filter.Relation);
                target = await ModuleTargetResolver.FollowAsync(runner, _links.TargetLabel(module, relation), filter.TargetId,
                    owner, !scope.HasOwnerFilter || scope.IncludeShared, asOfUtc, cancellationToken).ConfigureAwait(false);
                cache.Add(key, target);
            }
            if (target is null || !links.Any(l => l.Relation == filter.Relation && l.TargetId == target)) return false;
        }
        return true;
    }

    private async Task<IReadOnlyList<ModuleRecordLink>?> ReadLinksAsync(IAsyncQueryRunner runner, ModuleManifest module,
        ModuleType type, IReadOnlyDictionary<string, object> data, MemoryScope scope, DateTimeOffset? asOfUtc, CancellationToken cancellationToken)
    {
        var relations = module.Ontology!.Relations.Where(r => r.From == type.Name).ToArray();
        if (relations.Length == 0) return [];
        var outgoing = await RowsAsync(runner, ModuleRecordQueries.Links(type.Name),
            new { id = Text(data, "id"), relations = relations.Select(r => r.Name).ToArray() }, cancellationToken).ConfigureAwait(false);
        var links = new List<ModuleRecordLink>();
        foreach (var relation in relations)
        {
            var targetType = _links.TargetLabel(module, relation); var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var edge in outgoing.Where(e => e["relation"].As<string>() == relation.Name))
            {
                var target = await ModuleTargetResolver.FollowAsync(runner, targetType, edge["target"].As<string>(),
                    Text(data, "owner_id"), !scope.HasOwnerFilter || scope.IncludeShared, asOfUtc, cancellationToken).ConfigureAwait(false);
                if (target is not null) ids.Add(target);
            }
            if (relation.Required && ids.Count == 0 || relation.Max is { } max && ids.Count > max) return null;
            links.AddRange(ids.OrderBy(id => id, StringComparer.Ordinal).Select(id => new ModuleRecordLink { Relation = relation.Name, TargetId = id, TargetType = targetType }));
        }
        return links.OrderBy(l => l.Relation, StringComparer.Ordinal).ThenBy(l => l.TargetId, StringComparer.Ordinal).ToList().AsReadOnly();
    }

    // A record's life as stored (feature 07): null when it has none, as it was written.
    private static ModuleRecordLife? Life(IReadOnlyDictionary<string, object> row) =>
        (Timestamp(row, "valid_from"), Timestamp(row, "valid_to"), Timestamp(row, "expires_at"), Timestamp(row, "review_by")) is var (from, to, expires, review)
        && (from ?? to ?? expires ?? review) is null ? null : new() { ValidFromUtc = from, ValidToUtc = to, ExpiresAtUtc = expires, ReviewByUtc = review };

    internal static ModuleRecord Record(string moduleId, ModuleType type, IReadOnlyDictionary<string, object> row, IReadOnlyList<ModuleRecordLink> links) => new()
    {
        ModuleId = moduleId, Type = type.Name, Id = Text(row, "id") ?? throw new ModuleRecordRefusal("Stored record has no identity."),
        Key = Text(row, "key") ?? throw new ModuleRecordRefusal("Stored record has no logical key."), OwnerId = Text(row, "owner_id"),
        CreatedAtUtc = Timestamp(row, "created_at") ?? throw new ModuleRecordRefusal("Stored record has no creation time."),
        InvalidatedAtUtc = Timestamp(row, "invalidated_at"), Links = links, Life = Life(row),
        ConfidenceMicro = row.TryGetValue("confidence_micro", out var confidence) && confidence is not null ? (int)confidence.As<long>() : null,
        SourceSessionId = Text(row, "source_session_id") ?? throw new ModuleRecordRefusal("Stored record has no source session."),
        Source = new()
        {
            MessageIds = row.TryGetValue("source_message_ids", out var ids) ? ids.As<List<string>>().AsReadOnly() : [], Excerpt = Text(row, "source_excerpt") ?? "",
            Pack = Text(row, "source_pack") is { } pack ? new()
            {
                Id = pack, Version = Text(row, "source_pack_version") ?? "", Digest = Text(row, "source_pack_digest") ?? "",
                File = Text(row, "source_pack_file") ?? "", Line = row.TryGetValue("source_pack_line", out var line) ? (int)line.As<long>() : 0,
                Carrier = Text(row, "source_carrier") is { } carrier ? new()
                {
                    Id = carrier, Version = Text(row, "source_carrier_version") ?? "", Digest = Text(row, "source_carrier_digest") ?? "",
                    Signer = Text(row, "source_carrier_signer") ?? "",
                } : null,
                UsagePolicy = Text(row, "source_policy"),
            } : null
        },
        Properties = type.Properties.Keys.Where(row.ContainsKey).ToDictionary(key => key, key => JsonSerializer.SerializeToElement(Value(row[key])), StringComparer.Ordinal).AsReadOnly()
    };
    private static DateTimeOffset? Timestamp(IReadOnlyDictionary<string, object> row, string key) =>
        row.TryGetValue(key, out var value) && value is ZonedDateTime time ? time.ToDateTimeOffset().ToUniversalTime() : null;
    private static object Value(object value) => value switch
    {
        ZonedDateTime time => time.ToDateTimeOffset().ToUniversalTime(),
        LocalDate day => new DateOnly(day.Year, day.Month, day.Day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => value
    };
    private static object? Parameter(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null, JsonValueKind.String => value.GetString(), JsonValueKind.True => true, JsonValueKind.False => false,
        JsonValueKind.Number when value.TryGetInt64(out var number) => number, JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.Array => value.EnumerateArray().Select(Parameter).ToArray(), _ => throw new ModuleRecordRefusal("Unsupported filter value.")
    };
    private static ModuleReadResult Refused(string error) => new() { Status = ModuleReadStatus.Refused, Errors = [error] };
}
