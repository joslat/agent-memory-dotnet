using System.Text;
using System.Text.Json.Nodes;
using AgentMemory.Extensibility.Ampack;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// A host's pack store (PACK-toolset, <c>ampack install</c>): a pack is verified in full before anything is written; a newer
/// version replaces the installed one; the ledger outlives removal, so an older version is refused even after the newer one
/// is uninstalled; a pack set installs as one; the store reopens as it was.
/// </summary>
public sealed class PackStoreTests : IDisposable
{
    private static readonly string MartialArts = Path.Combine(AppContext.BaseDirectory, "Packs", "MartialArts");
    private readonly string _root = Directory.CreateTempSubdirectory("pack-store-").FullName;
    private readonly PackSigningKey _key = PackAuthoring.CreateKey();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private PackTrust Trust() => PackTrust.Parse(Encoding.UTF8.GetBytes(PackAuthoring.TrustPolicy(_key.PublicKeyPem, "io.agentmemory", "io.example")));

    private byte[] MartialArtsPack(string version)
    {
        var source = new Dictionary<string, byte[]>(PackAuthoring.FromModuleBundle(Directory.EnumerateFiles(MartialArts, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(MartialArts, f).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllBytes, StringComparer.Ordinal)), StringComparer.Ordinal);
        var pack = JsonNode.Parse(source["pack.json"])!.AsObject();
        pack["version"] = version;
        source["pack.json"] = Encoding.UTF8.GetBytes(pack.ToJsonString());
        return PackAuthoring.Build(source, _key.PrivateKeyPem);
    }

    [Fact]
    public void A_newer_version_replaces_the_installed_one_and_an_older_one_is_refused_even_after_removal()
    {
        var store = PackStore.Open(_root);
        var first = store.Install(MartialArtsPack("1.0.0"), Trust());
        var second = store.Install(MartialArtsPack("1.1.0"), Trust());

        store.Installed.Should().ContainSingle().Which.Should().Be(second);
        File.Exists(Path.Combine(_root, first.File)).Should().BeFalse("the older file is replaced");
        File.Exists(Path.Combine(_root, second.File)).Should().BeTrue();

        store.Uninstall(second.Id).Should().BeTrue();
        store.Installed.Should().BeEmpty();
        var reopened = PackStore.Open(_root);
        FluentActions.Invoking(() => reopened.Install(MartialArtsPack("1.0.0"), Trust())).Should().Throw<PackLoadException>()
            .Which.Problems.Should().Contain(p => p.Contains("older than 1.1.0", StringComparison.Ordinal));
        reopened.Install(MartialArtsPack("1.0.0"), Trust(), allowDowngrade: true).Version.Should().Be("1.0.0", "a host may choose to go back");
    }

    [Fact]
    public void A_refused_pack_writes_nothing()
    {
        var store = PackStore.Open(_root);
        var untrusted = PackTrust.Parse(Encoding.UTF8.GetBytes(PackAuthoring.TrustPolicy(PackAuthoring.CreateKey().PublicKeyPem, "io.agentmemory")));

        FluentActions.Invoking(() => store.Install(MartialArtsPack("1.0.0"), untrusted)).Should().Throw<PackLoadException>();

        store.Installed.Should().BeEmpty();
        Directory.EnumerateFiles(Path.Combine(_root, "packs")).Should().BeEmpty();
        File.Exists(Path.Combine(_root, "ledger.json")).Should().BeFalse("nothing was recorded either");
    }

    [Fact]
    public void A_pack_set_installs_as_one_and_mounts_from_the_store()
    {
        var knowledge = MartialArtsPack("1.0.0");
        var set = PackAuthoring.BuildSet("io.example.dojo-set", "1.0.0", [knowledge], _key.PrivateKeyPem);
        var store = PackStore.Open(_root);

        store.Install(set, Trust()).Kind.Should().Be("set");

        var (packs, memory) = PackMemory.Mount(PackStore.Open(_root).Archives(), Trust());
        packs.Should().ContainSingle().Which.Id.Should().Be("io.agentmemory.packs.martialarts");
        memory.Count.Should().Be(93);
    }

    [Fact]
    public void A_composite_alone_is_not_installed()
    {
        var composite = PackAuthoring.BuildComposite(new Dictionary<string, byte[]>(PackAuthoring.FromModuleBundle(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["manifest.json"] = Encoding.UTF8.GetBytes("""{"format":"agentmemory.composite/v1alpha1","id":"io.example.family","version":"1.0.0","members":[{"id":"io.agentmemory.packs.martialarts","version":"[1,2)"}]}"""),
        }), StringComparer.Ordinal), [MartialArtsPack("1.0.0")], _key.PrivateKeyPem);

        FluentActions.Invoking(() => PackStore.Open(_root).Install(composite, Trust())).Should().Throw<PackLoadException>()
            .Which.Problems.Should().ContainSingle().Which.Should().Contain("pack set");
    }
}
