using System.Text;
using AgentMemory.Packs.Format;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.PackFormat;

/// <summary>
/// Feature 01, step 2: the profiles a pack uses are derived from what it holds by the registry's map, never declared; the
/// conformance kit checks that both verifiers derive the same ones on every verified vector.
/// </summary>
public sealed class PackProfilesTests
{
    private static PackManifest Manifest(string extra = "") => PackManifest.Parse(Encoding.UTF8.GetBytes(
        "{\"format\":\"agentmemory.pack/1.2\",\"id\":\"io.example.notes\",\"version\":\"1.0.0\",\"kind\":\"knowledge\"" + extra + "}")).Manifest
        ?? throw new InvalidOperationException("the test manifest does not parse");

    private static string[] Used(PackManifest manifest, params string[] files) => [.. PackProfiles.Used(files, manifest).Select(p => p.Name)];

    [Fact]
    public void A_pack_uses_the_profiles_whose_files_fields_or_values_it_holds_in_the_registry_s_order()
    {
        Used(Manifest(), "pack.json", "card.json", "records/a.jsonl").Should().BeEmpty("core alone");
        Used(Manifest(), "pack.json", "evals/trial.json", "traversal.json").Should().Equal("walk", "trial");
        Used(Manifest(), "pack.json", "queries/one.json").Should().Equal("code");
        Used(Manifest(",\"protection\":\"audience\""), "pack.json").Should().Equal("protection");
        Used(Manifest(",\"code\":\"E1\""), "pack.json").Should().Equal("code");
        Used(Manifest(",\"protection\":\"open\",\"code\":\"E0\""), "pack.json").Should().BeEmpty("the default values use no profile");
    }

    [Fact]
    public void A_host_refuses_exactly_the_profiles_the_registry_marks_not_implemented_in_their_own_words()
    {
        var manifest = Manifest(",\"code\":\"E2\"");
        var refused = PackProfiles.Used(["pack.json", "policy.json"], manifest).Where(p => !p.Implemented).Select(p => PackProfiles.Refusal(p, manifest)).ToList();
        refused.Should().HaveCount(2);
        refused.Should().Contain(r => r.Contains("carries code (tier E2)", StringComparison.Ordinal));
        refused.Should().Contain(r => r.Contains("usage policy", StringComparison.Ordinal));
        PackRegistry.Profiles.Where(p => !p.Implemented).Select(p => p.Name).Should().BeEquivalentTo(["protection", "code", "permissions"]);
    }
}
