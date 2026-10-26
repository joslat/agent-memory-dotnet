using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentMemory.Abstractions.Options;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Reading;

namespace AgentMemory.Extensibility.Context.Traversal;

/// <summary>A record a walk reached: its score and how it was reached.</summary>
[Experimental("AMEXT001")]
internal sealed record TraversalItem(ModuleRecord Record, double Score, string Via, int Hops);

/// <summary>What one walk reached, best first, and the reads it could not make.</summary>
[Experimental("AMEXT001")]
internal sealed record TraversalResult(IReadOnlyList<TraversalItem> Items, IReadOnlyList<string> Problems)
{
    internal static readonly TraversalResult Empty = new([], []);
}

/// <summary>
/// The traversal walk (CMP-1): anchors match records through declared anchor properties; declared links are followed for at
/// most two hops; each reached record keeps its best score (anchor quality times the weights along the path) and that path.
/// Bounded in hops, fan-out, visited records and a score floor, so a hub cannot flood the gate.
/// </summary>
[Experimental("AMEXT001")]
internal static class TraversalWalk
{
    internal const int MaximumHops = 2;
    internal const int MaximumVisited = 256;
    internal const double MinimumScore = 0.1;

    internal static async Task<TraversalResult> RunAsync(ModuleResolution resolution, IModuleTraversalReader reader, ContextRequest request,
        CancellationToken cancellationToken)
    {
        var anchors = request.Anchors
            .Where(a => a is not null && !string.IsNullOrWhiteSpace(a.Value) && a.Value.Length <= ContextAnchor.MaximumLength)
            .DistinctBy(a => (a.Kind, a.Value)).Take(ContextAnchor.MaximumPerRequest).ToList();
        if (anchors.Count == 0) return TraversalResult.Empty;
        var context = new ModuleReadContext
        {
            Scope = request.ResolvedScope ?? (request.Owner is null ? null : MemoryScope.For(request.Owner)),
            SessionId = request.SessionId, SystemAsOfUtc = request.SystemAsOf
        };
        var modules = resolution.Modules.Where(m => m.Traversal is not null && m.Ontology is not null).ToList();
        var best = new Dictionary<string, TraversalItem>(StringComparer.Ordinal);
        var problems = new List<string>();

        // Seeds: records whose anchor properties match the anchors.
        foreach (var module in modules)
            foreach (var declared in module.Traversal!.Anchors)
            {
                var values = anchors.Where(a => a.Kind == declared.Kind).Select(a => a.Value).ToList();
                if (values.Count == 0) continue;
                var found = await reader.MatchAsync(new ModuleAnchorQuery
                {
                    ModuleId = module.Id, Type = declared.Type, Property = declared.Property, Match = declared.Match, Values = values
                }, context, cancellationToken).ConfigureAwait(false);
                if (found.Status != ModuleReadStatus.Succeeded) { problems.AddRange(found.Errors.Select(e => module.Id + ": " + e)); continue; }
                foreach (var record in found.Records)
                {
                    var value = record.Properties.TryGetValue(declared.Property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                    var (quality, anchor, pattern) = AnchorMatcher.Best(declared.Match, value, values);
                    if (quality <= 0) continue;
                    var via = Short(anchor!) + (string.Equals(pattern, anchor, StringComparison.OrdinalIgnoreCase)
                        ? "" : " matches " + declared.Property + " " + pattern);
                    Offer(best, new TraversalItem(record, quality, via, 0));
                }
            }

        // Hops: owned links, in their declared direction, from what the last hop reached.
        var frontier = best.Values.ToList();
        for (var hop = 1; hop <= MaximumHops && frontier.Count > 0 && best.Count < MaximumVisited; hop++)
        {
            var next = new List<TraversalItem>();
            foreach (var module in modules)
                foreach (var declared in module.Traversal!.Relations)
                {
                    var relation = module.Ontology!.Relations.Single(r => r.Name == declared.Name);
                    var start = declared.Direction == ModuleTraversalDirection.Out ? (module.Id, relation.From) : Target(module, relation);
                    if (start is not { } from) continue;
                    var starting = frontier.Where(i => i.Record.ModuleId == from.Module && i.Record.Type == from.Type)
                        .GroupBy(i => i.Record.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.MaxBy(i => i.Score)!, StringComparer.Ordinal);
                    if (starting.Count == 0) continue;
                    var step = await reader.ExpandAsync(new ModuleExpandQuery
                    {
                        ModuleId = module.Id, Relation = declared.Name, Direction = declared.Direction,
                        FromIds = [.. starting.Keys.Order(StringComparer.Ordinal)], FanOut = declared.FanOut
                    }, context, cancellationToken).ConfigureAwait(false);
                    if (step.Status != ModuleReadStatus.Succeeded) { problems.AddRange(step.Errors.Select(e => module.Id + ": " + e)); continue; }
                    foreach (var edge in step.Edges)
                    {
                        if (!starting.TryGetValue(edge.FromId, out var parent)) continue;
                        var score = parent.Score * declared.Weight;
                        if (score < MinimumScore) continue;
                        var verb = Verb(module, declared.Name);
                        var via = parent.Via + " → " + (declared.Direction == ModuleTraversalDirection.Out
                            ? verb + " " + Label(edge.To) : Label(edge.To) + " " + verb + " it");
                        var item = new TraversalItem(edge.To, score, via, hop);
                        if (Offer(best, item)) next.Add(item);
                        if (best.Count >= MaximumVisited) break;
                    }
                }
            frontier = next;
        }
        return new([.. best.Values.OrderByDescending(i => i.Score).ThenBy(i => i.Hops).ThenBy(i => i.Record.Id, StringComparer.Ordinal)],
            problems.AsReadOnly());
    }

    private static bool Offer(Dictionary<string, TraversalItem> best, TraversalItem item)
    {
        if (best.TryGetValue(item.Record.Id, out var known) && known.Score >= item.Score) return false;
        best[item.Record.Id] = item;
        return true;
    }

    private static (string Module, string Type)? Target(ModuleManifest module, ModuleRelation relation)
    {
        if (relation.To.StartsWith("core:", StringComparison.Ordinal)) return null;   // core entities are not walked in phase A
        var parts = relation.To.Split(':');
        return parts.Length == 2 ? (parts[0], parts[1]) : (module.Id, relation.To);
    }

    // DECISIONS_SUPERSEDES → "supersedes", CODEMAP_TESTS → "tests".
    private static string Verb(ModuleManifest module, string relation)
    {
        var prefix = ModuleManifestValidator.SchemaName(module.Ontology!.LabelPrefix).ToUpperInvariant() + "_";
        var name = relation.StartsWith(prefix, StringComparison.Ordinal) ? relation[prefix.Length..] : relation;
        return name.Replace('_', ' ').ToLowerInvariant();
    }

    // A record's name for a reason: its title, name or path when it has one, else its key, shortened.
    private static string Label(ModuleRecord record)
    {
        foreach (var property in (string[])["title", "name", "path"])
            if (record.Properties.TryGetValue(property, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text)
                return Short(text);
        return Short(record.Key);
    }

    private static string Short(string value)
    {
        var text = value.Trim();
        if (text.Contains(' ') && text.Contains('#') || text.EndsWith('#') || text.EndsWith(')') || text.EndsWith('.'))
            return AnchorMatcher.SimpleName(text) is { Length: > 0 } simple ? simple : text;
        var slash = text.LastIndexOf('/');
        text = slash >= 0 && slash < text.Length - 1 ? text[(slash + 1)..] : text;
        return text.Length <= 80 ? text : text[..77] + "...";
    }
}

/// <summary>One walk per request, shared by every module's traversal contributor in it (CMP-1).</summary>
[Experimental("AMEXT001")]
internal sealed class TraversalService(ModuleResolution resolution, IModuleTraversalReader? reader)
{
    private readonly ConditionalWeakTable<ContextRequest, Task<TraversalResult>> _walks = new();

    /// <summary>Whether the store can walk at all.</summary>
    internal bool Available => reader is not null;

    internal Task<TraversalResult> WalkAsync(ContextRequest request, CancellationToken cancellationToken) =>
        reader is null || request.Anchors.Count == 0
            ? Task.FromResult(TraversalResult.Empty)
            : _walks.GetValue(request, r => TraversalWalk.RunAsync(resolution, reader, r, cancellationToken));
}
