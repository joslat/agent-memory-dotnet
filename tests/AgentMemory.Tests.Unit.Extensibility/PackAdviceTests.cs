using System.Text;
using System.Text.Json.Nodes;
using AgentMemory.Extensibility.Ampack;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>PACK-composer: advice on a loaded pack, about what a host will do with it later; never a refusal.</summary>
public sealed class PackAdviceTests
{
    private static readonly string MartialArts = Path.Combine(AppContext.BaseDirectory, "Packs", "MartialArts");
    private static readonly PackSigningKey Key = PackAuthoring.CreateKey();

    private static LoadedPack Load(Action<JsonObject>? change = null, string? trial = null)
    {
        var source = new Dictionary<string, byte[]>(PackAuthoring.FromModuleBundle(Directory.EnumerateFiles(MartialArts, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(MartialArts, f).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllBytes, StringComparer.Ordinal)), StringComparer.Ordinal);
        if (change is not null)
        {
            var manifest = JsonNode.Parse(source["pack.json"])!.AsObject();
            change(manifest);
            source["pack.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        }
        if (trial is not null) source["evals/trial.json"] = Encoding.UTF8.GetBytes(trial);
        var trust = PackTrust.Parse(Encoding.UTF8.GetBytes(PackAuthoring.TrustPolicy(Key.PublicKeyPem, "io.agentmemory")));
        return PackLoader.Load(PackAuthoring.Build(source, Key.PrivateKeyPem), trust);
    }

    private static IEnumerable<string> Rules(LoadedPack pack, DateTimeOffset? now = null) => PackAdvice.Of(pack, now ?? DateTimeOffset.UtcNow).Select(a => a.Rule);

    [Fact]
    public void A_knowledge_pack_without_a_trial_is_told_a_host_requiring_trials_refuses_it()
    {
        var pack = Load();

        Rules(pack).Should().Equal("trial.missing");
        PackAdvice.Of(pack, DateTimeOffset.UtcNow).Single().Pack.Should().Be("io.agentmemory.packs.martialarts");
    }

    [Fact]
    public void A_trial_without_an_upgrade_rule_or_with_few_tasks_is_advised()
    {
        var pack = Load(trial: """{ "measure": "test-impact", "tasks": 20, "targets": { "hitRate": 0.3 } }""");

        Rules(pack).Should().Equal("trial.upgrade", "trial.small");
    }

    [Fact]
    public void A_full_trial_and_a_distant_validity_give_no_advice()
    {
        var pack = Load(
            m => m["validUntil"] = DateTimeOffset.UtcNow.AddDays(400).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
            trial: """{ "measure": "test-impact", "tasks": 40, "targets": { "hitRate": 0.3 }, "upgrade": { "noRegression": true, "tolerance": 0.02 } }""");

        Rules(pack).Should().BeEmpty();
        Rules(pack, DateTimeOffset.UtcNow.AddDays(380)).Should().Equal("validity.soon");
    }
}
