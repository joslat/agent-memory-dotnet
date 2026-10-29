using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentMemory.Abstractions.Options;
using AgentMemory.Extensibility.Context;
using AgentMemory.Extensibility.Context.Traversal;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Extensibility.Reading;

namespace AgentMemory.Extensibility.Ampack;

/// <summary>A record a pack read returned: what it is, its text as its type renders it, how the walk reached it, and its pack provenance.</summary>
[Experimental("AMEXT001")]
public sealed record PackHit(string Module, string Type, string Key, string Text, string? Via, PackSource? Pack);

/// <summary>Packs mounted together: every pack as loaded and verified, and the records held in memory.</summary>
[Experimental("AMEXT001")]
public sealed record PackMount(IReadOnlyList<LoadedPack> Packs, PackMemory Memory);

/// <summary>
/// Verified packs held in memory and read like a store (PACK-engine), with no database: a knowledge pack's records and a
/// snapshot's, read through the same contracts as the Neo4j store, <see cref="IModuleReader"/> per module and
/// <see cref="IModuleTraversalReader"/> for the walk. What <c>ampack test</c> runs a pack's own tests on, and what
/// <c>ampack serve</c> serves.
/// </summary>
/// <remarks>
/// <b>The store's rules, not a second set.</b> Reads are admitted by <see cref="ModuleQueryAdmission"/>; filters, orders,
/// key and link filters, fan-out, the neighbour cap and degrees follow the Neo4j reader's queries. Every pack record is shared
/// and live: a scope that excludes shared records reads nothing, and a read pinned to a clock is not applicable. A search
/// scores as the store's full-text index does, BM25 per search property summed over the properties (k1 1.2, b 0.75), then
/// orders by id; Lucene's lossy length norms can still order near-ties differently.
/// </remarks>
[Experimental("AMEXT001")]
public sealed partial class PackMemory : IModuleTraversalReader
{
    private const int MaximumFromIds = 256;
    private readonly Dictionary<string, ModuleRecord> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Module, string Type), List<ModuleRecord>> _byType = [];
    private readonly Dictionary<(string Relation, string TargetId), List<ModuleRecord>> _incoming = [];

    private PackMemory(ModuleResolution resolution) => Resolution = resolution;

    /// <summary>The packs' modules resolved together against the running SDK.</summary>
    public ModuleResolution Resolution { get; }

    /// <summary>How many records it holds.</summary>
    public int Count => _byId.Count;

    /// <summary>Every record held, by module, type and key: what a composer lists (PACK-composer).</summary>
    public IReadOnlyList<ModuleRecord> Records => [.. _byId.Values.OrderBy(r => r.ModuleId, StringComparer.Ordinal)
        .ThenBy(r => r.Type, StringComparer.Ordinal).ThenBy(r => r.Key, StringComparer.Ordinal)];

    /// <summary>
    /// Holds knowledge packs and snapshots in memory. A snapshot brings its module and its dependencies; a link into a record
    /// none of them holds is left out, as the store leaves it out.
    /// </summary>
    /// <exception cref="KnowledgePackException">The modules do not resolve together.</exception>
    public static PackMemory From(IEnumerable<KnowledgePack> knowledge, IEnumerable<ModuleSnapshot>? snapshots = null)
    {
        ArgumentNullException.ThrowIfNull(knowledge);
        var packs = knowledge.ToList();
        var shots = (snapshots ?? []).ToList();
        var manifests = new Dictionary<string, ModuleManifest>(StringComparer.Ordinal);
        foreach (var manifest in packs.Select(p => p.Manifest).Concat(shots.SelectMany(s => s.Dependencies.Prepend(s.Module))))
            manifests.TryAdd(manifest.Id, manifest);
        var resolution = ModuleResolver.Resolve([.. manifests.Values], ModuleSet.SdkVersion, ModuleHostFeatures.Known);
        if (!resolution.Succeeded) throw new KnowledgePackException(resolution.Errors);
        // PACK-consent: links into another module's records need its consent or the host's grant, as the store requires.
        resolution = resolution with { ConsentGrants = [.. shots.SelectMany(s => s.ConsentGrants).Distinct()] };
        var refused = shots.SelectMany(s => s.Records.SelectMany(r => r.Links.Select(l => s.Module.Ontology?.Relations.SingleOrDefault(x => x.Name == l.Relation && x.From == r.Type)))
                .OfType<ModuleRelation>().Distinct().Select(relation => resolution.ConsentRefusal(s.Module, relation)))
            .OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        if (refused.Count != 0) throw new KnowledgePackException(refused);

        var memory = new PackMemory(resolution);
        var loadedAt = DateTimeOffset.UtcNow;
        var sources = packs.Select(p => (p.Manifest, (IEnumerable<ModuleProposal>)p.Proposals))
            .Concat(shots.Select(s => (s.Module, s.Stamped()))).ToList();
        foreach (var (manifest, records) in sources)
            foreach (var record in records)
            {
                var stored = new ModuleRecord
                {
                    ModuleId = manifest.Id, Type = record.Type, Id = IdOf(manifest.Id, record.Type, record.Key), Key = record.Key,
                    CreatedAtUtc = loadedAt, Properties = record.Properties, SourceSessionId = "pack", Source = record.Source,
                };
                memory._byId[stored.Id] = stored;
            }
        // Links once every record is in: a link resolves to the record its target key names, in this module or a dependency.
        foreach (var (manifest, records) in sources)
            foreach (var record in records)
            {
                var links = new List<ModuleRecordLink>();
                foreach (var link in record.Links)
                {
                    var relation = manifest.Ontology!.Relations.SingleOrDefault(r => r.Name == link.Relation && r.From == record.Type);
                    if (relation is null || Target(manifest.Id, relation) is not { } target) continue;
                    var targetId = IdOf(target.Module, target.Type, link.Target);
                    if (memory._byId.ContainsKey(targetId))
                        links.Add(new ModuleRecordLink { Relation = relation.Name, TargetId = targetId, TargetType = target.Type });
                }
                var id = IdOf(manifest.Id, record.Type, record.Key);
                var linked = memory._byId[id] with
                {
                    Links = links.DistinctBy(l => (l.Relation, l.TargetId)).OrderBy(l => l.Relation, StringComparer.Ordinal)
                        .ThenBy(l => l.TargetId, StringComparer.Ordinal).ToList().AsReadOnly(),
                };
                memory._byId[id] = linked;
            }
        foreach (var record in memory._byId.Values.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            var type = (record.ModuleId, record.Type);
            if (!memory._byType.TryGetValue(type, out var list)) memory._byType[type] = list = [];
            list.Add(record);
            foreach (var link in record.Links)
            {
                if (!memory._incoming.TryGetValue((link.Relation, link.TargetId), out var into)) memory._incoming[(link.Relation, link.TargetId)] = into = [];
                into.Add(record);
            }
        }
        return memory;
    }

    /// <summary>
    /// Loads and verifies <c>.ampack</c> archives as a host does (single packs and pack sets; a composite only inside its set),
    /// imports each snapshot into the definition beside it, and holds every knowledge pack and snapshot in memory.
    /// </summary>
    /// <exception cref="PackLoadException">A pack is refused, a composite comes alone, or a snapshot's definition is missing.</exception>
    public static PackMount Mount(IEnumerable<byte[]> archives, PackTrust trust) => Mount(archives, trust, null);

    // off: given the loaded packs, the ids a host's gate keeps off; they, and snapshots of a definition kept off, are not
    // mounted (review 1, M1: ampack serve honours the gate).
    internal static PackMount Mount(IEnumerable<byte[]> archives, PackTrust trust, Func<IReadOnlyList<LoadedPack>, IReadOnlyCollection<string>>? off)
    {
        ArgumentNullException.ThrowIfNull(archives);
        ArgumentNullException.ThrowIfNull(trust);
        var loaded = new List<LoadedPack>();
        foreach (var archive in archives)
            switch (PackLoader.Peek(archive).Kind)
            {
                case "set":
                    var set = PackFamilies.LoadSet(archive, trust);
                    loaded.AddRange(set.AllPacks);
                    break;
                case "composite":
                    throw new PackLoadException(["A composite pins its members but does not carry them: mount the pack set that carries both."]);
                default:
                    loaded.Add(PackLoader.Load(archive, trust));
                    break;
            }
        if (off?.Invoke(loaded) is { Count: > 0 } kept)
            loaded.RemoveAll(p => kept.Contains(p.Id) || (p.Kind == "snapshot" && p.Manifest.Modules.Keys.Any(kept.Contains)));
        var definitions = loaded.Where(p => p.Kind == "definition").ToDictionary(p => p.Id, p => p.Manifest, StringComparer.Ordinal);
        var snapshots = new List<ModuleSnapshot>();
        foreach (var shot in loaded.Where(p => p.Kind == "snapshot"))
        {
            var of = shot.Manifest.Modules.Keys.Single();
            if (!definitions.TryGetValue(of, out var definition))
                throw new PackLoadException([$"{shot.Id} is a snapshot of {of}: mount that definition pack beside it."]);
            snapshots.Add(shot.ToSnapshot(definition, [.. definition.Modules.Keys.Where(definitions.ContainsKey).Select(d => definitions[d])]));
        }
        try { return new PackMount(loaded, From(loaded.Select(p => p.Knowledge).OfType<KnowledgePack>(), snapshots)); }
        catch (KnowledgePackException e) { throw new PackLoadException(e.Problems); }
    }

    /// <summary>
    /// Asks one of a module's views: a search view with <paramref name="text"/>, any view with a <paramref name="key"/>.
    /// Read as a host reads, in the shared scope.
    /// </summary>
    /// <exception cref="PackLoadException">The module or view is unknown, or the view refuses the read.</exception>
    public async Task<IReadOnlyList<PackHit>> AskAsync(string moduleId, string viewId, string? text = null, string? key = null, int? limit = null,
        CancellationToken cancellationToken = default)
    {
        if (Resolution.Modules.All(m => m.Id != moduleId)) throw new PackLoadException([$"{moduleId} is not mounted."]);
        var result = await new ModuleViews(Resolution, ReaderFor(moduleId))
            .EvaluateWithArgumentsAsync(viewId, new() { Search = text, Key = key, Limit = limit }, Shared, cancellationToken).ConfigureAwait(false);
        if (result.Status != ModuleReadStatus.Succeeded && result.Status != ModuleReadStatus.NotApplicable)
            throw new PackLoadException([.. result.Errors.DefaultIfEmpty($"The view {viewId} refused the read.")]);
        return [.. result.Items.Select(i => Hit(i.Record, null))];
    }

    /// <summary>
    /// Walks from the work in hand, as traversal context does: records whose anchor properties match the anchors, then the
    /// declared relations, within the host's budgets, ranked by path and the question's words; each hit with its reason path.
    /// </summary>
    public async Task<IReadOnlyList<PackHit>> WalkAsync(IReadOnlyList<ContextAnchor> anchors, string? question = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(anchors);
        var walk = await TraversalWalk.RunAsync(Resolution, this,
            new ContextRequest { SessionId = "pack", Owner = "pack", Query = question ?? "", Anchors = anchors }, cancellationToken).ConfigureAwait(false);
        return [.. walk.Items.Select(i => Hit(i.Record, i.Via))];
    }

    private static readonly ModuleReadContext Shared = new() { Scope = MemoryScope.For("pack") };

    private PackHit Hit(ModuleRecord record, string? via)
    {
        var type = Resolution.Modules.Single(m => m.Id == record.ModuleId).Ontology!.Types.Single(t => t.Name == record.Type);
        return new PackHit(record.ModuleId, record.Type, record.Key, ModuleRenderTemplate.Render(type, record.Properties), via, record.Source.Pack);
    }

    /// <summary>A reader bound to one calling module, as a host grants it: its own types and its dependencies'.</summary>
    public IModuleReader ReaderFor(string callerModuleId)
    {
        if (!Resolution.Modules.Any(m => m.Id == callerModuleId)) throw new ArgumentException($"{callerModuleId} is not held here.", nameof(callerModuleId));
        return new Reader(this, callerModuleId);
    }

    private static string IdOf(string module, string type, string key) => module + ":" + type + ":" + key;

    // The module and type a relation points at: its own type, or a dependency's ("module:Type"); core entities are not held.
    private static (string Module, string Type)? Target(string module, ModuleRelation relation)
    {
        var parts = relation.To.Split(':');
        return parts.Length == 1 ? (module, relation.To) : parts[0] == "core" ? null : (parts[0], parts[1]);
    }

    // Pack records are shared: a scope that excludes shared records sees none of them.
    private static bool Sees(ModuleReadContext context) => context.Scope is not { HasOwnerFilter: true, IncludeShared: false };

    private IEnumerable<ModuleRecord> OfType(string module, string type) => _byType.GetValueOrDefault((module, type)) ?? [];

    // The search index of one view, built once: mounted packs never change, so it never goes stale (review 1, I3).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Module, string Type, string View), Bm25> _indexes = new();

    private Bm25 Index(string module, string type, string view, IReadOnlyList<string> properties) =>
        _indexes.GetOrAdd((module, type, view), _ => new Bm25(OfType(module, type), properties));

    private sealed class Reader(PackMemory memory, string caller) : IModuleReader
    {
        public string CallerModuleId => caller;

        public Task<ModuleReadResult> FindAsync(ModuleQuery query, ModuleReadContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query); ArgumentNullException.ThrowIfNull(context); cancellationToken.ThrowIfCancellationRequested();
            if (context.SystemAsOfUtc is not null || context.ValidAsOfUtc is not null)
                return Task.FromResult(new ModuleReadResult { Status = ModuleReadStatus.NotApplicable, Errors = ["A pack in memory holds its current records only."] });
            var admission = ModuleQueryAdmission.Admit(memory.Resolution, caller, query, context, DateTimeOffset.UtcNow);
            if (admission.Status != ModuleReadStatus.Succeeded) return Task.FromResult(new ModuleReadResult { Status = admission.Status, Errors = admission.Errors });
            if (!Sees(context)) return Task.FromResult(new ModuleReadResult { Status = ModuleReadStatus.Succeeded });
            var admitted = admission.Query!;
            var module = memory.Resolution.Modules.Single(m => m.Id == admitted.ModuleId);
            var type = module.Ontology!.Types.Single(t => t.Name == admitted.Type);
            var found = memory.OfType(module.Id, type.Name)
                .Where(r => admitted.Key is null || r.Key == admitted.Key)
                .Where(r => admitted.Where.All(f => Holds(r, f, type.Properties[f.Property].Type)))
                .Where(r => admitted.Links.All(l => r.Links.Any(x => x.Relation == l.Relation && x.TargetId == l.TargetId)));
            IEnumerable<ModuleRecord> ordered;
            if (admitted.Search is { } search)
            {
                var properties = module.Views.Single(v => v.Id == search.ViewId).Search!.Properties;
                var corpus = memory.Index(module.Id, type.Name, search.ViewId, properties);
                var scored = found.Select(r => (Record: r, Score: corpus.Score(r, search.Terms))).Where(p => p.Score > 0);
                ordered = scored.OrderByDescending(p => p.Score).ThenBy(p => p.Record.Id, StringComparer.Ordinal).Select(p => p.Record);
            }
            else if (admitted.OrderBy is { } order && admitted.Selection != ModuleRecordSelection.History)
            {
                var kind = type.Properties[order.Property].Type;
                var present = found.Where(r => Present(r, order.Property)).ToList();
                var sorted = order.Descending
                    ? present.OrderByDescending(r => r.Properties[order.Property], new ValueOrder(kind)).ThenBy(r => r.Id, StringComparer.Ordinal)
                    : present.OrderBy(r => r.Properties[order.Property], new ValueOrder(kind)).ThenBy(r => r.Id, StringComparer.Ordinal);
                ordered = sorted.Concat(found.Where(r => !Present(r, order.Property)).OrderBy(r => r.Id, StringComparer.Ordinal));
            }
            else ordered = found.OrderBy(r => r.Id, StringComparer.Ordinal);
            return Task.FromResult(new ModuleReadResult { Status = ModuleReadStatus.Succeeded, Records = ordered.Take(admitted.Limit).ToList().AsReadOnly() });
        }
    }

    /// <inheritdoc/>
    public Task<ModuleReadResult> MatchAsync(ModuleAnchorQuery query, ModuleReadContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query); ArgumentNullException.ThrowIfNull(context); cancellationToken.ThrowIfCancellationRequested();
        if (context.SystemAsOfUtc is not null || context.ValidAsOfUtc is not null)
            return Task.FromResult(new ModuleReadResult { Status = ModuleReadStatus.NotApplicable, Errors = ["Traversal reads live records only."] });
        var module = Resolution.Modules.SingleOrDefault(m => m.Id == query.ModuleId);
        if (module?.Traversal?.Anchors.Any(a => a.Type == query.Type && a.Property == query.Property && a.Match == query.Match) != true)
            return Task.FromResult(new ModuleReadResult { Status = ModuleReadStatus.Refused, Errors = ["The anchor property is not declared for traversal."] });
        var values = query.Values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim().Replace('\\', '/').ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        if (values.Count == 0 || !Sees(context)) return Task.FromResult(new ModuleReadResult { Status = ModuleReadStatus.Succeeded });
        var found = OfType(module.Id, query.Type).Where(r => Text(r, query.Property) is { } value
            && (query.Match != ModuleAnchorMatch.Exact || values.Contains(value.ToLowerInvariant())));
        return Task.FromResult(new ModuleReadResult
        {
            Status = ModuleReadStatus.Succeeded, Records = found.Take(Math.Clamp(query.Limit, 1, ModuleAnchorQuery.MaximumLimit)).ToList().AsReadOnly(),
        });
    }

    /// <inheritdoc/>
    public Task<ModuleTraversalStep> ExpandAsync(ModuleExpandQuery query, ModuleReadContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query); ArgumentNullException.ThrowIfNull(context); cancellationToken.ThrowIfCancellationRequested();
        if (context.SystemAsOfUtc is not null || context.ValidAsOfUtc is not null)
            return Task.FromResult(new ModuleTraversalStep { Status = ModuleReadStatus.NotApplicable, Errors = ["Traversal reads live records only."] });
        var module = Resolution.Modules.SingleOrDefault(m => m.Id == query.ModuleId);
        var declared = module?.Traversal?.Relations.SingleOrDefault(r => r.Name == query.Relation && r.Direction == query.Direction);
        if (declared is null) return Refused("The link is not declared traversable in this direction.");
        if (query.FromIds.Count > MaximumFromIds) return Refused($"An expansion starts from at most {MaximumFromIds} records.");
        var relation = module!.Ontology!.Relations.Single(r => r.Name == query.Relation);
        if (Target(module.Id, relation) is not { } target) return Refused("Traversal does not walk into core entities.");
        if (!Sees(context)) return Task.FromResult(new ModuleTraversalStep { Status = ModuleReadStatus.Succeeded });
        var (reachedModule, reachedType) = query.Direction == ModuleTraversalDirection.Out ? target : (module.Id, relation.From);
        var text = Resolution.Modules.Single(m => m.Id == reachedModule).Ontology!.Types.Single(t => t.Name == reachedType).TextProperties();
        var words = query.Words.Take(ModuleExpandQuery.MaximumWords).Select(w => w.ToLowerInvariant()).ToList();
        var fanOut = Math.Clamp(query.FanOut, 1, Math.Min(declared.FanOut, ModuleTraversalRelation.MaximumFanOut));
        var edges = new List<ModuleTraversalEdge>();
        foreach (var from in query.FromIds.Distinct(StringComparer.Ordinal))
        {
            if (!_byId.TryGetValue(from, out var start)) continue;
            // At most the scan cap of neighbours is read per starting record, and a degree is counted up to it (red-team M3).
            IEnumerable<(ModuleRecord Record, int Degree)> reached = query.Direction == ModuleTraversalDirection.Out
                ? start.Links.Where(l => l.Relation == relation.Name).Select(l => _byId[l.TargetId]).Take(ModuleExpandQuery.MaximumScan)
                    .Select(r => (r, Math.Min(_incoming.GetValueOrDefault((relation.Name, r.Id))?.Count ?? 0, ModuleExpandQuery.MaximumScan)))
                : (_incoming.GetValueOrDefault((relation.Name, start.Id)) ?? []).Take(ModuleExpandQuery.MaximumScan)
                    .Select(r => (r, r.Links.Count(l => l.Relation == relation.Name)));
            var ranked = reached
                .Select(p => (p.Record, p.Degree, Hits: Hits(p.Record, text, words)))
                .OrderByDescending(p => p.Hits).ThenBy(p => p.Degree).ThenBy(p => p.Record.Id, StringComparer.Ordinal).Take(fanOut);
            edges.AddRange(ranked.Select(p => new ModuleTraversalEdge { FromId = from, To = p.Record with { Links = [] }, Degree = Math.Max(1, p.Degree) }));
        }
        return Task.FromResult(new ModuleTraversalStep { Status = ModuleReadStatus.Succeeded, Edges = edges.AsReadOnly() });
    }

    private static Task<ModuleTraversalStep> Refused(string error) =>
        Task.FromResult(new ModuleTraversalStep { Status = ModuleReadStatus.Refused, Errors = [error] });

    private static string? Text(ModuleRecord record, string property) =>
        record.Properties.TryGetValue(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Present(ModuleRecord record, string property) =>
        record.Properties.TryGetValue(property, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    // How many of the question's words the record's text holds, as the store's ranking counts them.
    private static int Hits(ModuleRecord record, IReadOnlyList<string> textProperties, IReadOnlyList<string> words)
    {
        if (words.Count == 0) return 0;
        var text = string.Join(' ', textProperties.Select(p => record.Properties.TryGetValue(p, out var v)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()) : "")).ToLowerInvariant();
        return words.Count(w => text.Contains(w, StringComparison.Ordinal));
    }

    // The words of one property, lower case, as a full-text index's standard analyzer splits them.
    private static List<string> Words(ModuleRecord record, string property) =>
        record.Properties.TryGetValue(property, out var v) && v.ValueKind == JsonValueKind.String
            ? [.. WordPattern().Matches(v.GetString()!.ToLowerInvariant()).Select(m => m.Value)] : [];

    // BM25 per property, summed over the search properties: each property is its own field with its own lengths, as in the
    // store's full-text index.
    private sealed class Bm25
    {
        private const double K1 = 1.2, B = 0.75;
        private readonly IReadOnlyList<string> _properties;
        private readonly Dictionary<string, (int Documents, double AverageLength, Dictionary<string, int> Holding)> _fields = new(StringComparer.Ordinal);

        public Bm25(IEnumerable<ModuleRecord> records, IReadOnlyList<string> properties)
        {
            _properties = properties;
            var all = records.ToList();
            foreach (var property in properties)
            {
                var documents = all.Select(r => Words(r, property)).Where(w => w.Count > 0).ToList();
                var holding = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var word in documents.SelectMany(d => d.Distinct(StringComparer.Ordinal))) holding[word] = holding.GetValueOrDefault(word) + 1;
                _fields[property] = (documents.Count, documents.Count == 0 ? 0 : documents.Average(d => d.Count), holding);
            }
        }

        public double Score(ModuleRecord record, IReadOnlyList<string> terms)
        {
            var score = 0.0;
            foreach (var property in _properties)
            {
                var words = Words(record, property);
                if (words.Count == 0) continue;
                var (documents, average, holding) = _fields[property];
                foreach (var term in terms)
                {
                    var frequency = words.Count(w => w == term);
                    if (frequency == 0) continue;
                    var df = holding.GetValueOrDefault(term);
                    var idf = Math.Log(1 + (documents - df + 0.5) / (df + 0.5));
                    score += idf * frequency * (K1 + 1) / (frequency + K1 * (1 - B + B * words.Count / average));
                }
            }
            return score;
        }
    }

    // The closed filter language of views: =, !=, <, <=, >, >= and in; null compares as absence.
    private static bool Holds(ModuleRecord record, ModuleViewFilter filter, string type)
    {
        var present = Present(record, filter.Property);
        if (filter.Value.ValueKind == JsonValueKind.Null) return filter.Operator == "=" ? !present : present;
        if (!present) return false;
        var value = record.Properties[filter.Property];
        var order = new ValueOrder(type);
        if (filter.Operator == "in") return filter.Value.ValueKind == JsonValueKind.Array && filter.Value.EnumerateArray().Any(v => order.Compare(value, v) == 0);
        var compared = order.Compare(value, filter.Value);
        return filter.Operator switch
        {
            "=" => compared == 0, "!=" => compared != 0, "<" => compared < 0, "<=" => compared <= 0, ">" => compared > 0, ">=" => compared >= 0,
            _ => false,
        };
    }

    // Values of one declared type in the store's order: numbers by value, dates and times as instants, text ordinally.
    private sealed class ValueOrder(string type) : IComparer<JsonElement>
    {
        public int Compare(JsonElement left, JsonElement right)
        {
            if (left.ValueKind == JsonValueKind.Number && right.ValueKind == JsonValueKind.Number) return left.GetDouble().CompareTo(right.GetDouble());
            if (left.ValueKind is JsonValueKind.True or JsonValueKind.False && right.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return left.GetBoolean().CompareTo(right.GetBoolean());
            var a = left.ValueKind == JsonValueKind.String ? left.GetString()! : left.GetRawText();
            var b = right.ValueKind == JsonValueKind.String ? right.GetString()! : right.GetRawText();
            if (type == "datetime" && DateTimeOffset.TryParse(a, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var x)
                && DateTimeOffset.TryParse(b, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var y)) return x.CompareTo(y);
            return string.CompareOrdinal(a, b);
        }
    }

    [GeneratedRegex("[a-z0-9_]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();
}
