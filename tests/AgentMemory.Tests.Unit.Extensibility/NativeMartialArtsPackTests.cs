using System.Text;
using AgentMemory.Extensibility.Ampack;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>The public, frozen native fixture proves the current format without requiring the private strategy checkout.</summary>
public sealed class NativeMartialArtsPackTests
{
    [Fact]
    public async Task Native_source_builds_verifies_and_answers_without_a_legacy_bundle()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Packs", "MartialArtsV12");
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(p => Path.GetFileName(p) != ".gitattributes").ToDictionary(
            p => Path.GetRelativePath(root, p).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllBytes, StringComparer.Ordinal);
        files.Should().ContainKey("pack.json").And.ContainKey("ontology.json").And.ContainKey("views.json");
        files.Should().NotContainKey("manifest.json");
        files.Keys.Should().NotContain(p => p.StartsWith("seed/", StringComparison.Ordinal) || p.StartsWith("agentmemory.module.", StringComparison.Ordinal));
        Encoding.UTF8.GetString(files["pack.json"]).Should().Contain("agentmemory.pack/1.2");

        var key = PackAuthoring.CreateKey();
        var trust = PackTrust.Parse(Encoding.UTF8.GetBytes(PackAuthoring.TrustPolicy(key.PublicKeyPem, "io.agentmemory.packs")));
        var archive = PackAuthoring.Build(files, key.PrivateKeyPem);
        var loaded = PackLoader.Load(archive, trust);
        loaded.Id.Should().Be("io.agentmemory.packs.martialarts");
        loaded.Knowledge!.Proposals.Should().HaveCount(93);
        var mounted = PackMemory.Mount([archive], trust);
        var hits = await mounted.Memory.AskAsync(loaded.Id, "kata", text: "heian sandan");
        hits.Should().Contain(h => h.Key == "shotokan/heian-sandan" && h.Pack != null);
    }
}
