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
    internal PackTrust(TrustPolicy policy) => Policy = policy;

    internal TrustPolicy Policy { get; }

    /// <summary>Reads a trust policy (<c>agentmemory.pack.trust/1.1</c>).</summary>
    /// <exception cref="InvalidDataException">The policy is malformed, or a key id is not the hash of its key.</exception>
    public static PackTrust Parse(byte[] json) => new(TrustPolicy.Parse(json ?? throw new ArgumentNullException(nameof(json))));

    /// <summary>Reads a trust policy file: at most 4 MiB, never through a link (review 6, M2: read whole and through links).</summary>
    /// <exception cref="InvalidDataException">The policy is malformed, larger than 4 MiB, a link or a folder.</exception>
    public static PackTrust Load(string path) => Parse(PackReader.ReadFile(path, TrustPolicy.MaximumBytes, "The trust policy"));

    /// <summary>Trusts nothing: every pack is refused. There is no setting that admits unsigned packs.</summary>
    public static PackTrust None { get; } = new(new TrustPolicy());

    /// <summary>
    /// This trust with a signed revocation document applied (pack format v1.2 §8, PACK-harden): signed by a key this policy
    /// names as <c>revoker</c>, its revoked digests and keys are added, and its expiry becomes the policy's, after which
    /// nothing verifies until a fresh document is applied.
    /// </summary>
    /// <exception cref="InvalidDataException">The document is malformed, not signed by a trusted revoker valid now, or issued in the future.</exception>
    /// <param name="signedDocument">The signed revocation document.</param>
    /// <param name="ledger">The host's install ledger: when given, what the document revokes is kept there for good, so a later run
    /// with an older list, or none, still holds it.</param>
    public PackTrust WithRevocations(byte[] signedDocument, PackInstallLedger? ledger = null)
    {
        ArgumentNullException.ThrowIfNull(signedDocument);
        var applied = new PackTrust(Revocations.Apply(Policy, signedDocument, DateTimeOffset.UtcNow, ledger?.Ledger));
        ledger?.Save();
        return applied;
    }

    /// <summary>
    /// This trust with one run's revocation documents applied together: with a <paramref name="ledger"/>, what the host revoked
    /// before first. A list with a live signer adds its revocations, sets freshness and is kept; a list without one denies the
    /// packs and non-revoker keys it names for this run only and is named in <paramref name="notApplied"/>. Lists that revoke
    /// each other's signers, or a list without a live signer that revokes a revoker still live, are refused.
    /// </summary>
    /// <exception cref="InvalidDataException">A document is malformed, unsigned or from the future; the lists revoke each other's
    /// signers; or a list without a live signer revokes a revoker that has not expired and no live list carries it forward. Never
    /// fall back to loading without the lists: that would lose this run's denials.</exception>
    public PackTrust WithRevocations(IReadOnlyList<byte[]> signedDocuments, PackInstallLedger? ledger, out IReadOnlyList<string> notApplied)
    {
        ArgumentNullException.ThrowIfNull(signedDocuments);
        var applied = new PackTrust(Revocations.ApplyAll(Policy, signedDocuments, DateTimeOffset.UtcNow, ledger?.Ledger, out notApplied));
        if (signedDocuments.Count > 0) ledger?.Save();
        return applied;
    }
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

    /// <summary>
    /// <paramref name="trust"/> with every revocation this host applied before added (fifth review): what was revoked here stays
    /// revoked, whatever lists a later run is given. Verifying with this ledger does the same.
    /// </summary>
    public PackTrust WithKeptRevocations(PackTrust trust)
    {
        ArgumentNullException.ThrowIfNull(trust);
        return new PackTrust(Ledger.Apply(trust.Policy));
    }

    /// <summary>
    /// Forgets the newest revocation list's freshness, for a host that stops using revocation lists; what was revoked stays
    /// revoked. Saved at once.
    /// </summary>
    public void RetireRevocationLists()
    {
        Ledger.RetireRevocationLists();
        Save();
    }

    /// <summary>Saves the ledger as it is.</summary>
    internal void Save() => Ledger.Save(_path);

    /// <summary>Records an installed pack and saves the ledger.</summary>
    public void Record(LoadedPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        Ledger.Record(pack.Id, pack.Version, pack.Digest);
        Ledger.Save(_path);
    }

    /// <summary>Records an installed composite, every composite under it and every member at any depth, and saves the ledger.</summary>
    public void Record(LoadedFamily family)
    {
        ArgumentNullException.ThrowIfNull(family);
        foreach (var composite in family.AllFamilies.Prepend(family)) Ledger.Record(composite.Id, composite.Version, composite.Digest);
        foreach (var member in family.AllMembers) Ledger.Record(member.Id, member.Version, member.Digest);
        Ledger.Save(_path);
    }

    /// <summary>Records an installed pack set, its families and its packs, and saves the ledger: an older set is then refused.</summary>
    public void Record(LoadedSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        Ledger.Record(set.Id, set.Version, set.Digest);
        foreach (var family in set.AllFamilies) Ledger.Record(family.Id, family.Version, family.Digest);
        foreach (var pack in set.AllPacks) Ledger.Record(pack.Id, pack.Version, pack.Digest);
        Ledger.Save(_path);
    }
}

/// <summary>A verified pack, mapped onto the module model.</summary>
[Experimental("AMEXT001")]
public sealed record LoadedPack
{
    /// <summary>The pack id.</summary>
    public string Id { get; internal init; } = null!;
    /// <summary>Its version.</summary>
    public string Version { get; internal init; } = null!;
    /// <summary>Its identity: <c>sha256:</c> over the exact bytes of its file list.</summary>
    public string Digest { get; internal init; } = null!;
    /// <summary><c>knowledge</c>, <c>definition</c> or <c>snapshot</c>.</summary>
    public string Kind { get; internal init; } = null!;
    /// <summary>The module manifest the pack maps to, validated.</summary>
    public ModuleManifest Manifest { get; internal init; } = null!;
    /// <summary>For a knowledge pack, the pack with its records, ready to compose and import; otherwise null.</summary>
    public KnowledgePack? Knowledge { get; internal init; }
    /// <summary>Who signed it: role and key id, each verified against the host's trust.</summary>
    public IReadOnlyList<string> Signers { get; internal init; } = [];
    /// <summary>
    /// For a snapshot pack, what its records were taken from: <c>git:</c> and the commit its <c>pack.json</c> names, otherwise
    /// <c>pack:</c> and its version; otherwise null.
    /// </summary>
    public string? SnapshotSource { get; internal init; }
    /// <summary>The pack's own tests (<c>evals/tests.json</c>), verified with it; null when it has none. Run with <see cref="PackTests"/>.</summary>
    public ReadOnlyMemory<byte>? Tests { get; internal init; }
    /// <summary>
    /// When the pack stops being valid on this host, and a host switches it off at runtime: its own <c>validUntil</c>, its
    /// family's and its set's, the host's revocation list expiry, and its signing keys' expiry, whichever comes first.
    /// </summary>
    public DateTimeOffset? ValidUntil { get; internal init; }
    /// <summary>For a federated pack, who published it, for whom and why, as verified against the host's trust; otherwise null.</summary>
    public PackPublication? Publication { get; internal init; }
    /// <summary>Its card (<c>card.json</c>, pack format v1.2 §15), as signed with it; null when it carries none.</summary>
    public PackCardInfo? Card { get; internal init; }
    /// <summary>
    /// The evaluation certificates it carries (pack format v1.2 §16), trusted or not: each says who evaluated it, on what, with
    /// what result. They inform; one admits the pack only where the host trusts certificates (<see cref="PackCertificates.Admit"/>).
    /// </summary>
    public IReadOnlyList<PackEvaluation> Certificates { get; internal init; } = [];
    /// <summary>For a snapshot pack, its verified record files, read by <see cref="PackLoader.ToSnapshot"/>.</summary>
    internal IReadOnlyDictionary<string, ReadOnlyMemory<byte>> RecordFiles { get; init; } = new Dictionary<string, ReadOnlyMemory<byte>>();
}

/// <summary>
/// A federated pack's publication (federated packs §3): the publisher's WebID, the WebIDs it is for (null when public), and
/// why it is shared. A host loads such a pack only when a key it bound to that WebID signed it and the host is in the audience.
/// </summary>
[Experimental("AMEXT001")]
public sealed record PackPublication(string Publisher, IReadOnlyList<string>? Audience, string? Purpose)
{
    /// <summary>Whether everyone may load it.</summary>
    public bool IsPublic => Audience is null;

    internal static PackPublication? Of(PackFederation? federation) =>
        federation is null ? null : new PackPublication(federation.Publisher, federation.Audience, federation.Purpose);
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
    /// An archive's id, version and kind, read only once its signatures hold (review 6, H1: an unsigned pack.json of any size was
    /// parsed to choose the loader): to choose how to load it (<see cref="Load(byte[], PackTrust, PackInstallLedger?, bool)"/>,
    /// <see cref="PackFamilies.LoadSet(byte[], PackTrust, PackInstallLedger?)"/> or <see cref="PackFamilies.LoadComposite"/>).
    /// The chosen path verifies everything else, the ledger included.
    /// </summary>
    /// <exception cref="PackLoadException">The archive is not readable under the strict profile, its signatures do not hold under
    /// <paramref name="trust"/>, or it has no valid pack.json.</exception>
    public static (string Id, string Version, string Kind) Peek(byte[] archive, PackTrust trust)
    {
        var (manifest, _) = Open(archive, trust);
        return (manifest.Id, manifest.Version, manifest.Kind);
    }

    // The archive read once and authenticated: its manifest, to pick the loader, and its content, for the loader to verify.
    internal static (PackManifest Manifest, PackContent Content) Open(byte[] archive, PackTrust trust, PackInstallLedger? ledger = null)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(trust);
        PackContent content;
        try { content = PackReader.ReadArchive(archive, "archive"); }
        catch (InvalidDataException e) { throw new PackLoadException([e.Message]); }
        var (manifest, problems) = PackVerifier.Authenticated(content, trust.Policy, DateTimeOffset.UtcNow, ledger?.Ledger);
        return manifest is null ? throw new PackLoadException(problems) : (manifest, content);
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

        var validUntil = ValidUntil(pack, trust.Policy);
        if (knowledge is not null) knowledge.ValidUntil = validUntil;
        return new LoadedPack
        {
            Id = pack.Id, Version = pack.Version, Digest = pack.Digest, Kind = pack.Manifest.Kind, Manifest = manifest,
            Knowledge = knowledge, Signers = signers, SnapshotSource = snapshotSource, RecordFiles = recordFiles, ValidUntil = validUntil,
            Publication = PackPublication.Of(pack.Manifest.Federation),
            Card = PackCardInfo.Of(pack.Files),
            // Read under the same trust the pack verified under: what this host revoked before (its ledger) holds for evaluator
            // keys too (review 1, H2).
            Certificates = PackCertificates.Read(pack, ledger is null ? trust.Policy : ledger.Ledger.Apply(trust.Policy), DateTimeOffset.UtcNow),
            // An absent file is null, not empty: a plain null would convert, through byte[], to an empty and present ReadOnlyMemory.
            Tests = pack.Files.TryGetValue(PackTests.FileName, out var tests) ? new ReadOnlyMemory<byte>(tests) : (ReadOnlyMemory<byte>?)null,
        };
    }

    /// <summary>
    /// When a verified pack stops being valid here: its own validUntil, the revocation list's expiry, and for each required role
    /// the latest expiry among the keys that signed in it (a key without one never expires), whichever comes first.
    /// </summary>
    internal static DateTimeOffset? ValidUntil(VerifiedPack pack, TrustPolicy policy)
    {
        var keys = policy.RequiredRoles.Select(role => pack.Signatures.Where(s => s.Role == role).Select(s => policy.Find(s.KeyId)?.NotAfter).ToList())
            .Select(expiries => expiries.Count == 0 || expiries.Any(e => e is null) ? null : expiries.Max());
        return Earliest([pack.Manifest.ValidUntil, policy.RevocationsExpire, .. keys]);
    }

    // A member of a set or family valid no longer than what carries it: the loaded pack and its knowledge pack alike (review 1, M9).
    internal static LoadedPack Narrowed(LoadedPack pack, DateTimeOffset? until)
    {
        var earliest = Earliest([pack.ValidUntil, until]);
        // A copy of the knowledge pack, never the one other loaded copies share (review 2: a pack in two families expired early).
        return pack with { ValidUntil = earliest, Knowledge = pack.Knowledge?.WithValidUntil(earliest) };
    }

    internal static DateTimeOffset? Earliest(IEnumerable<DateTimeOffset?> times)
    {
        DateTimeOffset? earliest = null;
        foreach (var time in times)
            if (time is { } value && (earliest is null || value < earliest)) earliest = value;
        return earliest;
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
        var added = pack.Knowledge is { } knowledge ? builder.AddKnowledgePack(knowledge, priority, pack.ValidUntil)
            : builder.AddDefinitionPack(pack.Manifest, pack.Digest, string.Join(", ", pack.Signers), priority, pack.ValidUntil);
        // The latest certificate this host trusts now, as it loaded the pack: a decision it made from a certificate counts only
        // while that certificate travels with the pack, is trusted, and no newer trusted one stands beside it (review 1, M1;
        // review 2, L3).
        if (added.Modules.LastOrDefault(r => r.Manifest.Id == pack.Id && r.PackDigest == pack.Digest) is { } registration)
            registration.TrustedCertificates = PackCertificates.LatestTrusted(pack) is { } latest ? [latest.Digest] : [];
        return added;
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
    /// <param name="grants">The host's consent grants (<see cref="ModuleSnapshot.ConsentGrants"/>): a link into a dependency that
    /// does not list the definition in containableBy is refused without one.</param>
    /// <exception cref="PackLoadException">The pack is not a snapshot of this definition, or a record is refused; every reason is listed.</exception>
    public static ModuleSnapshot ToSnapshot(this LoadedPack pack, ModuleManifest definition, IReadOnlyList<ModuleManifest>? dependencies = null,
        IReadOnlyList<ModuleConsentGrant>? grants = null)
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
            return ModuleSnapshot.FromRecordFiles(definition, dependencies ?? [], pack.SnapshotSource!, pack.RecordFiles, grants) with
            {
                Carrier = new PackCarrier { Id = pack.Id, Version = pack.Version, Digest = pack.Digest, Signer = string.Join(", ", pack.Signers) },
            };
        }
        catch (KnowledgePackException e) { throw new PackLoadException(e.Problems); }
    }
}
