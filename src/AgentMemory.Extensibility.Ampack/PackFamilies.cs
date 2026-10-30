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
    /// <summary>The member packs, in the composite's order; a member that is itself a composite is in <see cref="Families"/>.</summary>
    public required IReadOnlyList<LoadedPack> Members { get; init; }
    /// <summary>The composites it contains (PACK-nest), each with its own members, in the composite's order.</summary>
    public IReadOnlyList<LoadedFamily> Families { get; init; } = [];
    /// <summary>When the composite stops being valid here; every member's validity is capped by it, at any depth.</summary>
    public DateTimeOffset? ValidUntil { get; init; }

    /// <summary>Every pack in the family at any depth, each once, in order: a pack two composites share appears where first named.</summary>
    public IReadOnlyList<LoadedPack> AllMembers => [.. Members.Concat(Families.SelectMany(f => f.AllMembers)).DistinctBy(p => p.Id)];

    /// <summary>Every composite below this one, each once, outer before inner.</summary>
    public IReadOnlyList<LoadedFamily> AllFamilies => [.. Families.SelectMany(f => f.AllFamilies.Prepend(f)).DistinctBy(f => f.Id)];
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
    /// <summary>The outermost composites in the set (no other composite in it pins them), each with its members from the same set.</summary>
    public IReadOnlyList<LoadedFamily> Families { get; init; } = [];
    /// <summary>Members of no composite.</summary>
    public IReadOnlyList<LoadedPack> Packs { get; init; } = [];
    /// <summary>When the set stops being valid here; every pack in it is capped by it.</summary>
    public DateTimeOffset? ValidUntil { get; init; }

    /// <summary>Every pack the set carries, each once: the families' at any depth, then the packs of no family.</summary>
    public IReadOnlyList<LoadedPack> AllPacks => [.. Families.SelectMany(f => f.AllMembers).Concat(Packs).DistinctBy(p => p.Id)];

    /// <summary>Every composite the set carries, each once, outer before inner.</summary>
    public IReadOnlyList<LoadedFamily> AllFamilies => [.. Families.SelectMany(f => f.AllFamilies.Prepend(f)).DistinctBy(f => f.Id)];
}

/// <summary>
/// Compound and hierarchical packs (pack format v1.2 §5): a composite pack with its members, or a pack set that carries a
/// whole family in one archive. A member must be exactly the digest the composite's lock pins; a missing, extra or swapped
/// member refuses the family. A member may be a composite itself (PACK-nest): containment is a directed acyclic graph at
/// most four composites deep, and the outermost lock pins the whole tree, since each nested composite's digest covers its
/// own lock. Two locks that pin different content for one id refuse the family; two composites may share a member only as
/// the same content.
/// </summary>
[Experimental("AMEXT001")]
public static class PackFamilies
{
    /// <summary>
    /// Loads a composite pack and its members (folders or <c>.ampack</c> files): every pack and composite under it, at any
    /// depth, each given once.
    /// </summary>
    /// <exception cref="PackLoadException">The composite, a member or a pin is refused; every reason is listed.</exception>
    public static LoadedFamily LoadComposite(string compositePath, IEnumerable<string> memberPaths, PackTrust trust, PackInstallLedger? ledger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(compositePath);
        ArgumentNullException.ThrowIfNull(memberPaths);
        var root = OpenComposite(Read(compositePath), trust, ledger);
        var problems = new List<string>();
        var composites = new Dictionary<string, Opened>(StringComparer.Ordinal) { [root.Pack.Id] = root };
        var packs = new Dictionary<string, LoadedPack>(StringComparer.Ordinal);
        foreach (var path in memberPaths)
        {
            var content = Read(path);
            if (IsComposite(content, trust, ledger))
            {
                var opened = OpenComposite(content, trust, ledger);
                if (!composites.TryAdd(opened.Pack.Id, opened)) problems.Add($"{opened.Pack.Id} is given twice.");
            }
            else
            {
                var pack = PackLoader.Load(content, trust, ledger, allowDowngrade: false);
                if (!packs.TryAdd(pack.Id, pack)) problems.Add($"{pack.Id} is given twice.");
            }
        }
        var given = problems.Count;
        var families = Tree(composites, packs, trust, null, problems, out var reached);
        // Strangers only once the tree stands: when it cannot be assembled, nothing counts as reached.
        if (problems.Count == given)
            foreach (var extra in packs.Keys.Concat(composites.Keys).Where(id => !reached.Contains(id) && id != root.Pack.Id).Order(StringComparer.Ordinal))
                problems.Add($"{extra} is not a member of {root.Pack.Id}.");
        if (problems.Count > 0) throw new PackLoadException(problems);
        return families.Single(f => f.Id == root.Pack.Id);
    }

    /// <summary>Loads a pack set: the set's signature, then every member on its own, then every family's pins.</summary>
    /// <exception cref="PackLoadException">The set, a member or a pin is refused; every reason is listed.</exception>
    public static LoadedSet LoadSet(string path, PackTrust trust, PackInstallLedger? ledger = null) => LoadSet(Read(path), trust, ledger, false);

    /// <summary>Loads a pack set read into memory.</summary>
    public static LoadedSet LoadSet(byte[] archive, PackTrust trust, PackInstallLedger? ledger = null) => LoadSet(archive, trust, ledger, false);

    /// <summary>
    /// Loads a pack set read into memory; <paramref name="allowDowngrade"/> waives the ledger's version history only: a version
    /// still names one content for ever, and every revocation the host kept still holds (sixth review).
    /// </summary>
    public static LoadedSet LoadSet(byte[] archive, PackTrust trust, PackInstallLedger? ledger, bool allowDowngrade)
    {
        ArgumentNullException.ThrowIfNull(archive);
        PackContent content;
        try { content = PackReader.ReadArchive(archive, "set"); }
        catch (InvalidDataException e) { throw new PackLoadException([e.Message]); }
        return LoadSet(content, trust, ledger, allowDowngrade);
    }

    /// <summary>
    /// Composes a family through the SDK's composite rules: every member the manifest names, at its priority, the switched-off
    /// ones left out, each member depending only on what it contains; a nested family as a composite member, its members at
    /// its slot's priority plus their own.
    /// </summary>
    public static ModuleSetBuilder AddFamily(this ModuleSetBuilder builder, LoadedFamily family, Action<CompositeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(family);
        return builder.AddComposite(family.Manifest, members => AddMembers(members, family), configure);
    }

    /// <summary>
    /// The packs a host holds from these sets and packs, composed with no switches of its own: worked out by running the host's own
    /// composition (<see cref="AddFamily"/> for each family, <see cref="PackLoader.AddPack"/> for each pack on its own) and reading
    /// back what it registered, each with the validity it registered, so every door that holds packs holds exactly what a host holds
    /// and refuses what a host refuses (round 11, R11-1 to R11-3: serve, test and DemoBrain rebuilt the composition and missed its
    /// refusals: a lock pinned outside its range, a member needing one switched off, a module reached through two sets). Snapshot
    /// packs are no modules; they come after, as given.
    /// </summary>
    /// <exception cref="PackLoadException">A host composing them would refuse; the problem is the host's own.</exception>
    public static IReadOnlyList<LoadedPack> HostComposes(IEnumerable<LoadedSet> sets, IEnumerable<LoadedPack> packs)
    {
        ArgumentNullException.ThrowIfNull(sets);
        ArgumentNullException.ThrowIfNull(packs);
        List<LoadedSet> setList = [.. sets];
        List<LoadedPack> packList = [.. packs];
        var builder = new ModuleSetBuilder();
        var snapshots = new List<LoadedPack>();
        try
        {
            foreach (var set in setList)
            {
                foreach (var family in set.Families) builder.AddFamily(family);
                foreach (var pack in set.Packs) Add(pack);
            }
            foreach (var pack in packList) Add(pack);
        }
        catch (Exception e) when (e is not PackLoadException && e is InvalidOperationException or ArgumentException)
        {
            throw new PackLoadException([e.Message]);
        }
        var copies = setList.SelectMany(s => s.AllPacks).Concat(packList).GroupBy(p => p.Digest, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        return
        [
            .. builder.Modules.Where(r => r.PackDigest is not null).Select(r => copies[r.PackDigest!] is var pack && pack.ValidUntil == r.ValidUntil
                ? pack : pack with { ValidUntil = r.ValidUntil, Knowledge = pack.Knowledge?.WithValidUntil(r.ValidUntil) }),
            .. snapshots,
        ];

        void Add(LoadedPack pack)
        {
            if (pack.Kind == "snapshot") snapshots.Add(pack);
            else builder.AddPack(pack);
        }
    }

    // In the composite's order: a nested family as a composite member, a pack as a module.
    private static void AddMembers(CompositeMembers members, LoadedFamily family)
    {
        foreach (var member in family.Manifest.Members)
        {
            if (family.Families.FirstOrDefault(f => f.Id == member.Id) is { } nested)
                members.AddComposite(nested.Manifest, inner => AddMembers(inner, nested));
            else if (family.Members.FirstOrDefault(p => p.Id == member.Id) is { } pack)
            {
                if (pack.Knowledge is { } knowledge) members.AddKnowledgePack(knowledge, pack.ValidUntil);
                else members.AddDefinitionPack(pack.Manifest, pack.Digest, string.Join(", ", pack.Signers), pack.ValidUntil);
            }
        }
    }

    private static PackContent Read(string path)
    {
        try { return PackReader.Read(path); }
        catch (Exception e) when (e is InvalidDataException or IOException) { throw new PackLoadException([$"{path}: {e.Message}"]); }
    }

    internal static LoadedSet LoadSet(PackContent content, PackTrust trust, PackInstallLedger? ledger, bool allowDowngrade = false)
    {
        ArgumentNullException.ThrowIfNull(trust);
        var (set, verifications) = PackVerifier.VerifySet(content, trust.Policy, DateTimeOffset.UtcNow, ledger?.Ledger, allowDowngrade);
        if (set.Pack is not { } outer) throw new PackLoadException(set.Problems);
        var problems = verifications.Where(v => !v.Value.IsVerified).SelectMany(v => v.Value.Problems.Select(p => $"packs/{v.Key}/: {p}")).ToList();
        if (problems.Count > 0) throw new PackLoadException(problems);

        var members = PackSets.Members(content.Files);
        var composites = new Dictionary<string, Opened>(StringComparer.Ordinal);
        var packs = new Dictionary<string, LoadedPack>(StringComparer.Ordinal);
        // The set's own validity caps every pack in it (red team 7).
        var setValidUntil = PackLoader.ValidUntil(outer, trust.Policy);
        foreach (var (id, files) in members.OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            var memberContent = new PackContent(files, content.Source + "#" + id);
            if (verifications[id].Pack!.Manifest.Kind == "composite") composites[id] = OpenComposite(memberContent, trust, ledger, allowDowngrade);
            else
            {
                var pack = PackLoader.Load(memberContent, trust, ledger, allowDowngrade);
                packs[id] = PackLoader.Narrowed(pack, setValidUntil);
            }
        }
        var refused = new List<string>();
        var families = Tree(composites, packs, trust, setValidUntil, refused, out var reached);
        if (refused.Count > 0) throw new PackLoadException(refused);
        return new LoadedSet
        {
            Id = outer.Id, Version = outer.Version, Digest = outer.Digest, Families = families, ValidUntil = setValidUntil,
            Packs = [.. packs.Values.Where(p => !reached.Contains(p.Id)).OrderBy(p => p.Id, StringComparer.Ordinal)],
        };
    }

    // Whether a pack's content declares a composite, read once its signatures hold (review 6, H1); one that does not
    // authenticate is loaded as a pack, which refuses it with every reason.
    private static bool IsComposite(PackContent content, PackTrust trust, PackInstallLedger? ledger) =>
        PackVerifier.Authenticated(content, trust.Policy, DateTimeOffset.UtcNow, ledger?.Ledger).Manifest?.Kind == "composite";

    /// <summary>
    /// The families the composites form (PACK-nest): every lock agrees with every other on each id it pins, no composite
    /// contains itself at any depth, and from each outermost composite (one no other pins) the tree is assembled at most four
    /// deep, each member exactly the content its lock pins. <paramref name="reached"/> is every id some family holds.
    /// </summary>
    private static List<LoadedFamily> Tree(IReadOnlyDictionary<string, Opened> composites, IReadOnlyDictionary<string, LoadedPack> packs, PackTrust trust,
        DateTimeOffset? cap, List<string> problems, out HashSet<string> reached)
    {
        reached = new HashSet<string>(StringComparer.Ordinal);
        // Two locks that pin different content for one id: a family installs one content of each pack.
        foreach (var group in composites.Values.SelectMany(c => c.Lock.Select(pin => (Composite: c.Pack.Id, Pin: pin))).GroupBy(p => p.Pin.Id, StringComparer.Ordinal))
            if (group.Select(p => (p.Pin.Version, p.Pin.Digest)).Distinct().Count() > 1)
            {
                var (a, b) = (group.First(), group.First(p => (p.Pin.Version, p.Pin.Digest) != (group.First().Pin.Version, group.First().Pin.Digest)));
                problems.Add($"{a.Composite} and {b.Composite} pin different {group.Key}: {a.Pin.Version} {Short(a.Pin.Digest)} and {b.Pin.Version} {Short(b.Pin.Digest)}; one family installs one content of each pack.");
            }
        if (Cycle(composites) is { } cycle) problems.Add($"{cycle[0]} contains itself: {string.Join(" > ", cycle)}.");
        if (problems.Count > 0) return [];
        var pinned = composites.Values.SelectMany(c => c.Lock.Select(p => p.Id)).ToHashSet(StringComparer.Ordinal);
        var families = new List<LoadedFamily>();
        foreach (var root in composites.Values.Where(c => !pinned.Contains(c.Pack.Id)).OrderBy(c => c.Pack.Id, StringComparer.Ordinal))
        {
            reached.Add(root.Pack.Id);
            if (Assemble(root, composites, packs, trust, cap, [root.Pack.Id], problems, reached) is { } family) families.Add(family);
        }
        return families;
    }

    // The first containment cycle among the composites, as the chain of ids that closes it; null when there is none.
    private static List<string>? Cycle(IReadOnlyDictionary<string, Opened> composites)
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var path = new List<string>();
        List<string>? Visit(string id)
        {
            if (state.GetValueOrDefault(id) == 2) return null;
            if (state.GetValueOrDefault(id) == 1) return [.. path.Skip(path.IndexOf(id)), id];
            state[id] = 1;
            path.Add(id);
            foreach (var pin in composites[id].Lock.Where(p => composites.ContainsKey(p.Id)))
                if (Visit(pin.Id) is { } found) return found;
            path.RemoveAt(path.Count - 1);
            state[id] = 2;
            return null;
        }
        foreach (var id in composites.Keys.Order(StringComparer.Ordinal))
            if (Visit(id) is { } cycle) return cycle;
        return null;
    }

    private static string Short(string digest) => digest[..Math.Min(digest.Length, 23)] + "…";

    private sealed record Opened(VerifiedPack Pack, CompositeManifest Manifest, IReadOnlyList<LockEntry> Lock);

    private static Opened OpenComposite(PackContent content, PackTrust trust, PackInstallLedger? ledger, bool allowDowngrade = false)
    {
        ArgumentNullException.ThrowIfNull(trust);
        var verification = PackVerifier.VerifyOne(content, trust.Policy, DateTimeOffset.UtcNow, ledger?.Ledger, allowDowngrade);
        if (verification.Pack is not { } pack) throw new PackLoadException(verification.Problems);
        if (pack.Manifest.Kind != "composite") throw new PackLoadException([$"{pack.Id} is a {pack.Manifest.Kind} pack, not a composite."]);
        CompositeManifest manifest;
        try { manifest = CompositeManifest.Parse(Encoding.UTF8.GetString(PackLayout.ToComposite(pack.Files))); }
        catch (Exception e) when (e is FormatException or InvalidDataException) { throw new PackLoadException([e.Message]); }
        if (manifest.Validate() is { Count: > 0 } invalid) throw new PackLoadException(invalid);
        var (entries, problem) = PackLock.Parse(pack.Files[PackPaths.LockFile]);
        if (entries is null) throw new PackLoadException([problem!]);
        return new Opened(pack, manifest, entries);
    }

    // Exactly the pinned members, each at the pinned version and digest: a pack, or a composite assembled the same way one
    // level down. The composite's own validity caps every member (red team 7), at any depth. Null when anything is refused.
    private static LoadedFamily? Assemble(Opened composite, IReadOnlyDictionary<string, Opened> composites, IReadOnlyDictionary<string, LoadedPack> packs,
        PackTrust trust, DateTimeOffset? cap, IReadOnlyList<string> chain, List<string> problems, HashSet<string> reached)
    {
        if (chain.Count > CompositeManifest.MaximumDepth)
        {
            problems.Add($"{string.Join(" > ", chain)} nests deeper than {CompositeManifest.MaximumDepth} composites.");
            // What it pins belongs to the family all the same: no second, misleading "not a member".
            var below = new Queue<Opened>([composite]);
            while (below.TryDequeue(out var next))
                foreach (var pin in next.Lock.Where(p => reached.Add(p.Id)))
                    if (composites.TryGetValue(pin.Id, out var deeper)) below.Enqueue(deeper);
            return null;
        }
        var validUntil = PackLoader.Earliest([PackLoader.ValidUntil(composite.Pack, trust.Policy), cap]);
        var leaves = new Dictionary<string, LoadedPack>(StringComparer.Ordinal);
        var nested = new Dictionary<string, LoadedFamily>(StringComparer.Ordinal);
        var failed = false;
        foreach (var pin in composite.Lock)
        {
            (string Version, string Digest)? given = composites.TryGetValue(pin.Id, out var inner) ? (inner.Pack.Version, inner.Pack.Digest)
                : packs.TryGetValue(pin.Id, out var leaf) ? (leaf.Version, leaf.Digest) : null;
            if (given is not { } found) { problems.Add($"{composite.Pack.Id} pins {pin.Id} {pin.Version}, which is missing."); failed = true; continue; }
            // Pinned, whatever its content: a member given as other content is that, not a stranger to the family.
            reached.Add(pin.Id);
            if (found.Digest != pin.Digest || found.Version != pin.Version)
            {
                problems.Add($"{composite.Pack.Id} pins {pin.Id} {pin.Version} {pin.Digest[..23]}…, but was given {found.Version} {found.Digest[..23]}….");
                failed = true;
                continue;
            }
            if (inner is not null)
            {
                if (Assemble(inner, composites, packs, trust, validUntil, [.. chain, pin.Id], problems, reached) is { } family) nested[pin.Id] = family;
                else failed = true;
            }
            // A composite contains modules; a snapshot is records for one of them and travels beside it in a set.
            else if (packs[pin.Id].Kind == "snapshot")
            {
                problems.Add($"{pin.Id} is a snapshot pack; a composite contains modules, so carry it beside {composite.Pack.Id} in a pack set.");
                failed = true;
            }
            else leaves[pin.Id] = PackLoader.Narrowed(packs[pin.Id], validUntil);
        }
        if (failed) return null;
        return new LoadedFamily
        {
            Id = composite.Pack.Id, Version = composite.Pack.Version, Digest = composite.Pack.Digest, Manifest = composite.Manifest, ValidUntil = validUntil,
            Members = [.. composite.Manifest.Members.Where(m => leaves.ContainsKey(m.Id)).Select(m => leaves[m.Id])],
            Families = [.. composite.Manifest.Members.Where(m => nested.ContainsKey(m.Id)).Select(m => nested[m.Id])],
        };
    }
}
