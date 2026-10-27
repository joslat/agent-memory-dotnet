using System.Diagnostics.CodeAnalysis;
using System.Text;
using AgentMemory.Packs.Format;

namespace AgentMemory.Extensibility.Ampack;

/// <summary>One step of verifying a pack: what it checked, whether it held, and the details (as <c>ampack verify --explain</c> shows them).</summary>
[Experimental("AMEXT001")]
public sealed record PackCheck(string Name, bool Passed, IReadOnlyList<string> Lines);

/// <summary>A member of a pack set or a pin of a composite: id, version, kind (for a set member) and digest.</summary>
[Experimental("AMEXT001")]
public sealed record PackPart(string Id, string Version, string? Kind, string Digest);

/// <summary>
/// Everything a pack composer shows about one <c>.ampack</c> (PACK-composer): its identity as its file list states it, every
/// verification step against the host's trust, its declared fields, its members or pins, and, when it verifies, the pack
/// or pack set as a host loads it, mounted in memory to ask and walk. Built from the same code a host runs, so what the
/// composer shows is what a host would do.
/// </summary>
[Experimental("AMEXT001")]
public sealed record PackInspection
{
    /// <summary>The id, version and digest its file list names (null when it has no valid list).</summary>
    public (string Id, string Version, string Digest)? Identity { get; init; }
    /// <summary>The kind <c>pack.json</c> names, unverified.</summary>
    public string? Kind { get; init; }
    /// <summary>Every verification step in order; the last failing one says why it is refused.</summary>
    public IReadOnlyList<PackCheck> Checks { get; init; } = [];
    /// <summary>Whether the pack verified and loaded as a host loads it.</summary>
    public bool Loaded => Problems.Count == 0 && (Pack is not null || Set is not null || Pins.Count > 0);
    /// <summary>Why it did not load, when it did not.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];
    /// <summary>A single pack, loaded.</summary>
    public LoadedPack? Pack { get; init; }
    /// <summary>A pack set, loaded: its families and packs.</summary>
    public LoadedSet? Set { get; init; }
    /// <summary>For a pack set, its members; for a composite, what its lock pins.</summary>
    public IReadOnlyList<PackPart> Pins { get; init; } = [];
    /// <summary>The signers that counted: role, key id and claimed time.</summary>
    public IReadOnlyList<string> Signers { get; init; } = [];
    /// <summary>The packs mounted in memory (knowledge packs and snapshots with their definitions), to ask and walk; null when none.</summary>
    public PackMount? Mount { get; init; }
    /// <summary>The declaration files as text, by path (pack.json, ontology, views, traversal, trial, tests, members, lock).</summary>
    public IReadOnlyDictionary<string, string> Declarations { get; init; } = new Dictionary<string, string>();

    /// <summary>Inspects an <c>.ampack</c> against a host's trust. Never throws for a bad pack: the checks and problems say why.</summary>
    public static PackInspection Inspect(byte[] archive, PackTrust trust)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(trust);
        PackContent content;
        try { content = PackReader.ReadArchive(archive, "archive"); }
        catch (InvalidDataException e) { return new PackInspection { Checks = [new("archive", false, [e.Message])], Problems = [e.Message] }; }
        var checks = new List<PackCheck> { new("archive", true, [$"strict ZIP profile; {content.Files.Count} entries"]) };
        var verification = PackVerifier.Verify(content, trust.Policy, DateTimeOffset.UtcNow, explain: step => checks.Add(new(step.Name, step.Passed, step.Lines)));
        (string, string, string)? identity = content.Files.TryGetValue(PackPaths.FilesList, out var listBytes) && FilesList.Parse(listBytes).List is { } list
            ? (list.Id, list.Version, FilesList.DigestOf(listBytes)) : null;
        var kind = content.Files.TryGetValue(PackManifest.FileName, out var manifestBytes) ? PackManifest.Parse(manifestBytes).Manifest?.Kind : null;
        var declarations = content.Files.Where(f => f.Key.EndsWith(".json", StringComparison.Ordinal) && FilesList.IsListed(f.Key)
                && !f.Key.StartsWith("records/", StringComparison.Ordinal) && !f.Key.StartsWith("packs/", StringComparison.Ordinal))
            .OrderBy(f => f.Key, StringComparer.Ordinal).ToDictionary(f => f.Key, f => Encoding.UTF8.GetString(f.Value), StringComparer.Ordinal);
        var inspection = new PackInspection
        {
            Identity = identity, Kind = kind, Checks = checks, Declarations = declarations,
            Signers = verification.Pack?.Signatures.Select(s => $"{s.Role} · key {s.KeyId[..16]} · {s.SignedAt:u}").ToList() ?? [],
        };
        if (!verification.IsVerified) return inspection with { Problems = verification.Problems };
        try
        {
            switch (kind)
            {
                case "set":
                    var set = PackFamilies.LoadSet(archive, trust);
                    var members = set.AllPacks;
                    return inspection with
                    {
                        Set = set, Mount = MountOrNull(archive, trust),
                        Pins = [.. set.AllFamilies.Select(f => new PackPart(f.Id, f.Version, "composite", f.Digest)), .. members.Select(m => new PackPart(m.Id, m.Version, m.Kind, m.Digest))],
                    };
                case "composite":
                    var (entries, _) = PackLock.Parse(content.Files[PackPaths.LockFile]);
                    return inspection with { Pins = [.. (entries ?? []).Select(e => new PackPart(e.Id, e.Version, null, e.Digest))] };
                default:
                    var pack = PackLoader.Load(archive, trust);
                    return inspection with { Pack = pack, Mount = pack.Kind == "knowledge" ? MountOrNull(archive, trust) : null };
            }
        }
        catch (PackLoadException e) { return inspection with { Problems = e.Problems }; }
    }

    // Knowledge packs and snapshots with their definitions, in memory; a definition alone has nothing to ask.
    private static PackMount? MountOrNull(byte[] archive, PackTrust trust)
    {
        try { return PackMemory.Mount([archive], trust) is { Memory.Count: > 0 } mount ? mount : null; }
        catch (PackLoadException) { return null; }
    }
}
