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
/// key and link filters, fan-out, the link limit and degrees follow the Neo4j reader's queries. Every pack record is shared
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
    /// <param name="knowledge">The knowledge packs.</param>
    /// <param name="snapshots">The snapshots, each with its definition and dependencies.</param>
    /// <param name="grants">The host's consent grants (PACK-consent), beside the snapshots' own: a relation into a module that does
    /// not list the walking one in containableBy is walked only with one, as the host walks it (review 5: serve took none).</param>
    public static PackMemory From(IEnumerable<KnowledgePack> knowledge, IEnumerable<ModuleSnapshot>? snapshots = null, IEnumerable<ModuleConsentGrant>? grants = null)
    {
        ArgumentNullException.ThrowIfNull(knowledge);
        var packs = knowledge.ToList();
        var shots = (snapshots ?? []).ToList();
        var manifests = new Dictionary<string, ModuleManifest>(StringComparer.Ordinal);
        // A dependency shared by snapshots comes once; two different manifests under one id are refused, never one silently kept
        // (review 7, W4: the first won and hid the duplicate from the resolver's own check).
        foreach (var manifest in packs.Select(p => p.Manifest).Concat(shots.SelectMany(s => s.Dependencies.Prepend(s.Module))))
            if (!manifests.TryAdd(manifest.Id, manifest) && PackTrialGate.DigestOf(manifests[manifest.Id]) != PackTrialGate.DigestOf(manifest))
                throw new KnowledgePackException([$"Two different manifests of {manifest.Id} are given: a host holds one version of a module."]);
        var resolution = ModuleResolver.Resolve([.. manifests.Values], ModuleSet.SdkVersion, ModuleHostFeatures.Known);
        if (!resolution.Succeeded) throw new KnowledgePackException(resolution.Errors);
        // PACK-consent: links into another module's records need its consent or the host's grant, as the store requires.
        resolution = resolution with { ConsentGrants = [.. shots.SelectMany(s => s.ConsentGrants).Concat(grants ?? []).Distinct()] };
        // Every snapshot as an import checks it, with the grants the walk will have (review 6, L8: a snapshot a caller built was
        // never checked here).
        var invalid = shots.SelectMany(s => (s with { ConsentGrants = resolution.ConsentGrants }).Problems()).Distinct(StringComparer.Ordinal).ToList();
        if (invalid.Count != 0) throw new KnowledgePackException(invalid);
        var refused = shots.SelectMany(s => s.Records.SelectMany(r => r.Links.Select(l => s.Module.Ontology?.Relation(l.Relation, r.Type)))
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
        // A snapshot whose links reach into a dependency type that holds no records is refused, as the store refuses it at import
        // (review 7, W9: the store resolved links as it wrote, memory once all was mounted, so import order changed the walk).
        // What is held and what is linked, each worked out once (round 10, C-M3: a scan per relation of every record and every
        // record held made this cost relations times records).
        var held = memory._byId.Values.Select(r => (r.ModuleId, r.Type)).ToHashSet();
        foreach (var shot in shots)
        {
            var linked = shot.Records.SelectMany(r => r.Links.Select(l => (r.Type, l.Relation))).ToHashSet();
            foreach (var relation in shot.Module.Ontology!.Relations.Where(r => r.To.Contains(':', StringComparison.Ordinal) && !r.To.StartsWith("core:", StringComparison.Ordinal)))
                if (linked.Contains((relation.From, relation.Name))
                    && Target(shot.Module.Id, relation) is { } into && !held.Contains((into.Module, into.Type)))
                    throw new KnowledgePackException([$"{shot.Module.Id} links into {relation.To}, which holds no records here yet: import {into.Module} first."]);
        }
        // Links once every record is in: a link resolves to the record its target key names, in this module or a dependency.
        foreach (var (manifest, records) in sources)
            foreach (var record in records)
            {
                var links = new List<ModuleRecordLink>();
                foreach (var link in record.Links)
                {
                    var relation = manifest.Ontology!.Relation(link.Relation, record.Type);
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
    public static PackMount Mount(IEnumerable<byte[]> archives, PackTrust trust) => Mount(archives, trust, off: null);

    /// <summary>Mounts the packs as <see cref="Mount(IEnumerable{byte[]}, PackTrust)"/> does, with the host's consent grants.</summary>
    public static PackMount Mount(IEnumerable<byte[]> archives, PackTrust trust, IEnumerable<ModuleConsentGrant> grants) => Mount(archives, trust, null, grants);

    // off: given the loaded packs, the ids a host's gate keeps off; they, and snapshots of a definition kept off, are not
    // mounted (review 1, M1: ampack serve honours the gate).
    internal static PackMount Mount(IEnumerable<byte[]> archives, PackTrust trust, Func<IReadOnlyList<LoadedPack>, IReadOnlySet<string>, IReadOnlyCollection<string>>? off,
        IEnumerable<ModuleConsentGrant>? grants = null)
    {
        ArgumentNullException.ThrowIfNull(archives);
        ArgumentNullException.ThrowIfNull(trust);
        var sets = new List<LoadedSet>();
        var direct = new List<LoadedPack>();
        var familyMembers = new HashSet<string>(StringComparer.Ordinal);
        // Packs also reached on their own, in a set or given directly: a pack is a family member only where every route to it is
        // a family's (review 8, W3: one family route made a standalone pack a family member, whose certificates the gate set aside).
        var standalone = new HashSet<string>(StringComparer.Ordinal);
        foreach (var archive in archives)
        {
            var (manifest, content) = PackLoader.Open(archive, trust);
            switch (manifest.Kind)
            {
                case "set":
                    var set = PackFamilies.LoadSet(content, trust, null);
                    sets.Add(set);
                    familyMembers.UnionWith(set.Families.SelectMany(f => f.AllMembers).Select(p => p.Id));
                    standalone.UnionWith(set.Packs.Select(p => p.Id));
                    break;
                case "composite":
                    throw new PackLoadException(["A composite pins its members but does not carry them: mount the pack set that carries both."]);
                default:
                    var pack = PackLoader.Load(content, trust, null, allowDowngrade: false);
                    direct.Add(pack);
                    standalone.Add(pack.Id);
                    break;
            }
        }
        familyMembers.ExceptWith(standalone);
        // Held as a host holds them, before the gate or anything else reads them (review 7, W4): composed by the host's own
        // composition, the one decider of what is held (round 11; round 12, R12-1: a check here on what was only carried refused a
        // pack a family carries switched off and the host composes once); then one live snapshot of a module.
        var loaded = PackMountPlan.Of(PackFamilies.HostComposes(sets, direct));
        if (off?.Invoke(loaded, familyMembers) is { Count: > 0 } kept)
            loaded.RemoveAll(p => kept.Contains(p.Id) || (p.Kind == "snapshot" && p.Manifest.Modules.Keys.Any(kept.Contains)));
        var definitions = loaded.Where(p => p.Kind == "definition").ToDictionary(p => p.Id, p => p.Manifest, StringComparer.Ordinal);
        var snapshots = new List<ModuleSnapshot>();
        foreach (var shot in loaded.Where(p => p.Kind == "snapshot"))
        {
            var of = shot.Manifest.Modules.Keys.Single();
            if (!definitions.TryGetValue(of, out var definition))
                throw new PackLoadException([$"{shot.Id} is a snapshot of {of}: mount that definition pack beside it."]);
            // With the host's grants, the same ones the walk gets below: one consent check, one set of grants (review 6, M1).
            snapshots.Add(shot.ToSnapshot(definition, [.. definition.Modules.Keys.Where(definitions.ContainsKey).Select(d => definitions[d])], [.. grants ?? []]));
        }
        try { return new PackMount(loaded, From(loaded.Select(p => p.Knowledge).OfType<KnowledgePack>(), snapshots, grants)); }
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
    /// The hits are what a host offers: each module's own records that are not hubs, at most its <c>maxItems</c> (review 2).
    /// </summary>
    public async Task<IReadOnlyList<PackHit>> WalkAsync(IReadOnlyList<ContextAnchor> anchors, string? question = null, CancellationToken cancellationToken = default) =>
        (await WalkAsync(anchors, question, live: null, cancellationToken).ConfigureAwait(false)).Hits;

    // live: whether a module takes part now; a pack past its validUntil neither seeds, steers nor offers (review 2, M2 in serve).
    // Problems: what the walk could not do (a record past the link limit, a refused read), for the caller to show (review 3).
    internal async Task<(IReadOnlyList<PackHit> Hits, IReadOnlyList<string> Problems)> WalkAsync(IReadOnlyList<ContextAnchor> anchors, string? question,
        Func<string, bool>? live, CancellationToken cancellationToken, Func<string, bool>? liveCarrier = null)
    {
        ArgumentNullException.ThrowIfNull(anchors);
        var walk = await TraversalWalk.RunAsync(Resolution, this,
            new ContextRequest { SessionId = "pack", Owner = "pack", Query = question ?? "", Anchors = anchors }, cancellationToken, live: live,
            // A record takes part only while the pack that carried it does (review 7, W8): decided where the walk seeds, steers
            // and offers, not after it.
            liveRecord: liveCarrier is null ? null : r => r.Source.Pack?.Carrier?.Id is not { } carrier || liveCarrier(carrier)).ConfigureAwait(false);
        return ([.. Offered(Resolution, walk.Items).Select(i => Hit(i.Record, i.Via))], walk.Problems);
    }

    /// <summary>
    /// What a host offers from a walk, as each module's traversal contributor takes it: the module's own records that are not
    /// hubs, at most its maxItems, in the walk's order. `ampack serve`, REST and pack tests show this, not every record reached.
    /// </summary>
    internal static IEnumerable<TraversalItem> Offered(ModuleResolution resolution, IReadOnlyList<TraversalItem> items)
    {
        var taken = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var traversal = resolution.Modules.FirstOrDefault(m => m.Id == item.Record.ModuleId)?.Traversal;
            if (traversal is null || traversal.Hubs.Contains(item.Record.Type, StringComparer.Ordinal)) continue;
            var held = taken.GetValueOrDefault(item.Record.ModuleId);
            if (held >= traversal.MaxItems) continue;
            taken[item.Record.ModuleId] = held + 1;
            yield return item;
        }
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
        var values = query.Values.Where(v => !ModuleAnchorQuery.IsBlank(v)).ToList();
        if (values.Count == 0 || !Sees(context)) return Task.FromResult(new ModuleReadResult { Status = ModuleReadStatus.Succeeded });
        // Exactly the records that match by the walk's own rules, in the format's order (review 2: the first 512 records that held
        // the property, matching or not, in key order).
        // The walk's time budget reaches this scan through the token (review 3: a common name scanned a whole type unbounded).
        // Best match first, then the format's order, at most the limit: what every store returns (review 6, M2).
        // Every record looks at the budget: a record's match is bounded in work (review 7, W1: every 1,024 records was a count,
        // and one record could cost seconds).
        var quality = (query with { Values = values }).Matcher();
        var found = OfType(module.Id, query.Type)
            .Select(r => { cancellationToken.ThrowIfCancellationRequested(); return r; })
            .Select(r => (Record: r, Quality: Text(r, query.Property) is { } value ? quality(value) : 0))
            .Where(p => p.Quality > 0)
            .OrderByDescending(p => p.Quality).ThenBy(p => p.Record, Context.Traversal.TraversalOrder.Records)
            .Take(Math.Clamp(query.Limit, 1, ModuleAnchorQuery.MaximumLimit)).Select(p => p.Record).ToList();
        return Task.FromResult(new ModuleReadResult { Status = ModuleReadStatus.Succeeded, Records = found.AsReadOnly() });
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
        var words = query.Words.Where(w => !ModuleAnchorQuery.IsBlank(w)).Select(ModuleExpandQuery.Word)
            .Where(w => w.Length <= ModuleExpandQuery.MaximumWordLength).Distinct(StringComparer.Ordinal).Take(ModuleExpandQuery.MaximumWords).ToList();
        var fanOut = Math.Clamp(query.FanOut, 1, Math.Min(declared.FanOut, ModuleTraversalRelation.MaximumFanOut));
        var edges = new List<ModuleTraversalEdge>();
        var skipped = new List<string>();
        foreach (var from in query.FromIds.Distinct(StringComparer.Ordinal))
        {
            // The walk's budget reaches each start (review 6, M3: an expansion ran to the end whatever the budget).
            cancellationToken.ThrowIfCancellationRequested();
            if (!_byId.TryGetValue(from, out var start)) continue;
            // Every neighbour within the link limit is ranked (review 2: the first 1,024 in id order were); a record past it is
            // not expanded, and the walk is told; no more links are read than show that (review 6, M3: every link was).
            // Distinct live neighbours, counted as the Neo4j store counts them (review 3); an In neighbour's degree is its own links.
            var links = (query.Direction == ModuleTraversalDirection.Out
                ? start.Links.Where(l => l.Relation == relation.Name).Select(l => _byId[l.TargetId])
                : _incoming.GetValueOrDefault((relation.Name, start.Id)) ?? []).DistinctBy(r => r.Id).Take(ModuleExpandQuery.MaximumLinks + 1).ToList();
            if (links.Count > ModuleExpandQuery.MaximumLinks)
            {
                skipped.Add($"{start.Key} has more than {ModuleExpandQuery.MaximumLinks} {relation.Name} links: not expanded.");
                continue;
            }
            IEnumerable<(ModuleRecord Record, int Degree)> reached = links.Select(r => (r, Degree(relation.Name, r, query.Direction)));
            var ranked = reached
                .Select(p => (p.Record, p.Degree, Hits: Hits(p.Record, text, words)))
                .OrderByDescending(p => p.Hits).ThenBy(p => p.Degree).ThenBy(p => p.Record, Context.Traversal.TraversalOrder.Records).Take(fanOut);
            edges.AddRange(ranked.Select(p => new ModuleTraversalEdge { FromId = from, To = p.Record with { Links = [] }, Degree = Math.Max(1, p.Degree) }));
        }
        return Task.FromResult(new ModuleTraversalStep { Status = ModuleReadStatus.Succeeded, Edges = edges.AsReadOnly(), Errors = skipped });
    }

    private static Task<ModuleTraversalStep> Refused(string error) =>
        Task.FromResult(new ModuleTraversalStep { Status = ModuleReadStatus.Refused, Errors = [error] });

    // A reached record's degree through one relation, counted once for the mount, up to MaximumScan (review 6, M3: counted in
    // full for every reach): through an Out step, the records that link to it; through an In step, the records it links to.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Relation, string Id, ModuleTraversalDirection Direction), int> _degrees = new();

    private int Degree(string relation, ModuleRecord reached, ModuleTraversalDirection direction) =>
        _degrees.GetOrAdd((relation, reached.Id, direction), key => key.Direction == ModuleTraversalDirection.Out
            ? (_incoming.GetValueOrDefault((key.Relation, key.Id)) ?? []).DistinctBy(o => o.Id).Take(ModuleExpandQuery.MaximumScan).Count()
            : reached.Links.Where(l => l.Relation == key.Relation).Select(l => l.TargetId).Distinct(StringComparer.Ordinal).Take(ModuleExpandQuery.MaximumScan).Count());

    private static string? Text(ModuleRecord record, string property) =>
        record.Properties.TryGetValue(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Present(ModuleRecord record, string property) =>
        record.Properties.TryGetValue(property, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    // How many of the question's words the record's text holds, as the store's ranking counts them.
    private static int Hits(ModuleRecord record, IReadOnlyList<string> textProperties, IReadOnlyList<string> words)
    {
        if (words.Count == 0) return 0;
        var text = string.Join(' ', textProperties.Select(p => record.Properties.TryGetValue(p, out var v)
            && v.ValueKind == JsonValueKind.String ? v.GetString() : ""));
        text = Context.Traversal.AnchorMatcher.Folded(text);
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
