using AgentMemory.Extensibility.Bundles;
using AgentMemory.Extensibility.Packs;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>The shipped "I know martial arts" pack (packs/AgentMemory.Packs.MartialArts) opens and holds what it promises.</summary>
public sealed class MartialArtsPackTests
{
    internal static KnowledgePack Open()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Packs", "MartialArts");
        var content = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllBytes, StringComparer.Ordinal);
        var key = BundleSigning.CreateKey();
        var trust = new BundleTrust();
        trust.DeveloperKeys.Add(key.PublicKeyPem);
        return KnowledgePack.Open(BundleSigning.Sign(content, "io.agentmemory.packs.martialarts", "1.0.0", key.PrivateKeyPem), trust);
    }

    [Fact]
    public void ThePackOpensWithEveryRecordSourcedAndTargetsFirst()
    {
        var pack = Open();
        pack.Id.Should().Be("io.agentmemory.packs.martialarts");
        pack.Proposals.GroupBy(p => p.Type).ToDictionary(g => g.Key, g => g.Count()).Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["MartialArtsArt"] = 3, ["MartialArtsRank"] = 17, ["MartialArtsTechnique"] = 54, ["MartialArtsKata"] = 19,
        });
        pack.Proposals.Should().OnlyContain(p => p.Properties.ContainsKey("source") && p.Source.Pack != null);
        pack.Proposals.Take(3).Should().OnlyContain(p => p.Type == "MartialArtsArt");
        pack.Proposals.Last().Type.Should().Be("MartialArtsKata");
    }

    [Fact]
    public void EachRankNamesItsNextGradeSoAFreshQuestionCanAnswerWhatComesNext()
    {
        var six = Open().Proposals.Single(p => p.Key == "shotokan/6-kyu");
        six.Properties["requirement"].GetString().Should().Be("Kata: Heian Sandan.");
        six.Properties["next"].GetString().Should().Be("5th kyu, kata Heian Yondan");
    }

    [Fact]
    public void TheKataKnowTheirRankAndTechniques()
    {
        var shodan = Open().Proposals.Single(p => p.Key == "shotokan/heian-shodan");
        shodan.Properties["moves"].GetInt32().Should().Be(21);
        shodan.Links.Select(l => l.Target).Should().Contain(["shotokan", "shotokan/8-kyu", "shotokan/gedan-barai", "shotokan/oi-zuki"]);
    }
}
