using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace AgentMemory.Extensibility.Ampack;

/// <summary>
/// A Mesh Memory Protocol store imported (feature 15): the folder of entry files a node (sym, MMP 2.0) keeps, one Cognitive
/// Memory Block an entry. Each block becomes a note keyed by its key, its text the seven categories (focus, issue, intent,
/// motivation, commitment, perspective, mood) in that order, its time the block's own creation time. A block the store marks
/// superseded is not imported. The blocks' signatures bind the mesh's own canonical form, which this importer does not
/// reproduce: they are recorded as not checked, never as valid.
/// </summary>
public static partial class PackImports
{
    private static readonly string[] Cat7 = ["focus", "issue", "intent", "motivation", "commitment", "perspective", "mood"];

    /// <summary>Imports a Mesh Memory Protocol store: <paramref name="files"/> holds its entry files by name.</summary>
    /// <exception cref="InvalidDataException">An entry is not JSON, or the store holds no text to import.</exception>
    public static ImportResult FromMeshMemory(IReadOnlyDictionary<string, byte[]> files, string id, string version, string owner, string signingKeyPem)
    {
        var entries = new List<(string Key, string Text, IReadOnlyDictionary<string, string>? Life)>();
        var skipped = new List<string>();
        var dropped = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var source = new List<byte>();
        var signed = 0;
        // The store keeps its entries at its top level; anything in a subfolder is not an entry.
        static bool IsEntry(string n) => !n.Contains('/') && n.EndsWith(".json", StringComparison.Ordinal);
        foreach (var name in files.Keys.Where(IsEntry).Order(StringComparer.Ordinal))
        {
            // The store's own markers are not blocks.
            if (name == "migration-v1.json") { dropped["file '" + name + "'"] = 1; continue; }
            foreach (var part in new[] { Encoding.UTF8.GetBytes(name), SHA256.HashData(files[name]) })
            {
                source.AddRange(BitConverter.GetBytes(part.Length));
                source.AddRange(part);
            }
            if (Parse(files[name], $"'{Shown(name)}'") is not JsonObject entry) throw new InvalidDataException($"'{Shown(name)}' is not an entry object.");
            var cmb = entry["cmb"] as JsonObject;
            var metadata = cmb?["metadata"] as JsonObject;
            // A two-section block reads its metadata first, as the mesh does: that is the section its signature binds.
            var key = (metadata is null ? null : Str(metadata, "key")) ?? (cmb is null ? null : Str(cmb, "key")) ?? Str(entry, "key");
            if (key is not { Length: > 0 }) { skipped.Add($"'{Shown(name)}': no key"); continue; }
            if (Str(entry, "lifecycle") is "superseded") { skipped.Add($"'{Shown(key)}': superseded"); continue; }
            var categories = (cmb?["categories"] ?? cmb) as JsonObject;
            var text = Join("\n", [.. Cat7.Select(c => categories?[c] switch
            {
                JsonObject o when Str(o, "text") is { Length: > 0 } t => $"{c}: {t}",
                JsonValue v when v.TryGetValue<string>(out var t) && t.Length > 0 => $"{c}: {t}",
                _ => null,
            })]) ?? Str(entry, "content");
            if (text is null || string.IsNullOrWhiteSpace(text)) { skipped.Add($"'{Shown(key)}': no text"); continue; }
            foreach (var field in (categories ?? []).Select(f => f.Key).Where(f => !Cat7.Contains(f)))
                dropped[$"category '{field}'"] = dropped.GetValueOrDefault($"category '{field}'") + 1;
            if (metadata?["sig"] is not null) signed++;
            var created = metadata?["createdTimestamp"] ?? cmb?["createdAt"] ?? entry["originTimestamp"];
            var life = Millis(created) is { } from ? new Dictionary<string, string>(StringComparer.Ordinal) { ["validFrom"] = from } : null;
            foreach (var other in new[] { "lineage", "tags", "admission", "tether", "provenance" })
                if (entry[other] is JsonObject or JsonArray { Count: > 0 }) dropped["entry." + other] = dropped.GetValueOrDefault("entry." + other) + 1;
            entries.Add((key, text, life));
        }
        foreach (var other in files.Keys.Where(n => !IsEntry(n)).Order(StringComparer.Ordinal))
            dropped["file '" + Shown(other) + "'"] = 1;
        var signature = signed == 0 ? "absent" : $"not checked: {signed} block(s) carry a signature over the mesh's canonical form, which this importer does not verify";
        return Write([.. source], entries, skipped, dropped, "mesh-memory", "a Mesh Memory Protocol store", id, version, owner, signingKeyPem, signature);
    }

    // A time the mesh writes as milliseconds since 1970, or as an ISO 8601 instant.
    private static string? Millis(JsonNode? value) => value switch
    {
        JsonValue v when v.TryGetValue<long>(out var ms) && ms > 0 && ms < 32_503_680_000_000 =>
            DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        JsonValue v when v.TryGetValue<double>(out var d) && d > 0 && d < 32_503_680_000_000 =>
            DateTimeOffset.FromUnixTimeMilliseconds((long)d).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        JsonValue v when v.TryGetValue<string>(out var s) => Instant(s),
        _ => null,
    };
}
