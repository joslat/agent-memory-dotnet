using System.Text.Json;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Packs.Architecture;
using AgentMemory.Packs.Decisions;
using AgentMemory.Packs.Intent;

#pragma warning disable AMEXT001 // Onboarding review sets are an Extensibility preview feature.

namespace AgentMemory.McpHost.Coding;

/// <summary>
/// CMP-9 over MCP: the review sets of one repository (<c>docs/memory/review/&lt;module&gt;.json</c>), listed and decided by
/// <c>review_list</c> and <c>review_decide</c>. A decision is the same file change <c>agentmemory onboard decide</c> makes;
/// the pack's next import merges what was accepted. An edit is checked against the pack's own rules before it is kept.
/// </summary>
internal sealed class CodingReview(string root)
{
    private static readonly Dictionary<string, Func<ModuleManifest>> Manifests = new(StringComparer.OrdinalIgnoreCase)
    {
        ["decisions"] = () => DecisionsPack.Manifest, ["architecture"] = () => ArchitecturePack.Manifest, ["intent"] = () => IntentPack.Manifest,
    };

    private readonly string _root = Path.GetFullPath(root);

    /// <summary>The proposals of one pack, or of every pack with a review set, optionally in one state.</summary>
    public object List(string? pack, ReviewState? state)
    {
        var sets = new List<object>();
        foreach (var (name, manifest) in Packs(pack))
        {
            var path = PathOf(manifest);
            if (!File.Exists(path)) continue;
            var set = ModuleReview.Parse(File.ReadAllText(path));
            sets.Add(new
            {
                pack = name, module = set.Module,
                byState = set.Items.GroupBy(i => i.State.ToString().ToLowerInvariant()).ToDictionary(g => g.Key, g => g.Count()),
                items = set.Items.Where(i => state is null || i.State == state).Take(200).Select(i => new
                {
                    key = $"{i.Type}:{i.Key}", state = i.State.ToString().ToLowerInvariant(), i.Confidence, properties = i.Properties,
                    evidence = i.Evidence.Select(e => new { e.File, e.Line, e.EndLine, e.Quote }), i.ProposedBy, i.ReviewedBy, i.Note,
                }),
            });
        }
        return new { sets };
    }

    /// <summary>Accepts or rejects proposals, optionally with edits; null when the pack has no review set.</summary>
    /// <exception cref="ArgumentException">An unknown pack or key, or an edit that makes a record the pack refuses.</exception>
    public object? Decide(CodingIdentity identity, string pack, IReadOnlyList<string> keys, bool accept, IReadOnlyDictionary<string, JsonElement>? edit, string? note)
    {
        if (!Manifests.TryGetValue(pack, out var manifest)) throw new ArgumentException($"No pack '{pack}'.", nameof(pack));
        var path = PathOf(manifest());
        if (!File.Exists(path)) return null;
        var set = ModuleReview.Decide(ModuleReview.Parse(File.ReadAllText(path)), keys, accept, "coding:" + identity.OwnerId, DateTimeOffset.UtcNow,
            note, accept ? edit : null);
        // Each accepted record must be one the pack takes, as its import will check.
        var admitted = new ModuleSnapshot
        {
            Module = manifest(), Dependencies = [], Source = "review",
            Records = [.. set.Items.Where(i => i.Admitted).Select(i => new ModuleProposal { Type = i.Type, Key = i.Key, Properties = i.Properties, Source = new ProposalSource { Excerpt = "review" } })],
        };
        if (admitted.Problems().FirstOrDefault(p => !p.Contains("link", StringComparison.OrdinalIgnoreCase)) is { } problem)
            throw new ArgumentException("The decision would make a record the pack refuses: " + problem, nameof(edit));
        File.WriteAllText(path, ModuleReview.Write(set), new System.Text.UTF8Encoding(false));
        return new
        {
            pack, decided = keys, accepted = accept,
            byState = set.Items.GroupBy(i => i.State.ToString().ToLowerInvariant()).ToDictionary(g => g.Key, g => g.Count()),
        };
    }

    private IEnumerable<(string Name, ModuleManifest Manifest)> Packs(string? pack) =>
        pack is null ? Manifests.Select(m => (m.Key, m.Value()))
        : Manifests.TryGetValue(pack, out var one) ? [(pack.ToLowerInvariant(), one())] : throw new ArgumentException($"No pack '{pack}'.", nameof(pack));

    private string PathOf(ModuleManifest manifest) => Path.Combine(_root, ModuleReview.DefaultPath(manifest.Id));
}
