using System.Diagnostics.CodeAnalysis;
using AgentMemory.Extensibility.Bundles;
using AgentMemory.Extensibility.Ingestion;
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

    /// <summary>
    /// This trust with a signed revocation document applied (pack format v1.2 §8, PACK-harden): signed by a key this policy
    /// names as <c>revoker</c>, its revoked digests and keys are added, and its expiry becomes the policy's, after which
    /// nothing verifies until a fresh document is applied.
    /// </summary>
    /// <exception cref="InvalidDataException">The document is malformed, not signed by a trusted revoker valid now, or issued in the future.</exception>
    public PackTrust WithRevocations(byte[] signedDocument) =>
        new(Revocations.Apply(Policy, signedDocument ?? throw new ArgumentNullException(nameof(signedDocument)), DateTimeOffset.UtcNow));
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

    /// <summary>Records an installed composite and every member, and saves the ledger.</summary>
    public void Record(LoadedFamily family)
    {
        ArgumentNullException.ThrowIfNull(family);
        Ledger.Record(family.Id, family.Version, family.Digest);
        foreach (var member in family.Members) Ledger.Record(member.Id, member.Version, member.Digest);
        Ledger.Save(_path);
    }

    /// <summary>Records an installed pack set, its families and its packs, and saves the ledger: an older set is then refused.</summary>
    public void Record(LoadedSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        Ledger.Record(set.Id, set.Version, set.Digest);
        foreach (var family in set.Families)
        {
            Ledger.Record(family.Id, family.Version, family.Digest);
            foreach (var member in family.Members) Ledger.Record(member.Id, member.Version, member.Digest);
        }
        foreach (var pack in set.Packs) Ledger.Record(pack.Id, pack.Version, pack.Digest);
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
    /// <summary>
    /// For a snapshot pack, what its records were taken from: <c>git:</c> and the commit its <c>pack.json</c> names, otherwise
    /// <c>pack:</c> and its version; otherwise null.
    /// </summary>
    public string? SnapshotSource { get; init; }
    /// <summary>The pack's own tests (<c>evals/tests.json</c>), verified with it; null when it has none. Run with <see cref="PackTests"/>.</summary>
    public ReadOnlyMemory<byte>? Tests { get; init; }
    /// <summary>When the pack declares <c>validUntil</c>: after it a host switches the pack off, at load and at runtime.</summary>
    public DateTimeOffset? ValidUntil { get; init; }
    /// <summary>For a snapshot pack, its verified record files, read by <see cref="PackLoader.ToSnapshot"/>.</summary>
    internal IReadOnlyDictionary<string, ReadOnlyMemory<byte>> RecordFiles { get; init; } = new Dictionary<string, ReadOnlyMemory<byte>>();
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

    /// <summary>
    /// An archive's id, version and kind as its <c>pack.json</c> states them, <b>unverified</b>: to choose how to load it
    /// (<see cref="Load(byte[], PackTrust, PackInstallLedger?, bool)"/>, <see cref="PackFamilies.LoadSet(byte[], PackTrust, PackInstallLedger?)"/>
    /// or <see cref="PackFamilies.LoadComposite"/>), never to trust it. The chosen path verifies everything.
    /// </summary>
    /// <exception cref="PackLoadException">The archive is not readable under the strict profile, or has no valid pack.json.</exception>
    public static (string Id, string Version, string Kind) Peek(byte[] archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        PackContent content;
        try { content = PackReader.ReadArchive(archive, "archive"); }
        catch (InvalidDataException e) { throw new PackLoadException([e.Message]); }
        if (!content.Files.TryGetValue(PackManifest.FileName, out var bytes)) throw new PackLoadException([$"There is no {PackManifest.FileName}."]);
        var (manifest, problem) = PackManifest.Parse(bytes);
        return manifest is null ? throw new PackLoadException([problem!]) : (manifest.Id, manifest.Version, manifest.Kind);
    }

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
        if (Unenforceable(pack) is { Count: > 0 } unenforceable) throw new PackLoadException(unenforceable);

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
        string? snapshotSource = null;
        var recordFiles = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        if (pack.Manifest.Kind == "snapshot")
        {
            // A snapshot's records are checked against its definition's ontology in ToSnapshot, before any import.
            if (manifest.Modules.Count != 1)
                throw new PackLoadException([$"A snapshot pack requires exactly the one definition its records belong to; {pack.Id} requires {manifest.Modules.Count}."]);
            snapshotSource = pack.Manifest.Source?.Commit is { } commit ? "git:" + commit : "pack:" + pack.Version;
            foreach (var file in PackLayout.RecordFiles(pack.Files.Keys)) recordFiles[file] = pack.Files[file];
        }

        return new LoadedPack
        {
            Id = pack.Id, Version = pack.Version, Digest = pack.Digest, Kind = pack.Manifest.Kind, Manifest = manifest,
            Knowledge = knowledge, Signers = signers, SnapshotSource = snapshotSource, RecordFiles = recordFiles, ValidUntil = pack.Manifest.ValidUntil,
            Tests = pack.Files.TryGetValue(PackTests.FileName, out var tests) ? tests : null,
        };
    }

    // What a pack declares that this host cannot enforce yet refuses it, rather than loading it as if it were open (PACK-review
    // 4.8): sealed content, code tiers above E0, and usage policies. Each is accepted once it is enforced.
    private static List<string> Unenforceable(VerifiedPack pack)
    {
        var problems = new List<string>();
        if (pack.Manifest.Protection != "open")
            problems.Add($"{pack.Id} is protected as '{pack.Manifest.Protection}'; this host opens 'open' packs only.");
        if (pack.Manifest.Code != "E0" || pack.Files.Keys.Any(f => f is "queries.json" or "components.json"
                || f.StartsWith("queries/", StringComparison.Ordinal) || f.StartsWith("components/", StringComparison.Ordinal)))
            problems.Add($"{pack.Id} carries code (tier {pack.Manifest.Code}); this host runs declarative (E0) packs only.");
        if (pack.Files.ContainsKey("policy.json"))
            problems.Add($"{pack.Id} carries a usage policy (policy.json); this host cannot enforce one yet, so it does not load the pack.");
        return problems;
    }

    /// <summary>
    /// Composes a loaded pack: a knowledge pack with a search contributor per view (its records import with
    /// <see cref="KnowledgePackImporter"/>); a definition pack as a data-only module whose records the host's importer
    /// writes. Either is named in the fingerprint by its digest and signers. A pack that declares a trial still needs a passing
    /// decision for exactly this pack (its digest) when the host requires trials.
    /// </summary>
    public static ModuleSetBuilder AddPack(this ModuleSetBuilder builder, LoadedPack pack, int priority = 100)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(pack);
        if (pack.Kind == "snapshot")
            throw new PackLoadException([$"{pack.Id} is a snapshot pack: compose its definition, and import the snapshot with ToSnapshot."]);
        return pack.Knowledge is { } knowledge ? builder.AddKnowledgePack(knowledge, priority, pack.ValidUntil)
            : builder.AddDefinitionPack(pack.Manifest, pack.Digest, string.Join(", ", pack.Signers), priority, pack.ValidUntil);
    }

    /// <summary>
    /// The records of a snapshot pack as a snapshot of <paramref name="definition"/>, for the snapshot import (v1.2 §3): the
    /// pack must require that definition in a range that holds the host's version of it, and every record is checked against
    /// the definition's ontology. Nothing is written here. Every record keeps this pack as its carrier: id, version, digest and
    /// signers (PACK-bind), so a snapshot signed by one key for another publisher's definition says so on each record.
    /// </summary>
    /// <param name="pack">A loaded snapshot pack.</param>
    /// <param name="definition">The definition its records belong to: a loaded definition pack's manifest, or the host's own.</param>
    /// <param name="dependencies">The definition's own dependencies, as <see cref="ModuleSnapshot.Dependencies"/>.</param>
    /// <exception cref="PackLoadException">The pack is not a snapshot of this definition, or a record is refused; every reason is listed.</exception>
    public static ModuleSnapshot ToSnapshot(this LoadedPack pack, ModuleManifest definition, IReadOnlyList<ModuleManifest>? dependencies = null)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(definition);
        if (pack.Kind != "snapshot") throw new PackLoadException([$"{pack.Id} is a {pack.Kind} pack, not a snapshot."]);
        if (!pack.Manifest.Modules.TryGetValue(definition.Id, out var range))
            throw new PackLoadException([$"{pack.Id} is a snapshot of {string.Join(", ", pack.Manifest.Modules.Keys)}, not of {definition.Id}."]);
        if (!range.Contains(definition.Version))
            throw new PackLoadException([$"{pack.Id} needs {definition.Id} {range.Value}; the host has {definition.Version.Value}."]);
        try
        {
            return ModuleSnapshot.FromRecordFiles(definition, dependencies ?? [], pack.SnapshotSource!, pack.RecordFiles) with
            {
                Carrier = new PackCarrier { Id = pack.Id, Version = pack.Version, Digest = pack.Digest, Signer = string.Join(", ", pack.Signers) },
            };
        }
        catch (KnowledgePackException e) { throw new PackLoadException(e.Problems); }
    }
}
