using System.ComponentModel;
using System.Text.Json;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Extensibility.Context;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Ampack;

/// <summary>
/// <c>ampack serve</c> (pack format v1.2 §10, PACK-serve): verified packs mounted in memory and served over MCP on standard
/// input and output, so any agent, in any language, reads a pack with no .NET host and no database. Read-only: the tools list
/// the packs, ask a view, and walk from the work in hand; every hit names the pack, version, digest, file and line it came
/// from. Logs go to standard error, which MCP leaves free.
/// </summary>
internal static class Serve
{
    internal static int Run(PackMount mount)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddSingleton(mount);
        builder.Services.AddMcpServer(o => o.ServerInfo = new() { Name = "ampack", Version = typeof(Serve).Assembly.GetName().Version?.ToString() ?? "0" })
            .WithStdioServerTransport()
            .WithTools<PackTools>();
        Console.Error.WriteLine($"ampack serve: {mount.Packs.Count} pack(s), {mount.Memory.Count:N0} records, MCP on standard input and output.");
        builder.Build().Run();
        return 0;
    }
}

/// <summary>The tools <c>ampack serve</c> offers: read-only, over the packs it mounted.</summary>
[McpServerToolType]
internal sealed class PackTools
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };

    [McpServerTool(Name = "pack_list", ReadOnly = true), Description("The memory packs this server holds: id, version, digest, kind, title, description, the views each offers (search views take text; any view takes a record key), and whether it can be walked from files or symbols.")]
    public static string List(PackMount mount) => JsonSerializer.Serialize(mount.Packs.Select(p => new
    {
        id = p.Id, version = p.Version, digest = p.Digest, kind = p.Kind,
        title = p.Manifest.Display?.Title, description = p.Manifest.Display?.Description,
        views = (mount.Memory.Resolution.Modules.FirstOrDefault(m => m.Id == p.Id)?.Views ?? p.Manifest.Views)
            .Select(v => new { id = v.Id, type = v.Type, search = v.Search is not null }),
        walkable = p.Manifest.Traversal is not null,
        source = p.SnapshotSource, validUntil = p.ValidUntil, expired = Expired(p),
    }), Json);

    // PACK-staleness: a pack past its validUntil is switched off while the server runs, as a host switches it off.
    private static bool Expired(LoadedPack pack) => pack.ValidUntil is { } until && DateTimeOffset.UtcNow > until;

    private static HashSet<string> ExpiredIds(PackMount mount) =>
        mount.Packs.Where(Expired).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);

    [McpServerTool(Name = "pack_ask", ReadOnly = true), Description("Ask one view of a pack: a search view with a question or keywords, or any view with an exact record key. Returns the records as their type renders them, each with its pack provenance.")]
    public static async Task<string> Ask(PackMount mount,
        [Description("The pack id, from pack_list")] string pack,
        [Description("The view id, from pack_list")] string view,
        [Description("The question or keywords, for a search view")] string? text = null,
        [Description("An exact record key, instead of text")] string? key = null,
        [Description("At most this many records (the view's own limit when omitted)")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        if (mount.Packs.FirstOrDefault(p => p.Id == pack) is { } loaded && Expired(loaded))
            return JsonSerializer.Serialize(new { error = $"{pack} was valid until {loaded.ValidUntil:u}; it is switched off." }, Json);
        var expired = ExpiredIds(mount);
        // A snapshot's records leave with their carrier, as on a walk (red team 7).
        try
        {
            var hits = await mount.Memory.AskAsync(pack, view, text, key, limit, cancellationToken).ConfigureAwait(false);
            return Hits([.. hits.Where(h => !(h.Pack?.Carrier?.Id is { } carrier && expired.Contains(carrier)))]);
        }
        catch (PackLoadException e) { return JsonSerializer.Serialize(new { error = string.Join(" ", e.Problems) }, Json); }
    }

    [McpServerTool(Name = "pack_walk", ReadOnly = true), Description("Walk the packs from the work in hand: the files and symbols you are working on, and optionally what you are trying to do. Follows only the relations the packs declare, within fixed budgets, and returns what governs that work (for code: its types, their tests, decisions), each with the reason path it was reached by.")]
    public static async Task<string> Walk(PackMount mount,
        [Description("Repository-relative paths of the files in hand")] string[]? paths = null,
        [Description("Symbols in hand: simple names or full identities")] string[]? symbols = null,
        [Description("What you are trying to do, in a sentence")] string? question = null,
        CancellationToken cancellationToken = default)
    {
        var anchors = (paths ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => ContextAnchor.Path(p))
            .Concat((symbols ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => ContextAnchor.Symbol(s)))
            .Take(ContextAnchor.MaximumPerRequest).ToList();
        if (anchors.Count == 0) return JsonSerializer.Serialize(new { error = "Give at least one path or symbol to walk from." }, Json);
        var expired = ExpiredIds(mount);
        var hits = await mount.Memory.WalkAsync(anchors, question, cancellationToken).ConfigureAwait(false);
        return Hits([.. hits.Where(h => !expired.Contains(h.Module) && !(h.Pack?.Carrier?.Id is { } carrier && expired.Contains(carrier)))]);
    }

    private static string Hits(IReadOnlyList<PackHit> hits) => JsonSerializer.Serialize(hits.Select(h => new
    {
        pack = h.Module, type = h.Type, key = h.Key, text = h.Text, via = h.Via,
        source = h.Pack is { } s ? new { id = s.Id, version = s.Version, digest = s.Digest, file = s.File, line = s.Line } : null,
    }), Json);
}
