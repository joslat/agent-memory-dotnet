using System.Diagnostics.CodeAnalysis;
using System.Text;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Packs.Format;

namespace AgentMemory.Extensibility.Ampack;

/// <summary>A verified composite pack with exactly the members its lock pins, each verified on its own.</summary>
[Experimental("AMEXT001")]
public sealed record LoadedFamily
{
    /// <summary>The composite's id.</summary>
    public required string Id { get; init; }
    /// <summary>Its version.</summary>
    public required string Version { get; init; }
    /// <summary>Its digest; its lock pins every member's.</summary>
    public required string Digest { get; init; }
    /// <summary>The composite manifest: members, version ranges, priorities, switches and containment.</summary>
    public required CompositeManifest Manifest { get; init; }
    /// <summary>The members, in the composite's order.</summary>
    public required IReadOnlyList<LoadedPack> Members { get; init; }
}

/// <summary>A verified pack set: its digest, the families it carries, and the packs in it that belong to no family.</summary>
[Experimental("AMEXT001")]
public sealed record LoadedSet
{
    /// <summary>The set's id.</summary>
    public required string Id { get; init; }
    /// <summary>Its version.</summary>
    public required string Version { get; init; }
    /// <summary>Its digest: the set's signature binds every member to it.</summary>
    public required string Digest { get; init; }
    /// <summary>The composites in the set, each with its members from the same set.</summary>
    public IReadOnlyList<LoadedFamily> Families { get; init; } = [];
    /// <summary>Members of no composite.</summary>
    public IReadOnlyList<LoadedPack> Packs { get; init; } = [];
}

/// <summary>
/// Compound and hierarchical packs (pack format v1.2 §5): a composite pack with its members, or a pack set that carries a
/// whole family in one archive. A member must be exactly the digest the composite's lock pins; a missing, extra or swapped
/// member refuses the family.
/// </summary>
[Experimental("AMEXT001")]
public static class PackFamilies
{
    /// <summary>Loads a composite pack and its member packs (folders or <c>.ampack</c> files).</summary>
    /// <exception cref="PackLoadException">The composite, a member or a pin is refused; every reason is listed.</exception>
    public static LoadedFamily LoadComposite(string compositePath, IEnumerable<string> memberPaths, PackTrust trust, PackInstallLedger? ledger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(compositePath);
        ArgumentNullException.ThrowIfNull(memberPaths);
        var composite = OpenComposite(Read(compositePath), trust, ledger);
        var members = memberPaths.Select(p => PackLoader.Load(Read(p), trust, ledger, allowDowngrade: false)).ToList();
        return Assemble(composite, members);
    }

    /// <summary>Loads a pack set: the set's signature, then every member on its own, then every family's pins.</summary>
    /// <exception cref="PackLoadException">The set, a member or a pin is refused; every reason is listed.</exception>
    public static LoadedSet LoadSet(string path, PackTrust trust, PackInstallLedger? ledger = null) => LoadSet(Read(path), trust, ledger);

    /// <summary>Loads a pack set read into memory.</summary>
    public static LoadedSet LoadSet(byte[] archive, PackTrust trust, PackInstallLedger? ledger = null)
    {
        ArgumentNullException.ThrowIfNull(archive);
        PackContent content;
        try { content = PackReader.ReadArchive(archive, "set"); }
        catch (InvalidDataException e) { throw new PackLoadException([e.Message]); }
        return LoadSet(content, trust, ledger);
    }

    /// <summary>
    /// Composes a family through the SDK's composite rules: every member the manifest names, at its priority, the switched-off
    /// ones left out, each member depending only on what it contains.
    /// </summary>
    public static ModuleSetBuilder AddFamily(this ModuleSetBuilder builder, LoadedFamily family, Action<CompositeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(family);
        return builder.AddComposite(family.Manifest, members =>
        {
            foreach (var pack in family.Members)
                if (pack.Knowledge is { } knowledge) members.AddKnowledgePack(knowledge);
                else members.Add(pack.Manifest, _ => { });
        }, configure);
    }

    private static PackContent Read(string path)
    {
        try { return PackReader.Read(path); }
        catch (Exception e) when (e is InvalidDataException or IOException) { throw new PackLoadException([$"{path}: {e.Message}"]); }
    }

    private static LoadedSet LoadSet(PackContent content, PackTrust trust, PackInstallLedger? ledger)
    {
        ArgumentNullException.ThrowIfNull(trust);
        var (set, verifications) = PackVerifier.VerifySet(content, trust.Policy, DateTimeOffset.UtcNow, ledger?.Ledger);
        if (set.Pack is not { } outer) throw new PackLoadException(set.Problems);
        var problems = verifications.Where(v => !v.Value.IsVerified).SelectMany(v => v.Value.Problems.Select(p => $"packs/{v.Key}/: {p}")).ToList();
        if (problems.Count > 0) throw new PackLoadException(problems);

        var members = PackSets.Members(content.Files);
        var composites = new List<(VerifiedPack Pack, CompositeManifest Manifest, IReadOnlyList<LockEntry> Lock)>();
        var packs = new Dictionary<string, LoadedPack>(StringComparer.Ordinal);
        foreach (var (id, files) in members.OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            var memberContent = new PackContent(files, content.Source + "#" + id);
            if (verifications[id].Pack!.Manifest.Kind == "composite") composites.Add(OpenComposite(memberContent, trust, ledger));
            else packs[id] = PackLoader.Load(memberContent, trust, ledger, allowDowngrade: false);
        }
        var families = composites.Select(c => Assemble(c, [.. c.Lock.Select(e => packs.GetValueOrDefault(e.Id)).OfType<LoadedPack>()])).ToList();
        var inFamilies = families.SelectMany(f => f.Members.Select(m => m.Id)).ToHashSet(StringComparer.Ordinal);
        return new LoadedSet
        {
            Id = outer.Id, Version = outer.Version, Digest = outer.Digest, Families = families,
            Packs = [.. packs.Values.Where(p => !inFamilies.Contains(p.Id)).OrderBy(p => p.Id, StringComparer.Ordinal)],
        };
    }

    private static (VerifiedPack Pack, CompositeManifest Manifest, IReadOnlyList<LockEntry> Lock) OpenComposite(PackContent content, PackTrust trust,
        PackInstallLedger? ledger)
    {
        ArgumentNullException.ThrowIfNull(trust);
        var verification = PackVerifier.Verify(content, trust.Policy, DateTimeOffset.UtcNow, ledger?.Ledger);
        if (verification.Pack is not { } pack) throw new PackLoadException(verification.Problems);
        if (pack.Manifest.Kind != "composite") throw new PackLoadException([$"{pack.Id} is a {pack.Manifest.Kind} pack, not a composite."]);
        CompositeManifest manifest;
        try { manifest = CompositeManifest.Parse(Encoding.UTF8.GetString(PackLayout.ToComposite(pack.Files))); }
        catch (Exception e) when (e is FormatException or InvalidDataException) { throw new PackLoadException([e.Message]); }
        if (manifest.Validate() is { Count: > 0 } invalid) throw new PackLoadException(invalid);
        var (entries, problem) = PackLock.Parse(pack.Files[PackPaths.LockFile]);
        if (entries is null) throw new PackLoadException([problem!]);
        return (pack, manifest, entries);
    }

    // Exactly the pinned members: each present once, at the pinned version and digest; nothing else.
    private static LoadedFamily Assemble((VerifiedPack Pack, CompositeManifest Manifest, IReadOnlyList<LockEntry> Lock) composite, IReadOnlyList<LoadedPack> members)
    {
        var problems = new List<string>();
        var byId = members.GroupBy(m => m.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        foreach (var pin in composite.Lock)
        {
            if (!byId.TryGetValue(pin.Id, out var found)) { problems.Add($"{composite.Pack.Id} pins {pin.Id} {pin.Version}, which is missing."); continue; }
            if (found.Count > 1) problems.Add($"{pin.Id} is given twice.");
            else if (found[0].Digest != pin.Digest || found[0].Version != pin.Version)
                problems.Add($"{composite.Pack.Id} pins {pin.Id} {pin.Version} {pin.Digest[..23]}…, but was given {found[0].Version} {found[0].Digest[..23]}….");
        }
        foreach (var extra in byId.Keys.Where(id => composite.Lock.All(p => p.Id != id)).Order(StringComparer.Ordinal))
            problems.Add($"{extra} is not a member of {composite.Pack.Id}.");
        // A composite contains modules; a snapshot is records for one of them and travels beside it in a set.
        foreach (var snapshot in members.Where(m => m.Kind == "snapshot"))
            problems.Add($"{snapshot.Id} is a snapshot pack; a composite contains modules, so carry it beside {composite.Pack.Id} in a pack set.");
        if (problems.Count > 0) throw new PackLoadException(problems);
        return new LoadedFamily
        {
            Id = composite.Pack.Id, Version = composite.Pack.Version, Digest = composite.Pack.Digest, Manifest = composite.Manifest,
            Members = [.. composite.Manifest.Members.Select(m => byId[m.Id][0])],
        };
    }
}
