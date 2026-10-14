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
    /// <inheritdoc/>
    public string CallerModuleId { get; }

    /// <summary>Binds a reader without database work, schema application or automatic registration.</summary>
    public Neo4jModuleReader(INeo4jTransactionRunner runner, ModuleResolution resolution, string callerModuleId,
        IMemoryIsolationPolicy isolationPolicy, TimeProvider? timeProvider = null, IReadOnlyList<ModuleMigrationStep>? scripts = null)
    {
        ArgumentNullException.ThrowIfNull(runner); ArgumentNullException.ThrowIfNull(resolution); ArgumentNullException.ThrowIfNull(isolationPolicy);
        if (!resolution.Succeeded || !resolution.Modules.Any(m => m.Id == callerModuleId))
            throw new ArgumentException("Reader requires a resolved calling module.", nameof(callerModuleId));
        _runner = runner; _resolution = resolution; _isolation = isolationPolicy; _clock = timeProvider ?? TimeProvider.System;
        CallerModuleId = callerModuleId; _migrator = new(runner, resolution, scripts ?? [], null); _links = new(resolution);
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
            return await _runner.ReadAsync(async transaction =>
            {
                var readiness = await _migrator.PlanInAsync(transaction, cancellationToken).ConfigureAwait(false);
                if (readiness.Problems.Count != 0 || readiness.Pending.Count != 0 || readiness.PendingNamespaces.Count != 0 || readiness.PendingBookkeeping.Count != 0)
                    return Refused("Module migrations are pending or inconsistent; apply them explicitly before reads.");
                var records = new List<ModuleRecord>(); var skip = 0;
                var parameters = new Dictionary<string, object?> { ["owner"] = scope.HasOwnerFilter ? scope.OwnerId : null,
                    ["shared"] = scope.IncludeShared, ["key"] = admitted.Key, ["asOf"] = frozenContext.SystemAsOfUtc?.ToString("O"), ["take"] = 128 };
                for (var i = 0; i < admitted.Where.Count; i++) parameters["f" + i] = Parameter(admitted.Where[i].Value);
                var statement = ModuleReadQueries.Records(admitted, type);
                while (records.Count < admitted.Limit)
                {
                    parameters["skip"] = skip;
                    var rows = await RowsAsync(transaction, statement, parameters, cancellationToken).ConfigureAwait(false);
                    foreach (var row in rows)
                    {
                        var data = row["row"].As<Dictionary<string, object>>();
                        var links = await ReadLinksAsync(transaction, module, type, data, scope, frozenContext.SystemAsOfUtc, cancellationToken).ConfigureAwait(false);
                        if (links is null) continue;
                        records.Add(Record(module.Id, type, data, links));
                        if (records.Count == admitted.Limit) break;
                    }
                    if (rows.Count < 128) break;
                    skip = checked(skip + rows.Count);
                }
                return new ModuleReadResult { Status = ModuleReadStatus.Succeeded, Records = records.AsReadOnly() };
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (ModuleRecordRefusal refusal) { return Refused(refusal.Message); }
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

    private static ModuleRecord Record(string moduleId, ModuleType type, IReadOnlyDictionary<string, object> row, IReadOnlyList<ModuleRecordLink> links) => new()
    {
        ModuleId = moduleId, Type = type.Name, Id = Text(row, "id") ?? throw new ModuleRecordRefusal("Stored record has no identity."),
        Key = Text(row, "key") ?? throw new ModuleRecordRefusal("Stored record has no logical key."), OwnerId = Text(row, "owner_id"),
        CreatedAtUtc = Timestamp(row, "created_at") ?? throw new ModuleRecordRefusal("Stored record has no creation time."),
        InvalidatedAtUtc = Timestamp(row, "invalidated_at"), Links = links,
        SourceSessionId = Text(row, "source_session_id") ?? throw new ModuleRecordRefusal("Stored record has no source session."),
        Source = new() { MessageIds = row.TryGetValue("source_message_ids", out var ids) ? ids.As<List<string>>().AsReadOnly() : [], Excerpt = Text(row, "source_excerpt") ?? "" },
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
