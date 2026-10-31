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
        var anchors = query.Values.Where(v => !ModuleAnchorQuery.IsBlank(v)).ToList();
        if (anchors.Count == 0) return new() { Status = ModuleReadStatus.Succeeded };
        // The anchors as the walk compares them, and what the candidates must hold (review 2): the walk then matches exactly.
        // Not lowered here: the query lowers both sides with one folding (review 3).
        var values = anchors.Select(ModuleAnchorQuery.NormalizedPath).Where(v => v.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        var folders = values.Select(FirstSegment).Distinct(StringComparer.Ordinal).ToArray();
        var names = anchors.Select(a => ModuleAnchorQuery.SimpleName(a) is { Length: > 0 } simple ? simple : a.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        var statement = ModuleTraversalQueries.MatchCandidates(type.Name, query.Property, query.Match);
        try
        {
            return await ReadAsync(async transaction =>
            {
                if (await NotReadyAsync(transaction, cancellationToken).ConfigureAwait(false)) return PendingMigrations();
                // Every candidate matched as it arrives by the walk's rules, each one's match bounded in work (review 7, W1), and only
                // the best kept as it goes: best first, then key and id ordinal, as every store returns them (review 6, M2). Every
                // candidate is scanned, as the in-memory store scans every record, and the budget stops the scan where it is, on the
                // server and on every row (review 7, W5: past 250,000 rows it read an arbitrary subset; W6: it held every row).
                var matcher = (query with { Values = anchors }).Matcher();
                var limit = Math.Clamp(query.Limit, 1, ModuleAnchorQuery.MaximumLimit);
                var kept = new List<(string Id, string Key, double Quality)>();
                void Trim()
                {
                    kept.Sort(static (a, b) => b.Quality.CompareTo(a.Quality) is var q and not 0 ? q
                        : ModuleAnchorQuery.Order(a.Key, b.Key) is var k and not 0 ? k : ModuleAnchorQuery.Order(a.Id, b.Id));
                    if (kept.Count > limit) kept.RemoveRange(limit, kept.Count - limit);
                }
                await EachAsync(transaction, statement, new Dictionary<string, object?>
                {
                    ["owner"] = scope.HasOwnerFilter ? scope.OwnerId : null, ["shared"] = scope.IncludeShared, ["values"] = values,
                    ["folders"] = folders, ["names"] = names,
                }, row =>
                {
                    if (row["value"].As<object?>() is not string value) return;
                    var quality = matcher(value);
                    if (quality <= 0) return;
                    kept.Add((row["id"].As<string>(), row["key"].As<string>(), quality));
                    if (kept.Count >= 2 * limit + 1024) Trim();
                }, cancellationToken).ConfigureAwait(false);
                Trim();
                if (kept.Count == 0) return new ModuleReadResult { Status = ModuleReadStatus.Succeeded };
                var rows = await RowsAsync(transaction, ModuleTraversalQueries.ByIds(type.Name), new Dictionary<string, object?> { ["ids"] = kept.Select(k => k.Id).ToArray() },
                    cancellationToken).ConfigureAwait(false);
                var byId = rows.Select(r => Neo4jModuleReader.Record(module.Id, type, r["row"].As<Dictionary<string, object>>(), []))
                    .ToDictionary(r => r.Id, StringComparer.Ordinal);
                return new ModuleReadResult
                {
                    Status = ModuleReadStatus.Succeeded,
                    Records = kept.Where(k => byId.ContainsKey(k.Id)).Select(k => byId[k.Id]).ToList().AsReadOnly()
                };
            }, query.Budget, cancellationToken).ConfigureAwait(false);
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
            return await ReadAsync(async transaction =>
            {
                if (await NotReadyAsync(transaction, cancellationToken).ConfigureAwait(false))
                    return StepRefused("Module migrations are pending or inconsistent; apply them explicitly before reads.");
                // Past the link limit a record is not expanded, and the walk is told (review 2); the check is in the same statement
                // as the expansion (review 4, performance).
                var ids = query.FromIds.Distinct(StringComparer.Ordinal).ToArray();
                var fanOut = Math.Clamp(query.FanOut, 1, Math.Min(declared.FanOut, ModuleTraversalRelation.MaximumFanOut));
                // Narrow rows streamed into the best fanOut per starting record, in the format's order (review 8, W2: every row came
                // back whole and was held before the cut); only the records kept are then read whole, as the match read does.
                var past = new Dictionary<string, string>(StringComparer.Ordinal);
                var best = new Dictionary<string, List<(long Hits, int Degree, string Key, string Id)>>(StringComparer.Ordinal);
                static int Order((long Hits, int Degree, string Key, string Id) a, (long Hits, int Degree, string Key, string Id) b) =>
                    b.Hits.CompareTo(a.Hits) is var h and not 0 ? h : a.Degree.CompareTo(b.Degree) is var d and not 0 ? d
                    : ModuleAnchorQuery.Order(a.Key, b.Key) is var k and not 0 ? k : ModuleAnchorQuery.Order(a.Id, b.Id);
                void Trim(List<(long, int, string, string)> list)
                {
                    list.Sort(Order);
                    if (list.Count > fanOut) list.RemoveRange(fanOut, list.Count - fanOut);
                }
                await EachAsync(transaction, statement, new Dictionary<string, object?>
                {
                    ["owner"] = scope.HasOwnerFilter ? scope.OwnerId : null, ["shared"] = scope.IncludeShared, ["ids"] = ids,
                    ["maxLinks"] = ModuleExpandQuery.MaximumLinks, ["over"] = ModuleExpandQuery.MaximumLinks + 1,
                    ["fanOut"] = Math.Clamp(query.FanOut, 1, Math.Min(declared.FanOut, ModuleTraversalRelation.MaximumFanOut)),
                    ["words"] = Words(query.Words), ["text"] = reached.Type.TextProperties().ToArray(), ["fold"] = Fold,
                    ["scan"] = ModuleExpandQuery.MaximumScan, ["wide"] = ModuleTraversalQueries.WideKey
                }, row =>
                {
                    var from = row["fid"].As<string>();
                    if (row["links"].As<long>() > ModuleExpandQuery.MaximumLinks) { past.TryAdd(from, row["key"].As<string>()); return; }
                    if (row["id"].As<object?>() is not string id) return;
                    if (!best.TryGetValue(from, out var list)) best[from] = list = [];
                    list.Add((row["hits"].As<long>(), (int)Math.Clamp(row["degree"].As<long>(), 1, int.MaxValue), row["rkey"].As<string>(), id));
                    if (list.Count >= 2 * fanOut + 64) Trim(list);
                }, cancellationToken).ConfigureAwait(false);
                foreach (var list in best.Values) Trim(list);
                var keep = best.Values.SelectMany(l => l.Select(e => e.Id)).Distinct(StringComparer.Ordinal).ToArray();
                var label = query.Direction == ModuleTraversalDirection.Out ? targetLabel : relation.From;
                var byId = (await RowsAsync(transaction, ModuleTraversalQueries.ByIds(label), new Dictionary<string, object?> { ["ids"] = keep },
                        cancellationToken).ConfigureAwait(false))
                    .Select(r => Neo4jModuleReader.Record(reached.Module, reached.Type, r["row"].As<Dictionary<string, object>>(), []))
                    .ToDictionary(r => r.Id, StringComparer.Ordinal);
                // In the order the starting records were given, each one's neighbours in the format's order.
                var edges = ids.Where(best.ContainsKey).SelectMany(from => best[from].Where(e => byId.ContainsKey(e.Id))
                    .Select(e => new ModuleTraversalEdge { FromId = from, To = byId[e.Id], Degree = e.Degree })).ToList();
                return new ModuleTraversalStep
                {
                    Status = ModuleReadStatus.Succeeded,
                    Edges = edges.AsReadOnly(),
                    Errors = [.. past.Values.Select(key => $"{key} has more than {ModuleExpandQuery.MaximumLinks} {relation.Name} links: not expanded.")],
                };
            }, query.Budget, cancellationToken).ConfigureAwait(false);
        }
        catch (ModuleRecordRefusal refusal) { return StepRefused(refusal.Message); }
    }

    // A read under the walk's deadline where it gives one, stopped on the server (review 7, W3); a stop there is a stop by time.
    private async Task<T> ReadAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, TimeSpan? budget, CancellationToken cancellationToken)
    {
        try
        {
            return budget is { } left
                ? await _runner.ReadAsync(work, left, cancellationToken).ConfigureAwait(false)
                : await _runner.ReadAsync(work, cancellationToken).ConfigureAwait(false);
        }
        catch (Neo4jException e) when (budget is not null && e.Code?.Contains("TransactionTimedOut", StringComparison.Ordinal) == true)
        {
            throw new OperationCanceledException("The walk's time budget ended in the store.", e, cancellationToken);
        }
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

    // A reader lives for one request (one per scope), so a passed check holds for the rest of its walk: one check per request,
    // not one per read. A failed check is asked again.
    private async Task<bool> NotReadyAsync(IAsyncQueryRunner transaction, CancellationToken cancellationToken)
    {
        if (_ready) return false;
        var readiness = await _migrator.PlanInAsync(transaction, cancellationToken).ConfigureAwait(false);
        var notReady = readiness.Problems.Count != 0 || readiness.Pending.Count != 0 || readiness.PendingNamespaces.Count != 0 || readiness.PendingBookkeeping.Count != 0;
        _ready = !notReady;
        return notReady;
    }

    private volatile bool _ready;

    // The question's words as the query matches them: lower case, at most MaximumWordLength characters (as the walk's words
    // are, so both stores and the walk count the same), at most ModuleExpandQuery.MaximumWords.
    private static string[] Words(IReadOnlyList<string> words) =>
        [.. words.Where(w => !ModuleAnchorQuery.IsBlank(w)).Select(ModuleExpandQuery.Word).Where(w => w.Length <= ModuleExpandQuery.MaximumWordLength)
            .Distinct(StringComparer.Ordinal).Take(ModuleExpandQuery.MaximumWords)];

    // ASCII folding for the ranking's text, as the walk folds (review 3): A-Z to a-z, nothing else.
    private static readonly string[][] Fold = [.. Enumerable.Range('A', 26).Select(c => new[] { ((char)c).ToString(), ((char)(c + 32)).ToString() })];

    // What a stored value must hold to match an anchor by prefix or glob: its first segment (src/a/b.cs -> src). Every folder of
    // the anchor starts with it, so a value holding a folder holds it too: the same superset with one string an anchor (review
    // 7, W3: every folder of every anchor, up to 8,192 strings, each compared with every record on the server).
    private static string FirstSegment(string path) => path.IndexOf('/') is var slash and > 0 ? path[..slash] : path.TrimEnd('/');

    private static ModuleReadResult Refused(string error) => new() { Status = ModuleReadStatus.Refused, Errors = [error] };
    private static ModuleReadResult NotApplicable() => new() { Status = ModuleReadStatus.NotApplicable, Errors = ["Traversal reads live records only."] };
    private static ModuleReadResult PendingMigrations() => Refused("Module migrations are pending or inconsistent; apply them explicitly before reads.");
    private static ModuleTraversalStep StepRefused(string error) => new() { Status = ModuleReadStatus.Refused, Errors = [error] };
}
