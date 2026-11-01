using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace AgentMemory.Extensibility.Ampack;

/// <summary>
/// What <c>ampack serve</c> offers as MCP resources (feature 17) and as a read-only memory folder (feature 16, R4), over the packs
/// it mounted: <c>ampack://status/{id}</c>, and per pack digest its card, a view and a record. One door: every answer asks
/// <c>off</c> first, so a pack the host does not serve now (past its validUntil, its usage policy, or kept off by the trial gate
/// at startup) answers with its reason and never with records; a record past its life, or one an off carrier brought, is never
/// answered. Each answer is JSON.
/// </summary>
[Experimental("AMEXT001")]
public static class PackResources
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };

    /// <summary>The most entries one folder listing returns.</summary>
    public const int MaximumEntries = 1000;

    /// <summary>A pack's status: served or not, and why not.</summary>
    /// <param name="mount">The mounted packs.</param>
    /// <param name="id">The pack id.</param>
    /// <param name="off">Why a mounted pack is not served now, or null.</param>
    /// <param name="keptOff">The packs the trial gate kept off at startup, with its reason.</param>
    public static string Status(PackMount mount, string id, Func<LoadedPack, string?> off, IReadOnlyDictionary<string, string> keptOff)
    {
        ArgumentNullException.ThrowIfNull(mount);
        if (keptOff.TryGetValue(id, out var gated)) return Serialize(new { id, served = false, reason = gated });
        var versions = mount.Packs.Where(p => p.Id == id).ToList();
        if (versions.Count == 0) return Serialize(new { id, served = false, reason = $"{id} is not mounted on this server." });
        return Serialize(versions.Select(p => off(p) is { } reason
            ? (object)new { id, version = p.Version, digest = p.Digest, served = false, reason }
            : new { id, version = p.Version, digest = p.Digest, served = true, validUntil = p.ValidUntil }).ToList());
    }

    /// <summary>A pack's card, by the pack's digest.</summary>
    public static string Card(PackMount mount, string digest, Func<LoadedPack, string?> off) =>
        Served(mount, digest, off, out var pack, out var error) ? Serialize(new { id = pack!.Id, version = pack.Version, digest = pack.Digest, card = pack.Card }) : error!;

    /// <summary>A view of a pack: what it answers, and how to ask it (pack_ask).</summary>
    public static string View(PackMount mount, string digest, string view, Func<LoadedPack, string?> off)
    {
        if (!Served(mount, digest, off, out var pack, out var error)) return error!;
        var found = (mount.Memory.Resolution.Modules.FirstOrDefault(m => m.Id == pack!.Id)?.Views ?? pack!.Manifest.Views).FirstOrDefault(v => v.Id == view);
        return found is null ? Serialize(new { error = $"{pack!.Id} has no view '{view}'." })
            : Serialize(new { pack = pack!.Id, id = found.Id, type = found.Type, search = found.Search is not null, summary = found.Summary });
    }

    /// <summary>One record a pack brought, live now, by type and key.</summary>
    public static string Record(PackMount mount, string digest, string type, string key, Func<LoadedPack, string?> off, DateTimeOffset now)
    {
        if (!Served(mount, digest, off, out var pack, out var error)) return error!;
        var hit = Held(mount, pack!, off, now).FirstOrDefault(h => h.Type == type && h.Key == key);
        return hit is null ? Serialize(new { error = $"{pack!.Id} holds no live {type} '{key}'." }) : Serialize(Shown(hit));
    }

    /// <summary>
    /// The packs as a read-only memory folder (feature 16, R4): <c>/memories</c> lists the packs, <c>/memories/{id}</c> a pack's
    /// records as files <c>{type}/{key}</c>, and <c>/memories/{id}/{type}/{key}</c> a record's text. Nothing is ever written.
    /// Given <paramref name="from"/>, a record in hand as <c>{type}/{key}</c>, a pack's entries are ordered by the walk from it:
    /// that record, then the records its links reach (either way) by the fewest links, up to three, then the rest.
    /// </summary>
    public static string MemoryView(PackMount mount, string path, Func<LoadedPack, string?> off, IReadOnlyDictionary<string, string> keptOff, DateTimeOffset now,
        string? from = null)
    {
        ArgumentNullException.ThrowIfNull(mount);
        var parts = (path ?? "").Trim().TrimEnd('/').Split('/', 4, StringSplitOptions.None);
        if (parts is not ["", "memories", ..]) return Serialize(new { error = "Paths start at /memories." });
        if (parts.Length == 2)
            return Serialize(new
            {
                path = "/memories",
                entries = mount.Packs.Select(p => p.Id).Distinct().Order(StringComparer.Ordinal)
                    .Select(id => (object)(mount.Packs.Where(p => p.Id == id).Select(off).FirstOrDefault(r => r is not null) is { } reason
                        ? new { name = id + "/", served = false, reason } : new { name = id + "/", served = true }))
                    .Concat(keptOff.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => (object)new { name = k.Key + "/", served = false, reason = k.Value })).ToList(),
            });
        var id = parts[2];
        if (keptOff.TryGetValue(id, out var gated)) return Serialize(new { error = gated });
        var pack = mount.Packs.Where(p => p.Id == id).OrderByDescending(p => p.Version, StringComparer.Ordinal).FirstOrDefault();
        if (pack is null) return Serialize(new { error = $"{id} is not mounted on this server." });
        if (off(pack) is { } offReason) return Serialize(new { error = offReason });
        var held = Held(mount, pack, off, now);
        if (parts.Length == 3)
        {
            var entries = held.Select(h => h.Type + "/" + h.Key).ToList();
            if (from is not null)
            {
                var anchor = from.Trim().Trim('/');
                if (!entries.Contains(anchor)) return Serialize(new { error = $"{id} holds no live record at '{anchor}' to walk from." });
                // Breadth first over the pack's links, three links at most, as a walk goes; the rest keep their order after.
                var moduleOf = held.GroupBy(h => h.Type + "/" + h.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Module, StringComparer.Ordinal);
                var distance = new Dictionary<string, int>(StringComparer.Ordinal) { [anchor] = 0 };
                var frontier = new List<string> { anchor };
                for (var hop = 1; hop <= 3 && frontier.Count > 0; hop++)
                {
                    var next = new List<string>();
                    foreach (var at in frontier)
                    {
                        var cut = at.IndexOf('/', StringComparison.Ordinal);
                        // Each record's own module: a snapshot's records may be its definition's and its dependencies'.
                        foreach (var (type, key) in mount.Memory.Neighbours(moduleOf[at], at[..cut], at[(cut + 1)..], now))
                            if (distance.TryAdd(type + "/" + key, hop) && entries.Contains(type + "/" + key)) next.Add(type + "/" + key);
                    }
                    frontier = next;
                }
                entries = [.. entries.OrderBy(e => distance.GetValueOrDefault(e, int.MaxValue)).ThenBy(e => e, StringComparer.Ordinal)];
            }
            return Serialize(new
            {
                path = "/memories/" + id, total = held.Count, from = from is null ? null : from.Trim().Trim('/'),
                entries = entries.Take(MaximumEntries).ToList(),
            });
        }
        var rest = parts[3];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        var hit = slash <= 0 ? null : held.FirstOrDefault(h => h.Type == rest[..slash] && h.Key == rest[(slash + 1)..]);
        return hit is null ? Serialize(new { error = $"No live record at {path}." }) : Serialize(new { path, text = hit.Text, source = Source(hit) });
    }

    // The pack whose digest is given (with or without 'sha256:'), if this server serves it now.
    private static bool Served(PackMount mount, string digest, Func<LoadedPack, string?> off, out LoadedPack? pack, out string? error)
    {
        ArgumentNullException.ThrowIfNull(mount);
        var full = digest.StartsWith("sha256:", StringComparison.Ordinal) ? digest : "sha256:" + digest;
        pack = mount.Packs.FirstOrDefault(p => p.Digest == full);
        error = pack is null ? Serialize(new { error = $"No pack of digest {full} is mounted on this server." })
            : off(pack) is { } reason ? Serialize(new { id = pack.Id, served = false, reason }) : null;
        return error is null;
    }

    // The live records a pack brought: its own module's, or a snapshot's, never one an off pack or an off carrier brought.
    private static List<PackHit> Held(PackMount mount, LoadedPack pack, Func<LoadedPack, string?> off, DateTimeOffset now)
    {
        var offIds = mount.Packs.Where(p => off(p) is not null).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        return [.. mount.Memory.Live(now).Where(h => !offIds.Contains(h.Module) && !(h.Pack?.Carrier?.Id is { } carrier && offIds.Contains(carrier))
            && (pack.Kind == "snapshot" ? h.Pack?.Carrier?.Id == pack.Id : h.Module == pack.Id && h.Pack?.Carrier is null))];
    }

    private static object Shown(PackHit h) => new { pack = h.Module, type = h.Type, key = h.Key, text = h.Text, source = Source(h) };

    private static object? Source(PackHit h) => h.Pack is { } s ? new { id = s.Id, version = s.Version, digest = s.Digest, file = s.File, line = s.Line } : null;

    private static string Serialize(object value) => JsonSerializer.Serialize(value, Json);
}
