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
    private (Dictionary<string, byte[]> Composite, List<Dictionary<string, byte[]>> Members) Family(Func<string, string>? edit = null,
        string? policyFor = null)
    {
        // policyFor: the member folder that carries a usage policy (feature 10).
        var members = MemberFolders.Select(f =>
        {
            var source = FromManifest(f);
            if (f == policyFor) source["policy.json"] = Encoding.UTF8.GetBytes("""{"format":"agentmemory.pack.policy/1","rules":[{"allow":["read","rehydrate:local"],"audience":["*"]}]}""");
            return Built(source);
        }).ToList();
        var composite = edit is null ? FromManifest("Coding", "composite.json") : PackLayout.FromModule(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["manifest.json"] = Encoding.UTF8.GetBytes(edit(File.ReadAllText(Path.Combine(Packs, "Coding", "composite.json")))),
        });
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
    public void A_family_member_s_usage_policy_is_held_as_an_added_pack_s_is()
    {
        // Feature 10, review H2: members were composed with no policy (and no erasures), so their records were offered freely.
        var (composite, members) = Family(policyFor: "CodeMap");
        var family = PackFamilies.LoadComposite(Save("coding", composite), members.Select(m => Save(Id(m), m)), Trust());
        var member = family.AllMembers.Single(p => p.Id == CodeMapPack.Id);
        member.PolicyDigest.Should().StartWith("sha256:");
        new ModuleSetBuilder().AddFamily(family).PackPolicies.Should().ContainSingle(p => p.Digest == member.PolicyDigest);
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
    public void Review11_serve_refuses_a_family_the_host_refuses()
    {
        // R11-2: with codemap switched off, a host composing the family refuses it ("work needs codemap, which is switched off"),
        // while serve rebuilt the composition and served work. Serve now runs the host's composition, and refuses as it does.
        var (composite, members) = Family(json =>
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
            foreach (var member in node["members"]!.AsArray())
                if ((string?)member!["id"] == CodeMapPack.Id) member["enabled"] = false;
            return node.ToJsonString();
        });
        var carried = new Dictionary<string, IReadOnlyDictionary<string, byte[]>>(StringComparer.Ordinal) { [CodingPack.Manifest.Id] = composite };
        foreach (var member in members) carried[Id(member)] = member;
        var set = PackBuilder.Pack(Built(PackSets.Source("io.agentmemory.packs.coding-family", "0.1.0", carried)));

        var loaded = PackFamilies.LoadSet(set, Trust());
        var host = FluentActions.Invoking(() => new ModuleSetBuilder().AddFamily(loaded.Families[0])).Should().Throw<InvalidOperationException>().Which.Message;
        host.Should().Contain("switched off");
        FluentActions.Invoking(() => PackMemory.Mount([set], Trust())).Should().Throw<PackLoadException>()
            .Which.Problems.Should().ContainSingle().Which.Should().Be(host, "serve refuses with the host's own words");
    }

    [Fact]
    public void Review12_a_pack_a_family_carries_switched_off_is_held_once_when_given_on_its_own()
    {
        // R12-1: the mount plan refused what was only carried twice ("given more than once"), while a host composing the family,
        // with repository off, and repository on its own, holds it once. Every door decides on what is composed.
        var (composite, members) = Family(json =>
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
            foreach (var member in node["members"]!.AsArray())
                if ((string?)member!["id"] == RepositoryPack.Id) member["enabled"] = false;
            return node.ToJsonString();
        });
        var carried = new Dictionary<string, IReadOnlyDictionary<string, byte[]>>(StringComparer.Ordinal) { [CodingPack.Manifest.Id] = composite };
        foreach (var member in members) carried[Id(member)] = member;
        var set = PackBuilder.Pack(Built(PackSets.Source("io.agentmemory.packs.coding-family", "0.1.0", carried)));
        var repository = PackBuilder.Pack(members.Single(m => Id(m) == RepositoryPack.Id));

        var held = PackMemory.Mount([set, repository], Trust()).Packs;
        held.Count(p => p.Id == RepositoryPack.Id).Should().Be(1);
        held.Select(p => p.Id).Should().BeEquivalentTo(PackFamilies.HostComposes([PackFamilies.LoadSet(set, Trust())], [PackLoader.Load(repository, Trust())]).Select(p => p.Id));
        // R12-2: the store installs only what a host composes.
        var store = PackStore.Open(Path.Combine(_root, "store"));
        var (off, offMembers) = Family(json =>
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
            foreach (var member in node["members"]!.AsArray())
                if ((string?)member!["id"] == CodeMapPack.Id) member["enabled"] = false;
            return node.ToJsonString();
        });
        var refused = new Dictionary<string, IReadOnlyDictionary<string, byte[]>>(StringComparer.Ordinal) { [CodingPack.Manifest.Id] = off };
        foreach (var member in offMembers) refused[Id(member)] = member;
        FluentActions.Invoking(() => store.Install(PackBuilder.Pack(Built(PackSets.Source("io.agentmemory.packs.coding-off", "0.1.0", refused))), Trust()))
            .Should().Throw<PackLoadException>().Which.Problems.Should().Contain(p => p.Contains("switched off"));
        store.Installed.Should().BeEmpty("nothing is written when a host would refuse it");
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
