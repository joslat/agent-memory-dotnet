using System.IO.Compression;
using System.Text;
using AgentMemory.Extensibility.Ampack;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// What the pack composer shows (PACK-composer, phase 1): a pack's identity, every verification step, its declarations, and
/// the pack mounted in memory to ask; a changed or untrusted pack shows the step that refused it and loads nothing.
/// </summary>
public sealed class PackInspectionTests
{
    private static readonly string MartialArts = Path.Combine(AppContext.BaseDirectory, "Packs", "MartialArts");
    private readonly PackSigningKey _key = PackAuthoring.CreateKey();

    private PackTrust Trust(PackSigningKey? key = null) =>
        PackTrust.Parse(Encoding.UTF8.GetBytes(PackAuthoring.TrustPolicy((key ?? _key).PublicKeyPem, "io.agentmemory", "io.example")));

    private byte[] Archive() => PackAuthoring.Build(new Dictionary<string, byte[]>(PackAuthoring.FromModuleBundle(
        Directory.EnumerateFiles(MartialArts, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(MartialArts, f).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllBytes, StringComparer.Ordinal)), StringComparer.Ordinal),
        _key.PrivateKeyPem);

    [Fact]
    public async Task A_trusted_pack_shows_every_step_passed_its_declarations_and_answers_from_memory()
    {
        var inspection = PackInspection.Inspect(Archive(), Trust());

        inspection.Loaded.Should().BeTrue(string.Join("; ", inspection.Problems));
        inspection.Identity!.Value.Id.Should().Be("io.agentmemory.packs.martialarts");
        inspection.Kind.Should().Be("knowledge");
        inspection.Checks.Select(c => c.Name).Should().StartWith("archive").And.Contain("signatures");
        inspection.Checks.Should().OnlyContain(c => c.Passed);
        inspection.Signers.Should().ContainSingle().Which.Should().StartWith("publisher · key ");
        inspection.Declarations.Keys.Should().Contain(["pack.json", "ontology.json", "views.json"]);
        inspection.Mount!.Memory.Records.Should().HaveCount(93);
        (await inspection.Mount.Memory.AskAsync(inspection.Pack!.Id, "kata", "Heian Shodan")).First().Key.Should().Be("shotokan/heian-shodan");
    }

    [Fact]
    public void A_changed_or_untrusted_pack_shows_the_step_that_refused_it_and_loads_nothing()
    {
        var untrusted = PackInspection.Inspect(Archive(), Trust(PackAuthoring.CreateKey()));
        untrusted.Loaded.Should().BeFalse();
        untrusted.Checks.Last().Should().Match<PackCheck>(c => c.Name == "signatures" && !c.Passed);
        untrusted.Mount.Should().BeNull();
        untrusted.Identity.Should().NotBeNull("what the file list claims is shown, marked unverified by the failing step");

        var broken = PackInspection.Inspect([.. "not a zip"u8], Trust());
        broken.Checks.Should().ContainSingle().Which.Name.Should().Be("archive");
        broken.Loaded.Should().BeFalse();
    }

    [Fact]
    public void A_pack_set_shows_its_members_and_mounts_them()
    {
        var set = PackAuthoring.BuildSet("io.example.dojo-set", "1.0.0", [Archive()], _key.PrivateKeyPem);

        var inspection = PackInspection.Inspect(set, Trust());

        inspection.Kind.Should().Be("set");
        inspection.Set.Should().NotBeNull();
        inspection.Pins.Should().ContainSingle().Which.Should().Match<PackPart>(p => p.Id == "io.agentmemory.packs.martialarts" && p.Kind == "knowledge");
        inspection.Mount!.Memory.Count.Should().Be(93);
    }
}
