using System.Diagnostics.CodeAnalysis;
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

/// <summary>
/// The traversal reader for the host's Neo4j database (coding packs CMP-1). Host-level, but it reads only what manifests declare:
/// an anchor query must name a declared anchor property, an expansion a declared traversable link. Live records only, in the
/// scope the host's isolation policy resolves; reads never apply DDL and refuse while migrations are pending.
/// </summary>
[Experimental("AMEXT001")]
public sealed class Neo4jModuleTraversalReader : IModuleTraversalReader
{
    /// <summary>Most starting records one expansion accepts.</summary>
    public const int MaximumFromIds = 256;

    private readonly INeo4jTransactionRunner _runner;
    private readonly ModuleResolution _resolution;
    private readonly IMemoryIsolationPolicy _isolation;
    private readonly ModuleMigrator _migrator;
    private readonly ModuleRecordLinks _links;

    /// <summary>Binds a reader without database work.</summary>
    public Neo4jModuleTraversalReader(INeo4jTransactionRunner runner, ModuleResolution resolution, IMemoryIsolationPolicy isolationPolicy,
        IReadOnlyList<ModuleMigrationStep>? scripts = null)
    {
        ArgumentNullException.ThrowIfNull(runner); ArgumentNullException.ThrowIfNull(resolution); ArgumentNullException.ThrowIfNull(isolationPolicy);
        if (!resolution.Succeeded) throw new ArgumentException("A traversal reader requires a resolved module set.", nameof(resolution));
        _runner = runner; _resolution = resolution; _isolation = isolationPolicy;
        _migrator = new(runner, resolution, scripts ?? [], null); _links = new(resolution);
    }

    /// <inheritdoc/>
    public async Task<ModuleReadResult> MatchAsync(ModuleAnchorQuery query, ModuleReadContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query); ArgumentNullException.ThrowIfNull(context); cancellationToken.ThrowIfCancellationRequested();
        if (context.SystemAsOfUtc is not null || context.ValidAsOfUtc is not null) return NotApplicable();
        var module = _resolution.Modules.SingleOrDefault(m => m.Id == query.ModuleId);
        var declared = module?.Traversal?.Anchors.Any(a => a.Type == query.Type && a.Property == query.Property && a.Match == query.Match) == true;
        if (!declared) return Refused("The anchor property is not declared for traversal.");
        if (!TryScope(context, out var scope)) return Refused("An owner scope is required by the host isolation policy.");
        var type = module!.Ontology!.Types.Single(t => t.Name == query.Type);
        var values = query.Values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim().Replace('\\', '/').ToLowerInvariant()).Distinct().ToArray();
        if (values.Length == 0) return new() { Status = ModuleReadStatus.Succeeded };
        var statement = query.Match == ModuleAnchorMatch.Exact ? ModuleTraversalQueries.MatchExact(type.Name, query.Property) : ModuleTraversalQueries.MatchAny(type.Name, query.Property);
        try
        {
            return await _runner.ReadAsync(async transaction =>
            {
                if (await NotReadyAsync(transaction, cancellationToken).ConfigureAwait(false)) return PendingMigrations();
                var rows = await RowsAsync(transaction, statement, new Dictionary<string, object?>
                {
                    ["owner"] = scope.HasOwnerFilter ? scope.OwnerId : null, ["shared"] = scope.IncludeShared, ["values"] = values,
                    ["take"] = Math.Clamp(query.Limit, 1, ModuleAnchorQuery.MaximumLimit)
                }, cancellationToken).ConfigureAwait(false);
                return new ModuleReadResult
                {
                    Status = ModuleReadStatus.Succeeded,
                    Records = rows.Select(r => Neo4jModuleReader.Record(module.Id, type, r["row"].As<Dictionary<string, object>>(), [])).ToList().AsReadOnly()
                };
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (ModuleRecordRefusal refusal) { return Refused(refusal.Message); }
    }

    /// <inheritdoc/>
    public async Task<ModuleTraversalStep> ExpandAsync(ModuleExpandQuery query, ModuleReadContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query); ArgumentNullException.ThrowIfNull(context); cancellationToken.ThrowIfCancellationRequested();
        if (context.SystemAsOfUtc is not null || context.ValidAsOfUtc is not null)
            return new() { Status = ModuleReadStatus.NotApplicable, Errors = ["Traversal reads live records only."] };
        var module = _resolution.Modules.SingleOrDefault(m => m.Id == query.ModuleId);
        var declared = module?.Traversal?.Relations.SingleOrDefault(r => r.Name == query.Relation && r.Direction == query.Direction);
        if (declared is null) return StepRefused("The link is not declared traversable in this direction.");
        if (query.FromIds.Count > MaximumFromIds) return StepRefused($"An expansion starts from at most {MaximumFromIds} records.");
        if (!TryScope(context, out var scope)) return StepRefused("An owner scope is required by the host isolation policy.");
        var relation = module!.Ontology!.Relations.Single(r => r.Name == query.Relation);
        string targetLabel;
        try { targetLabel = _links.TargetLabel(module, relation); }
        catch (ModuleRecordRefusal refusal) { return StepRefused(refusal.Message); }
        if (targetLabel == "Entity") return StepRefused("Traversal does not walk into core entities.");
        (string Module, ModuleType Type) reached = query.Direction == ModuleTraversalDirection.Out
            ? Type(module, relation.To) : (module.Id, module.Ontology.Types.Single(t => t.Name == relation.From));
        var statement = query.Direction == ModuleTraversalDirection.Out
            ? ModuleTraversalQueries.Out(relation.From, relation.Name, targetLabel)
            : ModuleTraversalQueries.In(relation.From, relation.Name, targetLabel);
        try
        {
            return await _runner.ReadAsync(async transaction =>
            {
                if (await NotReadyAsync(transaction, cancellationToken).ConfigureAwait(false))
                    return StepRefused("Module migrations are pending or inconsistent; apply them explicitly before reads.");
                var rows = await RowsAsync(transaction, statement, new Dictionary<string, object?>
                {
                    ["owner"] = scope.HasOwnerFilter ? scope.OwnerId : null, ["shared"] = scope.IncludeShared,
                    ["ids"] = query.FromIds.Distinct(StringComparer.Ordinal).ToArray(),
                    ["fanOut"] = Math.Clamp(query.FanOut, 1, Math.Min(declared.FanOut, ModuleTraversalRelation.MaximumFanOut))
                }, cancellationToken).ConfigureAwait(false);
                return new ModuleTraversalStep
                {
                    Status = ModuleReadStatus.Succeeded,
                    Edges = rows.Select(r => new ModuleTraversalEdge
                    {
                        FromId = r["fid"].As<string>(),
                        To = Neo4jModuleReader.Record(reached.Module, reached.Type, r["row"].As<Dictionary<string, object>>(), [])
                    }).ToList().AsReadOnly()
                };
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (ModuleRecordRefusal refusal) { return StepRefused(refusal.Message); }
    }

    private (string Module, ModuleType Type) Type(ModuleManifest module, string target)
    {
        var parts = target.Split(':');
        var owner = parts.Length == 2 ? _resolution.Modules.Single(m => m.Id == parts[0]) : module;
        var name = parts.Length == 2 ? parts[1] : target;
        return (owner.Id, owner.Ontology!.Types.Single(t => t.Name == name));
    }

    private bool TryScope(ModuleReadContext context, out MemoryScope scope)
    {
        try { scope = _isolation.ResolveReadScope(context.Scope, null, "module.traverse", MemoryOperationAccess.Tenant) with { }; return true; }
        catch (MemoryOwnerScopeRequiredException) { scope = null!; return false; }
    }

    private async Task<bool> NotReadyAsync(IAsyncQueryRunner transaction, CancellationToken cancellationToken)
    {
        var readiness = await _migrator.PlanInAsync(transaction, cancellationToken).ConfigureAwait(false);
        return readiness.Problems.Count != 0 || readiness.Pending.Count != 0 || readiness.PendingNamespaces.Count != 0 || readiness.PendingBookkeeping.Count != 0;
    }

    private static ModuleReadResult Refused(string error) => new() { Status = ModuleReadStatus.Refused, Errors = [error] };
    private static ModuleReadResult NotApplicable() => new() { Status = ModuleReadStatus.NotApplicable, Errors = ["Traversal reads live records only."] };
    private static ModuleReadResult PendingMigrations() => Refused("Module migrations are pending or inconsistent; apply them explicitly before reads.");
    private static ModuleTraversalStep StepRefused(string error) => new() { Status = ModuleReadStatus.Refused, Errors = [error] };
}
