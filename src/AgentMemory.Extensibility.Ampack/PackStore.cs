using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentMemory.Packs.Format;

namespace AgentMemory.Extensibility.Ampack;

/// <summary>A pack installed in a <see cref="PackStore"/>: what it is, where it lies, and when it came.</summary>
[Experimental("AMEXT001")]
public sealed record InstalledPack(string Id, string Version, string Digest, string Kind, string File, DateTimeOffset InstalledAt);

/// <summary>
/// A host's installed packs in one folder (PACK-toolset, <c>ampack install</c>): <c>packs/&lt;id&gt;@&lt;version&gt;.ampack</c>, an
/// index, and the install ledger, which outlives every removal so an older version or other content under a known version
/// is never installed again. A pack is verified in full before a byte is written, and written whole or not at all. A newer
/// version of an installed id replaces it; a pack set installs as one file, and a composite only inside the set that carries
/// its members.
/// </summary>
[Experimental("AMEXT001")]
public sealed class PackStore
{
    private const string IndexFile = "installed.json";
    private const string LedgerFile = "ledger.json";
    private const string PacksFolder = "packs";
    private const string Format = "agentmemory.pack.store/1.2";
    private readonly string _root;
    private readonly List<InstalledPack> _installed;

    private PackStore(string root, List<InstalledPack> installed) { _root = root; _installed = installed; }

    /// <summary>The installed packs, by id.</summary>
    public IReadOnlyList<InstalledPack> Installed => [.. _installed.OrderBy(p => p.Id, StringComparer.Ordinal)];

    /// <summary>Opens the store in <paramref name="folder"/>, creating it when it is not there.</summary>
    /// <exception cref="InvalidDataException">The index is malformed.</exception>
    public static PackStore Open(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var root = Path.GetFullPath(folder);
        Directory.CreateDirectory(Path.Combine(root, PacksFolder));
        var index = Path.Combine(root, IndexFile);
        var installed = new List<InstalledPack>();
        if (File.Exists(index))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(index));
                if (document.RootElement.GetProperty("format").GetString() != Format) throw new InvalidDataException($"{index} is not a '{Format}' index.");
                foreach (var p in document.RootElement.GetProperty("packs").EnumerateArray())
                    installed.Add(new InstalledPack(p.GetProperty("id").GetString()!, p.GetProperty("version").GetString()!, p.GetProperty("digest").GetString()!,
                        p.GetProperty("kind").GetString()!, p.GetProperty("file").GetString()!,
                        DateTimeOffset.Parse(p.GetProperty("installedAt").GetString()!, CultureInfo.InvariantCulture)));
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                throw new InvalidDataException($"{index} is malformed: {e.Message}", e);
            }
        }
        return new PackStore(root, installed);
    }

    /// <summary>
    /// Verifies and installs an <c>.ampack</c> (a pack or a pack set): the ledger refuses an older version or other content
    /// under a version it knows; a newer version replaces the installed one. Nothing is written when anything is refused.
    /// </summary>
    /// <exception cref="PackLoadException">The pack is refused, with every reason, or is a composite on its own.</exception>
    public InstalledPack Install(byte[] archive, PackTrust trust, bool allowDowngrade = false)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(trust);
        var ledger = PackInstallLedger.Open(Path.Combine(_root, LedgerFile));
        var (id, version, kind) = PackLoader.Peek(archive);
        string digest;
        Action record;
        switch (kind)
        {
            case "set":
                var set = PackFamilies.LoadSet(archive, trust, allowDowngrade ? null : ledger);
                digest = set.Digest;
                record = () => ledger.Record(set);
                break;
            case "composite":
                throw new PackLoadException([$"{id} is a composite: it pins its members but does not carry them. Install the pack set that carries both."]);
            default:
                var pack = PackLoader.Load(archive, trust, ledger, allowDowngrade);
                digest = pack.Digest;
                record = () => ledger.Record(pack);
                break;
        }
        var file = Path.Combine(PacksFolder, $"{id}@{version}{PackLoader.Extension}");
        var target = Path.Combine(_root, file);
        var temporary = target + ".partial";
        File.WriteAllBytes(temporary, archive);
        File.Move(temporary, target, overwrite: true);
        record();
        var installed = new InstalledPack(id, version, digest, kind, file.Replace('\\', '/'), DateTimeOffset.UtcNow);
        foreach (var replaced in _installed.Where(p => p.Id == id && p.File != installed.File).ToList())
            File.Delete(Path.Combine(_root, replaced.File));
        _installed.RemoveAll(p => p.Id == id);
        _installed.Add(installed);
        Save();
        return installed;
    }

    /// <summary>Removes an installed pack; the ledger keeps it, so the same or an older version cannot be installed again unnoticed.</summary>
    /// <returns>Whether the id was installed.</returns>
    public bool Uninstall(string id)
    {
        var removed = _installed.Where(p => p.Id == id).ToList();
        foreach (var pack in removed) File.Delete(Path.Combine(_root, pack.File));
        _installed.RemoveAll(p => p.Id == id);
        if (removed.Count > 0) Save();
        return removed.Count > 0;
    }

    /// <summary>The installed archives' bytes, by id order, to mount or load (each is verified again by whoever loads it).</summary>
    public IReadOnlyList<byte[]> Archives() => [.. Installed.Select(p => PackReader.ReadArchiveFile(Path.Combine(_root, p.File)))];

    private void Save()
    {
        var index = new JsonObject
        {
            ["format"] = Format,
            ["packs"] = new JsonArray([.. Installed.Select(p => (JsonNode)new JsonObject
            {
                ["id"] = p.Id, ["version"] = p.Version, ["digest"] = p.Digest, ["kind"] = p.Kind, ["file"] = p.File,
                ["installedAt"] = p.InstalledAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            })]),
        };
        var path = Path.Combine(_root, IndexFile);
        File.WriteAllText(path + ".partial", index.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        File.Move(path + ".partial", path, overwrite: true);
    }
}
