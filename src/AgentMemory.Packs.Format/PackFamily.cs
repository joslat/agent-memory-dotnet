using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentMemory.Packs.Format;

/// <summary>One pinned member of a composite.</summary>
internal sealed record LockEntry(string Id, string Version, string Digest);

/// <summary>
/// <c>META/lock.json</c> (pack format v1.2 §5): the exact id, version and digest of every member a composite installs,
/// canonical, listed in the composite's file list and so covered by its signature.
/// </summary>
internal static class PackLock
{
    internal const string Format = "agentmemory.pack.lock/1.1";

    internal static byte[] Build(IEnumerable<LockEntry> members)
    {
        var list = members.OrderBy(m => m.Id, StringComparer.Ordinal).ToList();
        if (list.Count != list.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count())
            throw new InvalidDataException("Two members share an id: a lock pins exactly one version of each pack.");
        var array = new JsonArray([.. list.Select(m => (JsonNode)new JsonObject { ["id"] = m.Id, ["version"] = m.Version, ["digest"] = m.Digest })]);
        return Jcs.Canonical(new JsonObject { ["format"] = Format, ["members"] = array });
    }

    internal static (IReadOnlyList<LockEntry>? Entries, string? Problem) Parse(ReadOnlySpan<byte> bytes)
    {
        if (Jcs.CheckCanonical(bytes) is { } problem) return (null, $"{PackPaths.LockFile} {problem}.");
        using var document = JsonDocument.Parse(bytes.ToArray());
        var root = document.RootElement;
        if (!root.TryGetProperty("format", out var format) || format.GetString() != Format || !root.TryGetProperty("members", out var members)
            || members.ValueKind != JsonValueKind.Array || root.EnumerateObject().Any(p => p.Name is not ("format" or "members")))
            return (null, $"{PackPaths.LockFile} is not a '{Format}' lock: format and members, nothing else.");
        var entries = new List<LockEntry>();
        foreach (var m in members.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object || m.EnumerateObject().Any(p => p.Name is not ("id" or "version" or "digest"))
                || !m.TryGetProperty("id", out var id) || !m.TryGetProperty("version", out var version) || !m.TryGetProperty("digest", out var digest)
                || id.GetString() is not { } i || !PackManifest.IsValidId(i) || version.GetString() is not { } v || !SemVer.TryParse(v, out _)
                || digest.GetString() is not { } d || !d.StartsWith("sha256:", StringComparison.Ordinal) || d.Length != 71)
                return (null, $"{PackPaths.LockFile} has a malformed member.");
            entries.Add(new LockEntry(i, v, d));
        }
        if (entries.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() != entries.Count)
            return (null, $"{PackPaths.LockFile} pins one id twice.");
        return (entries, null);
    }
}

/// <summary>
/// A pack set (pack format v1.2 §5): complete packs side by side under <c>packs/&lt;id&gt;/</c>, each with its own file list
/// and signatures, and the set's own <c>pack.json</c> and <c>META/</c> over everything. Never an archive inside an archive.
/// </summary>
internal static class PackSets
{
    internal const string Folder = "packs/";

    /// <summary>The members of a set, by the folder they sit in, with their paths made relative to it.</summary>
    internal static Dictionary<string, Dictionary<string, byte[]>> Members(IReadOnlyDictionary<string, byte[]> files)
    {
        var members = new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.Ordinal);
        foreach (var (path, bytes) in files.Where(f => f.Key.StartsWith(Folder, StringComparison.Ordinal)))
        {
            var rest = path[Folder.Length..];
            var slash = rest.IndexOf('/');
            if (slash <= 0) throw new InvalidDataException($"'{path}' is not inside a member folder.");
            var id = rest[..slash];
            if (!members.TryGetValue(id, out var member)) members[id] = member = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            member[rest[(slash + 1)..]] = bytes;
        }
        return members;
    }

    /// <summary>The source files of a set that carries <paramref name="members"/> (each a complete, built pack).</summary>
    internal static Dictionary<string, byte[]> Source(string id, string version, IReadOnlyDictionary<string, IReadOnlyDictionary<string, byte[]>> members)
    {
        var pack = new JsonObject
        {
            ["format"] = PackManifest.Format, ["id"] = id, ["version"] = version, ["kind"] = "set",
            ["contains"] = new JsonArray([.. members.Keys.Order(StringComparer.Ordinal).Select(k => (JsonNode)k)]),
            ["tier"] = "open", ["protection"] = "open", ["code"] = "E0",
        };
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [PackManifest.FileName] = PackLayout.Write(pack) };
        foreach (var (memberId, memberFiles) in members)
            foreach (var (path, bytes) in memberFiles) files[Folder + memberId + "/" + path] = bytes;
        return files;
    }
}
