using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Packs.Format;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// The host lifecycle for sets (AMPACK-BATTLE-TEST step 3, 24 Nov 2026): a pack two sets carry, as every Mind set carries one
/// values pack, is held by each set that carries it, composed once, and let go only when no installed set holds it; other
/// content under the same id is still refused; a set's newer version lets go of what it no longer carries unless another set
/// holds it; one member of a set is never uninstalled alone.
/// </summary>
public sealed class PackHoldingTests : IDisposable
{
    private const string Values = "io.example.hold.values";
    private const string FactsA = "io.example.hold.factsa";
    private const string FactsB = "io.example.hold.factsb";
    private readonly string _root = Directory.CreateTempSubdirectory("pack-holding-").FullName;
    private readonly PackSigningKey _key = PackAuthoring.CreateKey();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private PackTrust Trust() => PackTrust.Parse(Encoding.UTF8.GetBytes(PackAuthoring.TrustPolicy(_key.PublicKeyPem, "io.example")));

    // A small knowledge pack with an ontology; text changes its record, so the same id and version can carry other content.
    private byte[] Knowledge(string id, string version = "1.0.0", string text = "Honesty first")
    {
        var name = string.Concat(id.Split('.').Last().Select((c, i) => i == 0 ? char.ToUpperInvariant(c) : c));
        var prefix = "Hold" + name;
        var bundle = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["manifest.json"] = Encoding.UTF8.GetBytes($$$$"""
                {"format":"agentmemory.module/v1alpha1","id":"{{{{id}}}}","version":"{{{{version}}}}","sdk":"[0.1,0.2)","seed":["seed/records.jsonl"],
                 "ontology":{"labelPrefix":"{{{{prefix}}}}","types":[{"name":"{{{{prefix}}}}Note","scope":"shared","render":"{text}","properties":{"text":{"type":"string","required":true}}}]},
                 "views":[{"id":"notes","type":"{{{{prefix}}}}Note","search":{"properties":["text"]},"limit":5}]}
                """.ReplaceLineEndings("\n")),
            ["seed/records.jsonl"] = Encoding.UTF8.GetBytes($$$"""{"type":"{{{prefix}}}Note","key":"n1","properties":{"text":"{{{text}}}"}}""" + "\n"),
        };
        return PackAuthoring.Build(PackAuthoring.FromModuleBundle(bundle), _key.PrivateKeyPem);
    }

    private byte[] Set(string id, string version, params byte[][] members) => PackAuthoring.BuildSet(id, version, members, _key.PrivateKeyPem, Trust());

    [Fact]
    public void Two_sets_carrying_the_same_pack_both_install_and_each_holds_it()
    {
        var store = PackStore.Open(_root);
        var values = Knowledge(Values);
        store.Install(Set("io.example.hold.set-a", "1.0.0", values, Knowledge(FactsA)), Trust());

        var b = store.Install(Set("io.example.hold.set-b", "1.0.0", values, Knowledge(FactsB)), Trust());

        b.Members.Should().Equal(FactsB, Values);
        store.Holders(Values).Should().Equal("io.example.hold.set-a", "io.example.hold.set-b");
        store.Holders(FactsA).Should().Equal("io.example.hold.set-a");
        var (packs, _) = PackMemory.Mount(store.Archives(), Trust());
        packs.Select(p => p.Id).Should().BeEquivalentTo([Values, FactsA, FactsB], "the shared pack is held once");
    }

    [Fact]
    public void A_set_carrying_other_content_under_an_installed_pack_s_id_is_still_refused()
    {
        var store = PackStore.Open(_root);
        store.Install(Set("io.example.hold.set-a", "1.0.0", Knowledge(Values), Knowledge(FactsA)), Trust());

        FluentActions.Invoking(() => store.Install(Set("io.example.hold.set-b", "1.0.0", Knowledge(Values, text: "Kindness first"), Knowledge(FactsB)), Trust()))
            .Should().Throw<PackLoadException>().Which.Problems.Should().Contain(p => p.Contains(Values, StringComparison.Ordinal));
        store.Installed.Should().ContainSingle();
    }

    [Fact]
    public void Uninstalling_a_set_lets_go_only_of_what_no_other_installed_set_holds()
    {
        var store = PackStore.Open(_root);
        var values = Knowledge(Values);
        store.Install(Set("io.example.hold.set-a", "1.0.0", values, Knowledge(FactsA)), Trust());
        store.Install(Set("io.example.hold.set-b", "1.0.0", values, Knowledge(FactsB)), Trust());

        store.Uninstall("io.example.hold.set-a", out var released).Should().BeTrue();
        released.Should().Equal(FactsA);
        store.Holders(Values).Should().Equal("io.example.hold.set-b");

        store.Uninstall("io.example.hold.set-b", out released).Should().BeTrue();
        released.Should().Equal(FactsB, Values);
    }

    [Fact]
    public void A_set_s_newer_version_lets_go_of_what_it_no_longer_carries_unless_another_set_holds_it()
    {
        var store = PackStore.Open(_root);
        var values = Knowledge(Values);
        store.Install(Set("io.example.hold.set-a", "1.0.0", values, Knowledge(FactsA)), Trust());
        store.Install(Set("io.example.hold.set-b", "1.0.0", values, Knowledge(FactsB)), Trust());

        store.Install(Set("io.example.hold.set-a", "1.1.0", Knowledge(FactsA)), Trust()).Released.Should().BeEmpty("set-b still holds the values");
        store.Install(Set("io.example.hold.set-a", "1.2.0", values), Trust()).Released.Should().Equal(FactsA);
        store.Holders(Values).Should().Equal("io.example.hold.set-a", "io.example.hold.set-b");
    }

    [Fact]
    public void One_member_of_a_set_is_never_uninstalled_alone()
    {
        var store = PackStore.Open(_root);
        store.Install(Set("io.example.hold.set-a", "1.0.0", Knowledge(Values), Knowledge(FactsA)), Trust());

        store.Uninstall(FactsA, out var released).Should().BeFalse("a set is installed and removed whole; a host switches a member off instead");
        released.Should().BeEmpty();
        store.Holders(FactsA).Should().Equal("io.example.hold.set-a");
    }

    [Fact]
    public void A_host_composes_a_pack_two_sets_carry_once_and_refuses_two_contents_for_one_id()
    {
        var values = Knowledge(Values);
        var a = PackFamilies.LoadSet(Set("io.example.hold.set-a", "1.0.0", values, Knowledge(FactsA)), Trust());
        var b = PackFamilies.LoadSet(Set("io.example.hold.set-b", "1.0.0", values, Knowledge(FactsB)), Trust());

        PackFamilies.HostComposes([a, b], []).Select(p => p.Id).Should().BeEquivalentTo([Values, FactsA, FactsB]);
        new ModuleSetBuilder().AddPack(a.Packs.Single(p => p.Id == Values)).AddPack(b.Packs.Single(p => p.Id == Values))
            .Modules.Should().ContainSingle(m => m.Manifest.Id == Values);

        var other = PackFamilies.LoadSet(Set("io.example.hold.set-c", "1.0.0", Knowledge(Values, text: "Kindness first")), Trust());
        FluentActions.Invoking(() => PackFamilies.HostComposes([a, other], [])).Should().Throw<PackLoadException>()
            .Which.Problems.Should().ContainSingle(p => p.Contains(Values, StringComparison.Ordinal));
    }

    // A set carrying one pack, valid until an instant: a set's end caps every pack it carries.
    private byte[] SetEnding(string id, DateTimeOffset until, byte[] member)
    {
        var source = PackSets.Source(id, "1.0.0", new Dictionary<string, IReadOnlyDictionary<string, byte[]>>(StringComparer.Ordinal)
        {
            [Values] = StrictZip.Read(member, PackLimits.Default),
        });
        var manifest = JsonNode.Parse(source["pack.json"])!.AsObject();
        manifest["validUntil"] = until.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        source["pack.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        var (files, _) = PackBuilder.Build(source);
        PackBuilder.Sign(files, _key.PrivateKeyPem, "publisher", DateTimeOffset.UtcNow.AddMinutes(-1));
        return PackBuilder.Pack(files);
    }

    [Fact]
    public void A_pack_two_sets_hold_stays_valid_until_the_later_of_their_ends_whatever_the_order()
    {
        var values = Knowledge(Values);
        var soon = SetEnding("io.example.hold.set-a", DateTimeOffset.UtcNow.AddDays(10), values);
        var later = SetEnding("io.example.hold.set-b", DateTimeOffset.UtcNow.AddDays(40), values);
        DateTimeOffset? Held(params byte[][] archives) => PackMemory.Mount(archives, Trust()).Packs.Should().ContainSingle().Subject.ValidUntil;

        Held(soon, later).Should().BeCloseTo(DateTimeOffset.UtcNow.AddDays(40), TimeSpan.FromMinutes(2), "held while any set that holds it is valid");
        Held(later, soon).Should().Be(Held(soon, later), "one rule, whatever the order");
    }
}
