// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentMemory.Packs.Format;

namespace AgentMemory.Extensibility.Ampack;

/// <summary>What a change did to one declared value: added, removed, or changed from one value to another.</summary>
[Experimental("AMEXT001")]
public enum PackChange
{
    /// <summary>Only in the second pack.</summary>
    Added,
    /// <summary>Only in the first pack.</summary>
    Removed,
    /// <summary>In both, different.</summary>
    Changed,
}

/// <summary>One declared value that differs: the file, the path inside it (named items by name), and the two values as JSON.</summary>
[Experimental("AMEXT001")]
public sealed record PackDeclarationChange(string File, string Path, PackChange Change, string? Before, string? After);

/// <summary>One record that differs, by type and key; for a changed one, the properties that changed and whether its links did.</summary>
[Experimental("AMEXT001")]
public sealed record PackRecordChange(string Type, string Key, PackChange Change, IReadOnlyList<string> Properties, bool LinksChanged);

/// <summary>One file that differs, with its lengths.</summary>
[Experimental("AMEXT001")]
public sealed record PackFileChange(string Path, PackChange Change, long? Before, long? After);

/// <summary>
/// The difference between two packs (PACK-toolset, <c>ampack diff</c>): identity, every declared value (pack.json, ontology,
/// views, traversal, trial, tests, members, lock), every record by type and key, and every file. It reads both packs under the
/// strict profile but trusts neither: verify them for that.
/// </summary>
[Experimental("AMEXT001")]
public sealed record PackDiff
{
    /// <summary>The first pack's id, version and digest, as its file list names them.</summary>
    public required (string Id, string Version, string Digest) Before { get; init; }
    /// <summary>The second pack's.</summary>
    public required (string Id, string Version, string Digest) After { get; init; }
    /// <summary>Every declared value that differs, in file and path order.</summary>
    public IReadOnlyList<PackDeclarationChange> Declarations { get; init; } = [];
    /// <summary>Every record that differs, in type and key order.</summary>
    public IReadOnlyList<PackRecordChange> Records { get; init; } = [];
    /// <summary>Records in both, unchanged.</summary>
    public int UnchangedRecords { get; init; }
    /// <summary>Every file that differs, in path order.</summary>
    public IReadOnlyList<PackFileChange> Files { get; init; } = [];
    /// <summary>Whether the two are the same pack: the same digest.</summary>
    public bool Same => Before.Digest == After.Digest;

    /// <summary>
    /// Compares two pack folders or <c>.ampack</c> files, read as they are: neither is verified, so neither may hold more than
    /// verification would parse (a JSON file or a JSONL line at most 1 MiB, all of it at most 64 MiB; review 7, D2).
    /// </summary>
    /// <exception cref="PackLoadException">A pack cannot be read under the strict profile, has no file list, or is larger
    /// than an unverified pack may be.</exception>
    public static PackDiff Compare(string before, string after) => Compare(Read(before), Read(after), verified: false);

    /// <summary>Compares two <c>.ampack</c> archives held in memory, read as they are (see <see cref="Compare(string, string)"/>).</summary>
    public static PackDiff Compare(byte[] before, byte[] after) => Compare(Archive(before), Archive(after), verified: false);

    /// <summary>
    /// Compares the files of two packs already read once; <paramref name="verified"/> when the caller verified those very
    /// bytes, so only verification's own bounds apply (the tool reads each pack once, verifies it and compares it).
    /// </summary>
    internal static PackDiff Compare(IReadOnlyDictionary<string, byte[]> a, IReadOnlyDictionary<string, byte[]> b, bool verified)
    {
        if (PackContentBounds.Problems(a, verified) is { Count: > 0 } first) throw new PackLoadException(first);
        if (PackContentBounds.Problems(b, verified) is { Count: > 0 } second) throw new PackLoadException(second);
        var declarations = new List<PackDeclarationChange>();
        foreach (var file in a.Keys.Union(b.Keys).Where(IsDeclaration).Order(StringComparer.Ordinal))
        {
            var before = Flatten(a.GetValueOrDefault(file));
            var after = Flatten(b.GetValueOrDefault(file));
            foreach (var path in before.Keys.Union(after.Keys).Order(StringComparer.Ordinal))
            {
                var x = before.GetValueOrDefault(path);
                var y = after.GetValueOrDefault(path);
                if (x == y) continue;
                declarations.Add(new(file, path, x is null ? PackChange.Added : y is null ? PackChange.Removed : PackChange.Changed, x, y));
            }
        }
        var recordsBefore = ReadRecords(a);
        var recordsAfter = ReadRecords(b);
        var records = new List<PackRecordChange>();
        var unchanged = 0;
        foreach (var id in recordsBefore.Keys.Union(recordsAfter.Keys).OrderBy(k => k.Type, StringComparer.Ordinal).ThenBy(k => k.Key, StringComparer.Ordinal))
        {
            var had = recordsBefore.TryGetValue(id, out var x);
            var has = recordsAfter.TryGetValue(id, out var y);
            if (had && has)
            {
                var properties = x!.Properties.Keys.Union(y!.Properties.Keys).Where(p => x.Properties.GetValueOrDefault(p) != y.Properties.GetValueOrDefault(p))
                    .Order(StringComparer.Ordinal).ToList();
                var links = !x.Links.SetEquals(y.Links);
                if (properties.Count == 0 && !links) { unchanged++; continue; }
                records.Add(new(id.Type, id.Key, PackChange.Changed, properties, links));
            }
            else records.Add(new(id.Type, id.Key, had ? PackChange.Removed : PackChange.Added, [], false));
        }
        var files = a.Keys.Union(b.Keys).Where(FilesList.IsListed).Order(StringComparer.Ordinal)
            .Select(path => (path, x: a.GetValueOrDefault(path), y: b.GetValueOrDefault(path)))
            .Where(f => f.x is null || f.y is null || !f.x.AsSpan().SequenceEqual(f.y))
            .Select(f => new PackFileChange(f.path, f.x is null ? PackChange.Added : f.y is null ? PackChange.Removed : PackChange.Changed, f.x?.LongLength, f.y?.LongLength))
            .ToList();
        return new PackDiff
        {
            Before = Identity(a), After = Identity(b), Declarations = declarations, Records = records, UnchangedRecords = unchanged, Files = files,
        };
    }

    // The declaration files: every JSON file outside records, assets, member folders and signatures.
    private static bool IsDeclaration(string path) =>
        path.EndsWith(".json", StringComparison.Ordinal) && FilesList.IsListed(path)
        && !path.StartsWith("records/", StringComparison.Ordinal) && !path.StartsWith("assets/", StringComparison.Ordinal)
        && !path.StartsWith("packs/", StringComparison.Ordinal);

    // A JSON file as path -> value: objects by member, arrays of named items by name (or id), other arrays whole.
    private static Dictionary<string, string> Flatten(byte[]? bytes)
    {
        var flat = new Dictionary<string, string>(StringComparer.Ordinal);
        if (bytes is null) return flat;
        JsonNode? root;
        try { root = JsonNode.Parse(bytes); }
        catch (JsonException) { flat["(file)"] = Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant() + " (not JSON)"; return flat; }
        Walk(root, "", flat);
        return flat;

        static void Walk(JsonNode? node, string path, Dictionary<string, string> into)
        {
            switch (node)
            {
                case JsonObject o when o.Count > 0:
                    foreach (var (key, value) in o) Walk(value, path.Length == 0 ? key : path + "." + key, into);
                    break;
                case JsonArray a when a.Count > 0 && a.All(x => x is JsonObject item && Name(item) is not null)
                                      && a.Select(x => Name((JsonObject)x!)).Distinct().Count() == a.Count:
                    foreach (var item in a.Cast<JsonObject>()) Walk(item, $"{path}[{Name(item)}]", into);
                    break;
                default:
                    into[path.Length == 0 ? "(value)" : path] = node?.ToJsonString() ?? "null";
                    break;
            }
        }

        static string? Name(JsonObject item) =>
            (item["name"] ?? item["id"]) is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }

    private sealed record Record(Dictionary<string, string> Properties, HashSet<string> Links);

    // Every record line, by type and key: its properties as canonical text, its links as relation>target.
    private static Dictionary<(string Type, string Key), Record> ReadRecords(IReadOnlyDictionary<string, byte[]> files)
    {
        var records = new Dictionary<(string, string), Record>();
        foreach (var file in PackLayout.RecordFiles(files.Keys))
            foreach (var line in Encoding.UTF8.GetString(files[file]).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                JsonObject? record;
                try { record = JsonNode.Parse(line) as JsonObject; }
                catch (JsonException) { continue; }
                if (record?["type"] is not JsonValue type || !type.TryGetValue<string>(out var t)
                    || record["key"] is not JsonValue key || !key.TryGetValue<string>(out var k)) continue;
                var properties = (record["properties"] as JsonObject ?? []).ToDictionary(p => p.Key, p => p.Value?.ToJsonString() ?? "null", StringComparer.Ordinal);
                var links = (record["links"] as JsonArray ?? []).OfType<JsonObject>()
                    .Select(l => $"{l["relation"]?.ToString()}>{l["target"]?.ToString()}").ToHashSet(StringComparer.Ordinal);
                records[(t, k)] = new Record(properties, links);
            }
        return records;
    }

    private static (string, string, string) Identity(IReadOnlyDictionary<string, byte[]> files)
    {
        if (!files.TryGetValue(PackPaths.FilesList, out var bytes) || FilesList.Parse(bytes).List is not { } list)
            throw new PackLoadException([$"There is no valid {PackPaths.FilesList}: build the pack first."]);
        return (list.Id, list.Version, FilesList.DigestOf(bytes));
    }

    private static IReadOnlyDictionary<string, byte[]> Read(string path)
    {
        try { return PackReader.Read(path).Files; }
        catch (Exception e) when (e is InvalidDataException or IOException) { throw new PackLoadException([$"{path}: {e.Message}"]); }
    }

    private static IReadOnlyDictionary<string, byte[]> Archive(byte[] archive)
    {
        try { return PackReader.ReadArchive(archive, "archive").Files; }
        catch (InvalidDataException e) { throw new PackLoadException([e.Message]); }
    }
}
