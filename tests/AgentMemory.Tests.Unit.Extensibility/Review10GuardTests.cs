#pragma warning disable AMEXT001
using System.Text;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Extensibility.Modules;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>Review 10's findings, each guarded where the rule lives; eng/guards/mutants.json records the breakage each must catch.</summary>
public sealed class Review10GuardTests
{
    private static readonly string MartialArts = Path.Combine(AppContext.BaseDirectory, "Packs", "MartialArts");
    private static readonly DateTimeOffset T1 = new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), T2 = new(2028, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly PackSigningKey _key = PackAuthoring.CreateKey();

    private LoadedPack Pack() => PackLoader.Load(PackAuthoring.Build(new Dictionary<string, byte[]>(PackAuthoring.FromModuleBundle(
        Directory.EnumerateFiles(MartialArts, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(MartialArts, f).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllBytes, StringComparer.Ordinal)), StringComparer.Ordinal),
        _key.PrivateKeyPem), PackTrust.Parse(Encoding.UTF8.GetBytes(PackAuthoring.TrustPolicy(_key.PublicKeyPem, "io.agentmemory"))));

    private static CompositeManifest Composite(string id, params (string Id, bool Enabled)[] members) => CompositeManifest.Parse(
        "{\"format\":\"agentmemory.composite/v1alpha1\",\"id\":\"" + id + "\",\"version\":\"1.0.0\",\"members\":["
        + string.Join(",", members.Select(m => "{\"id\":\"" + m.Id + "\",\"version\":\"[0,99)\",\"enabled\":" + (m.Enabled ? "true" : "false") + "}")) + "]}");

    private static LoadedFamily Family(CompositeManifest manifest, IReadOnlyList<LoadedPack> members, params LoadedFamily[] families) => new()
    {
        Id = manifest.Id, Version = "1.0.0", Digest = "sha256:" + new string('0', 64), Manifest = manifest, Members = members, Families = families,
    };

    [Fact]
    public void A_M2_the_writer_never_signs_at_a_time_every_reader_refuses()
    {
        // A future SOURCE_DATE_EPOCH: build said "signed 1" and wrote a pack every reader refused ("a time in the future").
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["pack.json"] = "{\"format\":\"agentmemory.pack/1.2\",\"id\":\"io.example.later\",\"version\":\"1.0.0\",\"kind\":\"knowledge\",\"sdk\":\"[0.1,0.2)\"}"u8.ToArray(),
        };
        files[AgentMemory.Packs.Format.PackPaths.FilesList] = AgentMemory.Packs.Format.FilesList.Build("io.example.later", "1.0.0", files);
        FluentActions.Invoking(() => AgentMemory.Packs.Format.PackBuilder.Sign(files, _key.PrivateKeyPem, "publisher", DateTimeOffset.UtcNow.AddHours(1)))
            .Should().Throw<InvalidDataException>().WithMessage("*in the future*");
        AgentMemory.Packs.Format.PackBuilder.Sign(files, _key.PrivateKeyPem, "publisher", DateTimeOffset.UtcNow.AddMinutes(-1)).Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void C_M2_a_family_member_counts_through_the_route_a_host_composes_it_by()
    {
        // R holds [A, X] and A holds [X]: a host composes X through A, the first route in member order, with A's end (T1). Serve kept
        // whichever copy came first in another order, so it served X until R's end (T2) after the host had switched it off.
        var pack = Pack();
        var x = pack.Id;
        LoadedSet R(bool aOn) => Set(Family(Composite("io.example.r", ("io.example.a", aOn), (x, true)), [pack with { ValidUntil = T2 }],
            Family(Composite("io.example.a", (x, true)), [pack with { ValidUntil = T1 }])));

        PackFamilies.HostComposes([R(aOn: true)], []).Should().ContainSingle().Which.ValidUntil.Should().Be(T1, "the first enabled route, as the host composes it");
        PackFamilies.HostComposes([R(aOn: false)], []).Should().ContainSingle().Which.ValidUntil.Should().Be(T2, "a route switched off does not count");
    }

    [Fact]
    public void R11_every_door_holds_what_the_host_composes_and_refuses_what_it_refuses()
    {
        var pack = Pack();
        var x = pack.Id;
        LoadedSet WithX(string family) => Set(Family(Composite(family, (x, true)), [pack]));

        // R11-1: one module reached through two sets, or a set and on its own: the host refuses it, whatever the packaging.
        FluentActions.Invoking(() => PackFamilies.HostComposes([WithX("io.example.f1"), WithX("io.example.f2")], [])).Should().Throw<PackLoadException>()
            .Which.Problems.Should().Contain(p => p.Contains("composed more than once"));
        FluentActions.Invoking(() => PackFamilies.HostComposes([WithX("io.example.f1")], [pack])).Should().Throw<PackLoadException>();

        // R11-2: a family pinning its member outside the range it declares is refused, not composed.
        var narrow = CompositeManifest.Parse("{\"format\":\"agentmemory.composite/v1alpha1\",\"id\":\"io.example.n\",\"version\":\"1.0.0\",\"members\":[{\"id\":\""
            + x + "\",\"version\":\"[50,60)\"}]}");
        FluentActions.Invoking(() => PackFamilies.HostComposes([Set(Family(narrow, [pack]))], [])).Should().Throw<PackLoadException>()
            .Which.Problems.Should().Contain(p => p.Contains("accepts"));

        // A member switched off is not held.
        PackFamilies.HostComposes([Set(Family(Composite("io.example.off", (x, false)), [pack]))], []).Should().BeEmpty();
    }

    [Fact]
    public void R12_4_each_pack_comes_from_the_route_that_registered_it()
    {
        // X switched off in S1 (its copy with one signer list), on in S2 (another): the host composes S2's X, and every field that
        // travels outside the digest (signers, certificates) must be S2's copy, not the first copy carried.
        var pack = Pack();
        var x = pack.Id;
        var first = pack with { Signers = ["publisher of s1"] };
        var second = pack with { Signers = ["publisher of s2"] };
        LoadedSet S(string id, bool on, LoadedPack copy) => Set(Family(Composite(id, (x, on)), [copy])) with { Id = id };

        PackFamilies.HostComposes([S("io.example.s1", false, first), S("io.example.s2", true, second)], [])
            .Should().ContainSingle().Which.Signers.Should().Equal(["publisher of s2"]);
    }

    private static LoadedSet Set(params LoadedFamily[] families) => new()
    {
        Id = "io.example.set", Version = "1.0.0", Digest = "sha256:" + new string('0', 64), Families = families,
    };
}
