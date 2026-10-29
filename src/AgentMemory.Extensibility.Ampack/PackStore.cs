using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentMemory.Packs.Format;

namespace AgentMemory.Extensibility.Ampack;

/// <summary>A pack installed in a <see cref="PackStore"/>: what it is, where it lies, and when it came.</summary>
[Experimental("AMEXT001")]
public sealed record InstalledPack(string Id, string Version, string Digest, string Kind, string File, DateTimeOffset InstalledAt)
{
    /// <summary>For a pack set, the ids of the packs it carries; empty otherwise.</summary>
    public IReadOnlyList<string> Members { get; init; } = [];
}

/// <summary>
/// A host's installed packs in one folder (PACK-toolset, <c>ampack install</c>): <c>packs/&lt;id&gt;@&lt;version&gt;.ampack</c>, an
/// index, and the install ledger, which outlives every removal so an older version or other content under a known version
/// is never installed again. A pack is verified in full before a byte is written, and written whole or not at all. A newer
/// version of an installed id replaces it; a pack set installs as one file, and a composite only inside the set that carries
/// its members. Installing and removing hold the store's lock, so two processes never lose each other's work (review 1, M8).
/// Removing a pack removes its file; records a host already imported from it stay in that host's store until the host
/// removes them (<c>KnowledgePackImporter.RemoveAsync</c>).
/// </summary>
[Experimental("AMEXT001")]
public sealed class PackStore
{
    private const string IndexFile = "installed.json";
    private const string LedgerFile = "ledger.json";
    private const string LockFile = ".lock";
    private const string PacksFolder = "packs";
    private const string Format = "agentmemory.pack.store/1.2";
    private readonly string _root;
    private List<InstalledPack> _installed;

    private PackStore(string root, List<InstalledPack> installed) { _root = root; _installed = installed; }

    /// <summary>The installed packs, by id.</summary>
    public IReadOnlyList<InstalledPack> Installed => [.. _installed.OrderBy(p => p.Id, StringComparer.Ordinal)];

    /// <summary>The store's install ledger, for a host that loads the installed packs and wants the same history held.</summary>
    public PackInstallLedger Ledger => PackInstallLedger.Open(Path.Combine(_root, LedgerFile));

    /// <summary>Opens the store in <paramref name="folder"/>, creating it when it is not there.</summary>
    /// <exception cref="InvalidDataException">The index is malformed.</exception>
    public static PackStore Open(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var root = Path.GetFullPath(folder);
        Directory.CreateDirectory(Path.Combine(root, PacksFolder));
        return new PackStore(root, Read(root));
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
        using var held = Hold();
        // What another process installed meanwhile is read under the lock, and the ledger is checked under it too.
        _installed = Read(_root);
        var ledger = PackInstallLedger.Open(Path.Combine(_root, LedgerFile));
        var (id, version, kind) = PackLoader.Peek(archive);
        string digest;
        IReadOnlyList<string> members = [];
        Action record;
        switch (kind)
        {
            case "set":
                // Allowing a downgrade waives the version history only: one content per version, and every kept revocation, still hold.
                var set = PackFamilies.LoadSet(archive, trust, ledger, allowDowngrade);
                digest = set.Digest;
                members = [.. set.AllPacks.Select(p => p.Id).Where(m => m != id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
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
        // One id is installed once: a pack that a set already carries, or a set carrying an installed pack, is refused (review 1, M8).
        var others = _installed.Where(p => p.Id != id).ToList();
        if (others.FirstOrDefault(p => p.Members.Contains(id, StringComparer.Ordinal)) is { } carrier)
            throw new PackLoadException([$"{id} is installed as part of the set {carrier.Id}: uninstall the set first, or install a newer set."]);
        if (members.FirstOrDefault(m => others.Any(p => p.Id == m || p.Members.Contains(m, StringComparer.Ordinal))) is { } overlap)
            throw new PackLoadException([$"The set {id} carries {overlap}, which is already installed here: uninstall it first."]);
        var file = FileFor(id, version);
        var target = Path.Combine(_root, file);
        var temporary = target + ".partial";
        File.WriteAllBytes(temporary, archive);
        // The ledger first: if it cannot be saved (another run holds it), nothing is installed and no file is left behind.
        try { record(); }
        catch
        {
            File.Delete(temporary);
            throw;
        }
        File.Move(temporary, target, overwrite: true);
        var installed = new InstalledPack(id, version, digest, kind, file, DateTimeOffset.UtcNow) { Members = members };
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
        using var held = Hold();
        _installed = Read(_root);
        var removed = _installed.Where(p => p.Id == id).ToList();
        foreach (var pack in removed) File.Delete(Path.Combine(_root, pack.File));
        _installed.RemoveAll(p => p.Id == id);
        if (removed.Count > 0) Save();
        return removed.Count > 0;
    }

    /// <summary>The installed archives' bytes, by id order, to mount or load (each is verified again by whoever loads it).</summary>
    public IReadOnlyList<byte[]> Archives() => [.. Installed.Select(p => PackReader.ReadArchiveFile(Path.Combine(_root, p.File)))];

    // Where an installed pack lies, always derived from its id and version: an index edited by hand cannot point the store at
    // another file to read or delete (review 1, M8).
    private static string FileFor(string id, string version) => $"{PacksFolder}/{id}@{version}{PackLoader.Extension}";

    private static List<InstalledPack> Read(string root)
    {
        var index = Path.Combine(root, IndexFile);
        var installed = new List<InstalledPack>();
        if (!File.Exists(index)) return installed;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(index));
            if (document.RootElement.GetProperty("format").GetString() != Format) throw new InvalidDataException($"{index} is not a '{Format}' index.");
            foreach (var p in document.RootElement.GetProperty("packs").EnumerateArray())
            {
                var id = p.GetProperty("id").GetString()!;
                var version = p.GetProperty("version").GetString()!;
                if (!PackManifest.IsValidId(id) || !SemVer.TryParse(version, out _)) throw new InvalidDataException($"{index} names a pack that is not a valid id and version.");
                var file = p.GetProperty("file").GetString()!;
                if (file != FileFor(id, version)) throw new InvalidDataException($"{index} places {id} {version} at '{file}', not where the store keeps it.");
                var members = p.TryGetProperty("members", out var list) ? [.. list.EnumerateArray().Select(m => m.GetString()!)] : new List<string>();
                installed.Add(new InstalledPack(id, version, p.GetProperty("digest").GetString()!, p.GetProperty("kind").GetString()!, file,
                    DateTimeOffset.Parse(p.GetProperty("installedAt").GetString()!, CultureInfo.InvariantCulture)) { Members = members });
            }
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException($"{index} is malformed: {e.Message}", e);
        }
        return installed;
    }

    // The store's lock: held for a whole install or removal; another process waits up to a minute, then is told the store is busy.
    private FileStream Hold()
    {
        var path = Path.Combine(_root, LockFile);
        var waited = TimeSpan.Zero;
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (waited < TimeSpan.FromMinutes(1))
            {
                Thread.Sleep(100);
                waited += TimeSpan.FromMilliseconds(100);
            }
        }
    }

    private void Save()
    {
        var index = new JsonObject
        {
            ["format"] = Format,
            ["packs"] = new JsonArray([.. Installed.Select(p =>
            {
                var entry = new JsonObject
                {
                    ["id"] = p.Id, ["version"] = p.Version, ["digest"] = p.Digest, ["kind"] = p.Kind, ["file"] = p.File,
                    ["installedAt"] = p.InstalledAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                };
                if (p.Members.Count > 0) entry["members"] = new JsonArray([.. p.Members.Select(m => (JsonNode)m)]);
                return (JsonNode)entry;
            })]),
        };
        var path = Path.Combine(_root, IndexFile);
        File.WriteAllText(path + ".partial", index.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        File.Move(path + ".partial", path, overwrite: true);
    }
}
