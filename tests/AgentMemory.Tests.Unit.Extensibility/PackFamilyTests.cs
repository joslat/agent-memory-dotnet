using System.Text;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Packs.CodeMap;
using AgentMemory.Packs.Coding;
using AgentMemory.Packs.Decisions;
using AgentMemory.Packs.Format;
using AgentMemory.Packs.Repository;
using AgentMemory.Packs.Work;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// Compound and hierarchical packs in the file format (pack format v1.2 §5, PACK-composite): the Coding family as a composite
/// pack with a lock and as one pack set composes exactly as the Coding composite assembly does; a member that is missing,
/// extra, swapped for other content, or changed inside a set refuses the family.
/// </summary>
public sealed class PackFamilyTests : IDisposable
{
    private static readonly string Packs = Path.Combine(AppContext.BaseDirectory, "Packs");
    private static readonly string[] MemberFolders = ["Decisions", "Repository", "CodeMap", "Work"];
    private readonly string _root = Directory.CreateTempSubdirectory("pack-family-").FullName;
    private readonly PackKey _key = PackKey.Create();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private PackTrust Trust() => PackTrust.Parse(Encoding.UTF8.GetBytes(PackAuthoring.TrustPolicy(_key.PublicKeyPem, "io.agentmemory.packs")));

    private Dictionary<string, byte[]> Built(Dictionary<string, byte[]> source)
    {
        var (files, _) = PackBuilder.Build(source);
        PackBuilder.Sign(files, _key.PrivateKeyPem, "publisher", DateTimeOffset.UtcNow.AddMinutes(-1));
        return files;
    }

    private static Dictionary<string, byte[]> FromManifest(string folder, string file = "manifest.json") =>
        PackLayout.FromModule(new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["manifest.json"] = File.ReadAllBytes(Path.Combine(Packs, folder, file)) });

    private static string Id(IReadOnlyDictionary<string, byte[]> files) => FilesList.Parse(files[PackPaths.FilesList]).List!.Id;

    // The four Coding members, built and signed, and the composite locked to exactly them.
    private (Dictionary<string, byte[]> Composite, List<Dictionary<string, byte[]>> Members) Family()
    {
        var members = MemberFolders.Select(f => Built(FromManifest(f))).ToList();
        var composite = FromManifest("Coding", "composite.json");
        composite[PackPaths.LockFile] = PackLock.Build(members.Select(m =>
        {
            var list = FilesList.Parse(m[PackPaths.FilesList]).List!;
            return new LockEntry(list.Id, list.Version, FilesList.DigestOf(m[PackPaths.FilesList]));
        }));
        return (Built(composite), members);
    }

    private string Save(string name, IReadOnlyDictionary<string, byte[]> files)
    {
        var path = Path.Combine(_root, name + ".ampack");
        File.WriteAllBytes(path, PackBuilder.Pack(files));
        return path;
    }

    [Fact]
    public void The_coding_family_as_a_composite_pack_composes_as_the_coding_assembly_does()
    {
        var (composite, members) = Family();

        var family = PackFamilies.LoadComposite(Save("coding", composite), members.Select(m => Save(Id(m), m)), Trust());
        var fromPacks = new ModuleSetBuilder().AddFamily(family);
        var fromAssembly = new ModuleSetBuilder().AddComposite<CodingPack>();

        family.Id.Should().Be(CodingPack.Manifest.Id);
        fromPacks.Modules.Select(m => m.Manifest.Id).Should().Equal(fromAssembly.Modules.Select(m => m.Manifest.Id));
        fromPacks.Priorities.Should().BeEquivalentTo(fromAssembly.Priorities);
        fromPacks.Modules.Select(m => PackTrialGate.DigestOf(m.Manifest)).Should().Equal(fromAssembly.Modules.Select(m => PackTrialGate.DigestOf(m.Manifest)));
    }

    [Fact]
    public void A_coding_member_outside_the_composite_s_range_is_refused()
    {
        // CP-acceptance: the Coding composite pins each member to [0.1.0-preview.1,0.2); a 0.2.0 work pack does not compose.
        var work = WorkPack.Manifest with { Version = ModuleVersion.Parse("0.2.0") };

        FluentActions.Invoking(() => new ModuleSetBuilder().AddComposite(CodingPack.Manifest, members => members.Add(work, _ => { })))
            .Should().Throw<InvalidOperationException>().WithMessage($"*accepts {WorkPack.Id} *not 0.2.0*");
        new ModuleSetBuilder().AddComposite(CodingPack.Manifest, members => members.Add(DecisionsPack.Manifest, _ => { }).Add(RepositoryPack.Manifest, _ => { })
                .Add(CodeMapPack.Manifest, _ => { }).Add(WorkPack.Manifest, _ => { }))
            .Modules.Should().HaveCount(4, "the members as shipped are inside the range");
    }

    [Fact]
    public void A_host_switches_members_of_a_pack_family_as_of_any_composite()
    {
        var (composite, members) = Family();
        var family = PackFamilies.LoadComposite(Save("coding", composite), members.Select(m => Save(Id(m), m)), Trust());

        var builder = new ModuleSetBuilder().AddFamily(family, o => o.Disable(RepositoryPack.Id).Prioritize(CodeMapPack.Id, 5));

        builder.Modules.Select(m => m.Manifest.Id).Should().Equal(DecisionsPack.Id, CodeMapPack.Id, WorkPack.Id);
        builder.Priorities[CodeMapPack.Id].Should().Be(5);
    }

    [Fact]
    public void A_missing_member_refuses_the_family()
    {
        var (composite, members) = Family();
        var act = () => PackFamilies.LoadComposite(Save("coding", composite), members.Skip(1).Select(m => Save(Id(m), m)), Trust());
        act.Should().Throw<PackLoadException>().Which.Problems.Should().ContainSingle().Which.Should().Contain("which is missing");
    }

    [Fact]
    public void A_member_swapped_for_other_content_refuses_the_family()
    {
        var (composite, members) = Family();
        // The same id and version, rebuilt with one more containableBy entry: valid, signed, and not what the lock pins.
        var other = FromManifest("CodeMap");
        other["pack.json"] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(other["pack.json"]).Replace("\"io.agentmemory.packs.work\"", "\"io.agentmemory.packs.work\", \"io.agentmemory.packs.other\"", StringComparison.Ordinal));
        var swapped = members.Select(m => Id(m) == CodeMapPack.Id ? Built(other) : m).ToList();

        var act = () => PackFamilies.LoadComposite(Save("coding", composite), swapped.Select(m => Save(Id(m), m)), Trust());

        act.Should().Throw<PackLoadException>().Which.Problems.Should().ContainSingle().Which.Should().Contain("pins io.agentmemory.packs.codemap");
    }

    [Fact]
    public void An_extra_member_refuses_the_family()
    {
        var (composite, members) = Family();
        var stranger = Built(PackLayout.FromModule(Directory.EnumerateFiles(Path.Combine(Packs, "MartialArts"), "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(Path.Combine(Packs, "MartialArts"), f).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllBytes, StringComparer.Ordinal)));

        var act = () => PackFamilies.LoadComposite(Save("coding", composite), members.Append(stranger).Select(m => Save(Id(m), m)), Trust());

        act.Should().Throw<PackLoadException>().Which.Problems.Should().ContainSingle().Which.Should().Contain("is not a member of");
    }

    [Fact]
    public void The_family_as_one_pack_set_loads_and_composes_the_same()
    {
        var (composite, members) = Family();
        var carried = new Dictionary<string, IReadOnlyDictionary<string, byte[]>>(StringComparer.Ordinal) { [CodingPack.Manifest.Id] = composite };
        foreach (var member in members) carried[Id(member)] = member;
        var set = Built(PackSets.Source("io.agentmemory.packs.coding-family", "0.1.0", carried));

        var loaded = PackFamilies.LoadSet(PackBuilder.Pack(set), Trust());

        loaded.Families.Should().ContainSingle().Which.Members.Should().HaveCount(4);
        loaded.Packs.Should().BeEmpty();
        new ModuleSetBuilder().AddFamily(loaded.Families[0]).Modules.Select(m => m.Manifest.Id)
            .Should().Equal(new ModuleSetBuilder().AddComposite<CodingPack>().Modules.Select(m => m.Manifest.Id));
    }

    [Fact]
    public void A_member_changed_inside_a_set_is_refused_even_when_the_set_is_resigned()
    {
        var (composite, members) = Family();
        var carried = new Dictionary<string, IReadOnlyDictionary<string, byte[]>>(StringComparer.Ordinal) { [CodingPack.Manifest.Id] = composite };
        foreach (var member in members) carried[Id(member)] = member;
        var source = PackSets.Source("io.agentmemory.packs.coding-family", "0.1.0", carried);
        var path = $"packs/{CodeMapPack.Id}/traversal.json";
        source[path] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(source[path]).Replace("\"maxItems\": 3", "\"maxItems\": 16", StringComparison.Ordinal));

        // The set's own signature is fresh; the member's list and signature still name the old bytes. The writer refuses to write
        // it (review 8, F2: each member's own checks need no trust), and a reader given those bytes anyway refuses them.
        var built = Built(source);
        FluentActions.Invoking(() => PackBuilder.Pack(built)).Should().Throw<InvalidDataException>().WithMessage($"*packs/{CodeMapPack.Id}/*");
        var act = () => PackFamilies.LoadSet(StrictZip.Write(built), Trust());

        act.Should().Throw<PackLoadException>().Which.Problems.Should().Contain(p => p.StartsWith($"packs/{CodeMapPack.Id}/", StringComparison.Ordinal));
    }
}
