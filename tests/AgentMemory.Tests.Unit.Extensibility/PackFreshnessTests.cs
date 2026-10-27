using System.Text;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Packs.CodeMap;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// PACK-staleness: a code map snapshot pack built at one commit, on a host at another, says so; one that names no commit says
/// that nothing can be said.
/// </summary>
public sealed class PackFreshnessTests
{
    private const string Built = "0a1b2c3d4e5f";
    private readonly PackSigningKey _key = PackAuthoring.CreateKey();

    private LoadedPack Snapshot(string? commit)
    {
        var builder = new CodeMapBuilder();
        builder.AddProject("src/Map/Map.csproj", "Acme.Map");
        builder.AddFile("src/Map/MapStore.cs", "src/Map/Map.csproj");
        var source = new Dictionary<string, byte[]>(PackAuthoring.FromSnapshot(builder.Build("git:local"), "io.example.acme-map", "1.0.0", commit: commit),
            StringComparer.Ordinal);
        return PackLoader.Load(PackAuthoring.Build(source, _key.PrivateKeyPem),
            PackTrust.Parse(Encoding.UTF8.GetBytes(PackAuthoring.TrustPolicy(_key.PublicKeyPem, "io.example"))));
    }

    [Fact]
    public void A_snapshot_built_at_the_hosts_commit_is_fresh_and_at_another_is_stale()
    {
        var snapshot = Snapshot(Built);

        snapshot.Freshness("git:" + Built + "6789abcdef0123456789abcdef01234567").Fresh.Should().BeTrue("the pack names the head by a prefix");
        var stale = snapshot.Freshness("9f8e7d6c5b4a39281706f5e4d3c2b1a098765432");
        stale.Fresh.Should().BeFalse();
        stale.BuiltAt.Should().Be(Built);
        stale.Detail.Should().Contain("stale").And.Contain("agentmemory codemap pack");
    }

    [Fact]
    public void A_snapshot_that_names_no_commit_cannot_be_judged()
    {
        var freshness = Snapshot(commit: null).Freshness("9f8e7d6");

        freshness.Fresh.Should().BeNull();
        freshness.BuiltAt.Should().BeNull();
        freshness.Detail.Should().Contain("unknown");
    }

    [Fact]
    public void Only_a_snapshot_against_a_commit_is_judged()
    {
        var snapshot = Snapshot(Built);

        FluentActions.Invoking(() => snapshot.Freshness("main")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => snapshot.Freshness("abc")).Should().Throw<ArgumentException>();
        var definition = PackLoader.Load(PackAuthoring.Build(PackAuthoring.FromModuleBundle(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["manifest.json"] = Encoding.UTF8.GetBytes("""{"format":"agentmemory.module/v1alpha1","id":"io.example.notes","version":"1.0.0","sdk":"[0.1,0.2)"}"""),
        }), _key.PrivateKeyPem), PackTrust.Parse(Encoding.UTF8.GetBytes(PackAuthoring.TrustPolicy(_key.PublicKeyPem, "io.example"))));
        FluentActions.Invoking(() => definition.Freshness(Built)).Should().Throw<ArgumentException>();
    }
}
