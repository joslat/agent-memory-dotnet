using System.Diagnostics.CodeAnalysis;
using AgentMemory.Extensibility.Bundles;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Packs.Format;

namespace AgentMemory.Extensibility.Ampack;

/// <summary>The keys a host trusts to sign packs, each scoped to pack id prefixes and roles (pack format v1.1 H3).</summary>
[Experimental("AMEXT001")]
public sealed class PackTrust
{
    private PackTrust(TrustPolicy policy) => Policy = policy;

    internal TrustPolicy Policy { get; }

    /// <summary>Reads a trust policy (<c>agentmemory.pack.trust/1.1</c>).</summary>
    /// <exception cref="InvalidDataException">The policy is malformed, or a key id is not the hash of its key.</exception>
    public static PackTrust Parse(byte[] json) => new(TrustPolicy.Parse(json ?? throw new ArgumentNullException(nameof(json))));

    /// <summary>Reads a trust policy file.</summary>
    public static PackTrust Load(string path) => Parse(File.ReadAllBytes(path));

    /// <summary>Trusts nothing: every pack is refused. There is no setting that admits unsigned packs.</summary>
    public static PackTrust None { get; } = new(new TrustPolicy());
}

/// <summary>
/// The host's record of every pack it installed, which survives removal, so a pack cannot be rolled back or swapped for
/// other content under a version it already had (pack format v1.1 H4).
/// </summary>
[Experimental("AMEXT001")]
public sealed class PackInstallLedger
{
    private readonly string _path;
    private PackInstallLedger(string path, HostLedger ledger) { _path = path; Ledger = ledger; }

    internal HostLedger Ledger { get; }

    /// <summary>Opens the ledger file, or starts an empty one there.</summary>
    public static PackInstallLedger Open(string path) => new(path, HostLedger.Load(path));

    /// <summary>Records an installed pack and saves the ledger.</summary>
    public void Record(LoadedPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        Ledger.Record(pack.Id, pack.Version, pack.Digest);
        Ledger.Save(_path);
    }
}

/// <summary>A verified pack, mapped onto the module model.</summary>
[Experimental("AMEXT001")]
public sealed record LoadedPack
{
    /// <summary>The pack id.</summary>
    public required string Id { get; init; }
    /// <summary>Its version.</summary>
    public required string Version { get; init; }
    /// <summary>Its identity: <c>sha256:</c> over the exact bytes of its file list.</summary>
    public required string Digest { get; init; }
    /// <summary><c>knowledge</c>, <c>definition</c> or <c>snapshot</c>.</summary>
    public required string Kind { get; init; }
    /// <summary>The module manifest the pack maps to, validated.</summary>
    public required ModuleManifest Manifest { get; init; }
    /// <summary>For a knowledge pack, the pack with its records, ready to compose and import; otherwise null.</summary>
    public KnowledgePack? Knowledge { get; init; }
    /// <summary>Who signed it: role and key id, each verified against the host's trust.</summary>
    public IReadOnlyList<string> Signers { get; init; } = [];
}

/// <summary>A pack that cannot be loaded, with every reason.</summary>
[Experimental("AMEXT001")]
public sealed class PackLoadException : InvalidOperationException
{
    /// <summary>Creates the exception with its problems.</summary>
    public PackLoadException(IReadOnlyList<string> problems)
        : base("The memory pack cannot be loaded:" + Environment.NewLine + string.Join(Environment.NewLine, (problems ?? []).Select(p => "- " + p))) =>
        Problems = problems ?? [];

    /// <summary>Every problem found.</summary>
    public IReadOnlyList<string> Problems { get; }
}

/// <summary>
/// Loads memory packs into a host (pack format v1.2 §4): read the folder or the <c>.ampack</c> under the strict archive
/// profile; verify integrity, signatures, the trust policy and the ledger; map the v1.2 layout onto a module manifest and
/// validate it; for a knowledge pack, check every record. Nothing reaches a store before all of that holds.
/// </summary>
[Experimental("AMEXT001")]
public static class PackLoader
{
    /// <summary>The file extension of the single-file form.</summary>
    public const string Extension = ".ampack";

    /// <summary>Loads a pack folder or an <c>.ampack</c> file.</summary>
    /// <exception cref="PackLoadException">The pack is refused; every reason is listed.</exception>
    public static LoadedPack Load(string path, PackTrust trust, PackInstallLedger? ledger = null, bool allowDowngrade = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        PackContent content;
        try { content = PackReader.Read(path); }
        catch (Exception e) when (e is InvalidDataException or IOException) { throw new PackLoadException([e.Message]); }
        return Load(content, trust, ledger, allowDowngrade);
    }

    /// <summary>Loads an <c>.ampack</c> read into memory.</summary>
    public static LoadedPack Load(byte[] archive, PackTrust trust, PackInstallLedger? ledger = null, bool allowDowngrade = false)
    {
        ArgumentNullException.ThrowIfNull(archive);
        PackContent content;
        try { content = PackReader.ReadArchive(archive, "archive"); }
        catch (InvalidDataException e) { throw new PackLoadException([e.Message]); }
        return Load(content, trust, ledger, allowDowngrade);
    }

    internal static LoadedPack Load(PackContent content, PackTrust trust, PackInstallLedger? ledger, bool allowDowngrade)
    {
        ArgumentNullException.ThrowIfNull(trust);
        var verification = PackVerifier.Verify(content, trust.Policy, DateTimeOffset.UtcNow, ledger?.Ledger, allowDowngrade);
        if (verification.Pack is not { } pack) throw new PackLoadException(verification.Problems);

        ModuleImage image;
        try { image = PackLayout.ToModule(pack.Files); }
        catch (InvalidDataException e) { throw new PackLoadException([e.Message]); }
        ModuleManifest manifest;
        try { manifest = ModuleManifest.Parse(System.Text.Encoding.UTF8.GetString(image.Manifest)); }
        catch (FormatException e) { throw new PackLoadException([e.Message]); }
        if (manifest.Validate() is { Count: > 0 } invalid) throw new PackLoadException(invalid);

        var signers = pack.Signatures.Select(s => $"{s.Role} {s.KeyId[..16]}").ToList();
        KnowledgePack? knowledge = null;
        if (pack.Manifest.Kind == "knowledge")
        {
            // The SDK's knowledge-pack checks, on the bytes this loader verified: the mapped manifest, the records and the icon.
            var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal) { [KnowledgePack.ManifestFile] = image.Manifest };
            foreach (var record in image.Seed) files[record] = pack.Files[record];
            if (image.Icon is { } icon && pack.Files.TryGetValue(icon, out var iconBytes)) files[icon] = iconBytes;
            var bundle = new VerifiedBundle
            {
                Id = pack.Id, Version = pack.Version, Digest = pack.Digest, KeyId = pack.Signatures[0].KeyId, Signer = BundleSigner.Developer,
                Files = files,
            };
            try { knowledge = KnowledgePack.Open(bundle); }
            catch (KnowledgePackException e) { throw new PackLoadException(e.Problems); }
        }
        else if (pack.Manifest.Kind == "snapshot")
            throw new PackLoadException(["Snapshot packs load through their definition's importer; that path is not built yet (PACK-migrate)."]);

        return new LoadedPack
        {
            Id = pack.Id, Version = pack.Version, Digest = pack.Digest, Kind = pack.Manifest.Kind, Manifest = manifest,
            Knowledge = knowledge, Signers = signers,
        };
    }

    /// <summary>
    /// Composes a loaded pack: a knowledge pack with a search contributor per view (its records import with
    /// <see cref="KnowledgePackImporter"/>); a definition pack as a data-only module whose records the host's importer
    /// writes. A pack that declares a trial still needs a passing decision when the host requires trials.
    /// </summary>
    public static ModuleSetBuilder AddPack(this ModuleSetBuilder builder, LoadedPack pack, int priority = 100)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(pack);
        return pack.Knowledge is { } knowledge ? builder.AddKnowledgePack(knowledge, priority) : builder.Add(pack.Manifest, _ => { });
    }
}
