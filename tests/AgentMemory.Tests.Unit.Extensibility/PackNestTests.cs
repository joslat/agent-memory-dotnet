using System.Text;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Packs.CodeMap;
using AgentMemory.Packs.Coding;
using AgentMemory.Packs.Format;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// Nested composites (PACK-nest): a composite may contain composites, at most four deep. The outermost lock pins the whole
/// tree; two locks that pin different content for one id refuse the family; two composites may share a member as the same
/// content, composed once; a composite that contains itself is refused. Nested members take their slot's priority plus
/// their own, and switching a slot off switches off everything under it.
/// </summary>
public sealed class PackNestTests : IDisposable
{
    private static readonly string Packs = Path.Combine(AppContext.BaseDirectory, "Packs");
    private static readonly string[] CodingMembers = ["Decisions", "Repository", "CodeMap", "Work"];
    private const string MartialArtsId = "io.agentmemory.packs.martialarts";
    private readonly string _root = Directory.CreateTempSubdirectory("pack-nest-").FullName;
    private readonly PackKey _key = PackKey.Create();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private PackTrust Trust() => PackTrust.Parse(Encoding.UTF8.GetBytes(PackAuthoring.TrustPolicy(_key.PublicKeyPem, "io.agentmemory.packs")));

    private Dictionary<string, byte[]> Built(Dictionary<string, byte[]> source)
    {
        var (files, _) = PackBuilder.Build(source);
        PackBuilder.Sign(files, _key.PrivateKeyPem, "publisher", DateTimeOffset.UtcNow.AddMinutes(-1));
        return files;
    }

    private static string Id(IReadOnlyDictionary<string, byte[]> files) => FilesList.Parse(files[PackPaths.FilesList]).List!.Id;

    private static LockEntry Pin(IReadOnlyDictionary<string, byte[]> files)
    {
        var list = FilesList.Parse(files[PackPaths.FilesList]).List!;
        return new LockEntry(list.Id, list.Version, FilesList.DigestOf(files[PackPaths.FilesList]));
    }

    // The martial arts pack; with other content, one kata's move count changed (the same id and version).
    private Dictionary<string, byte[]> MartialArts(bool otherContent = false)
    {
        var folder = Path.Combine(Packs, "MartialArts");
        var bundle = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(folder, f).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllBytes, StringComparer.Ordinal);
        if (otherContent)
            bundle["seed/kata.jsonl"] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bundle["seed/kata.jsonl"]).Replace("\"moves\":21", "\"moves\":22", StringComparison.Ordinal));
        return Built(new Dictionary<string, byte[]>(PackAuthoring.FromModuleBundle(bundle), StringComparer.Ordinal));
    }

    private static Dictionary<string, byte[]> FromManifest(string folder, string file = "manifest.json") =>
        PackLayout.FromModule(new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["manifest.json"] = File.ReadAllBytes(Path.Combine(Packs, folder, file)) });

    // A composite named id, with these members at these priorities, locked to exactly them (or to the pins given).
    private Dictionary<string, byte[]> Composite(string id, IEnumerable<(IReadOnlyDictionary<string, byte[]> Member, int Priority)> members, IEnumerable<LockEntry>? pins = null)
    {
        var list = members.ToList();
        var json = "{\"format\":\"agentmemory.composite/v1alpha1\",\"id\":\"" + id + "\",\"version\":\"1.0.0\",\"members\":["
            + string.Join(",", list.Select(m => "{\"id\":\"" + Id(m.Member) + "\",\"version\":\"[0.1.0-preview.1,2.0)\",\"priority\":" + m.Priority + "}")) + "]}";
        var source = PackLayout.FromModule(new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["manifest.json"] = Encoding.UTF8.GetBytes(json) });
        source[PackPaths.LockFile] = PackLock.Build(pins ?? list.Select(m => Pin(m.Member)));
        return Built(source);
    }

    private (Dictionary<string, byte[]> Coding, List<Dictionary<string, byte[]>> Members) Coding()
    {
        var members = CodingMembers.Select(f => Built(FromManifest(f))).ToList();
        var coding = FromManifest("Coding", "composite.json");
        coding[PackPaths.LockFile] = PackLock.Build(members.Select(Pin));
        return (Built(coding), members);
    }

    private string Save(IReadOnlyDictionary<string, byte[]> files)
    {
        var path = Path.Combine(_root, Id(files) + "-" + Guid.NewGuid().ToString("N")[..6] + ".ampack");
        File.WriteAllBytes(path, PackBuilder.Pack(files));
        return path;
    }

    [Fact]
    public void A_family_of_families_loads_from_one_set_and_composes_with_slot_priorities()
    {
        var (coding, codingMembers) = Coding();
        var martialArts = MartialArts();
        var workshop = Composite("io.agentmemory.packs.workshop", [(coding, 1000), (martialArts, 50)]);
        var carried = new Dictionary<string, IReadOnlyDictionary<string, byte[]>>(StringComparer.Ordinal) { [Id(workshop)] = workshop, [Id(coding)] = coding, [MartialArtsId] = martialArts };
        foreach (var member in codingMembers) carried[Id(member)] = member;

        var set = PackFamilies.LoadSet(PackBuilder.Pack(Built(PackSets.Source("io.agentmemory.packs.workshop-set", "1.0.0", carried))), Trust());

        var family = set.Families.Should().ContainSingle().Subject;
        family.Id.Should().Be("io.agentmemory.packs.workshop");
        family.Members.Select(m => m.Id).Should().Equal(MartialArtsId);
        family.Families.Should().ContainSingle().Which.Members.Should().HaveCount(4);
        set.AllPacks.Should().HaveCount(5);
        set.AllFamilies.Select(f => f.Id).Should().Equal("io.agentmemory.packs.workshop", CodingPack.Manifest.Id);
        set.Packs.Should().BeEmpty();

        var builder = new ModuleSetBuilder().AddFamily(family);
        builder.Modules.Select(m => m.Manifest.Id).Should().BeEquivalentTo([.. codingMembers.Select(Id), MartialArtsId]);
        // The code map is 30 inside Coding; Coding's slot in the workshop is 1000, so 1030: never before its slot, in its order.
        builder.Priorities[CodeMapPack.Id].Should().Be(1030);
        builder.Priorities[MartialArtsId].Should().Be(50);
    }

    [Fact]
    public void Switching_a_nested_slot_off_switches_off_everything_under_it()
    {
        var (coding, codingMembers) = Coding();
        var martialArts = MartialArts();
        var workshop = Composite("io.agentmemory.packs.workshop", [(coding, 10), (martialArts, 50)]);
        var family = PackFamilies.LoadComposite(Save(workshop), [Save(coding), Save(martialArts), .. codingMembers.Select(Save)], Trust());

        new ModuleSetBuilder().AddFamily(family, o => o.Disable(CodingPack.Manifest.Id)).Modules.Select(m => m.Manifest.Id).Should().Equal(MartialArtsId);
    }

    [Fact]
    public void Four_composites_deep_loads_and_five_is_refused()
    {
        var leaf = MartialArts();
        var chain = new List<Dictionary<string, byte[]>>();
        IReadOnlyDictionary<string, byte[]> below = leaf;
        foreach (var level in new[] { "four", "three", "two", "one", "zero" })
        {
            var composite = Composite("io.agentmemory.packs.level-" + level, [(below, 10)]);
            chain.Add(composite);
            below = composite;
        }

        var four = PackFamilies.LoadComposite(Save(chain[3]), [Save(leaf), .. chain.Take(3).Select(Save)], Trust());
        four.AllFamilies.Should().HaveCount(3);
        four.AllMembers.Select(m => m.Id).Should().Equal(MartialArtsId);
        new ModuleSetBuilder().AddFamily(four).Modules.Should().ContainSingle();

        var five = () => PackFamilies.LoadComposite(Save(chain[4]), [Save(leaf), .. chain.Take(4).Select(Save)], Trust());
        five.Should().Throw<PackLoadException>().Which.Problems.Should().ContainSingle().Which.Should().Contain("nests deeper than 4 composites");
    }

    [Fact]
    public void Two_locks_that_pin_different_content_for_one_id_refuse_the_family()
    {
        var first = MartialArts();
        var second = MartialArts(otherContent: true);
        var a = Composite("io.agentmemory.packs.alpha", [(first, 10)]);
        var b = Composite("io.agentmemory.packs.beta", [(second, 10)]);
        var top = Composite("io.agentmemory.packs.top", [(a, 10), (b, 20)]);

        var act = () => PackFamilies.LoadComposite(Save(top), [Save(a), Save(b), Save(first)], Trust());

        act.Should().Throw<PackLoadException>().Which.Problems.Should().ContainSingle()
            .Which.Should().Contain("pin different io.agentmemory.packs.martialarts");
    }

    [Fact]
    public void Two_composites_may_share_a_member_as_the_same_content_and_it_is_composed_once()
    {
        var shared = MartialArts();
        var a = Composite("io.agentmemory.packs.alpha", [(shared, 10)]);
        var b = Composite("io.agentmemory.packs.beta", [(shared, 10)]);
        var top = Composite("io.agentmemory.packs.top", [(a, 10), (b, 20)]);

        var family = PackFamilies.LoadComposite(Save(top), [Save(a), Save(b), Save(shared)], Trust());

        family.AllMembers.Select(m => m.Id).Should().Equal(MartialArtsId);
        var builder = new ModuleSetBuilder().AddFamily(family);
        builder.Modules.Should().ContainSingle();
        builder.Priorities[MartialArtsId].Should().Be(20, "from alpha, which adds it first: its slot 10 plus its own 10");
    }

    [Fact]
    public void A_composite_that_contains_itself_is_refused()
    {
        var leaf = MartialArts();
        var b = Composite("io.agentmemory.packs.beta", [(leaf, 10)]);
        var a = Composite("io.agentmemory.packs.alpha", [(b, 10)]);
        // Beta again, now also pinning alpha: a digest can never close such a loop, but ids can.
        var looped = Composite("io.agentmemory.packs.beta", [(leaf, 10), (a, 20)]);

        var act = () => PackFamilies.LoadComposite(Save(a), [Save(looped), Save(leaf)], Trust());

        act.Should().Throw<PackLoadException>().Which.Problems.Should().Contain(p => p.Contains("contains itself"));
    }
}
