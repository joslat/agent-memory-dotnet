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
        LoadedFamily R(bool aOn) => Family(Composite("io.example.r", ("io.example.a", aOn), (x, true)), [pack with { ValidUntil = T2 }],
            Family(Composite("io.example.a", (x, true)), [pack with { ValidUntil = T1 }]));

        PackFamilies.Composed(R(aOn: true)).Should().ContainSingle().Which.ValidUntil.Should().Be(T1, "the first enabled route, as the host composes it");
        PackFamilies.Composed(R(aOn: false)).Should().ContainSingle().Which.ValidUntil.Should().Be(T2, "a route switched off does not count");
    }
}
