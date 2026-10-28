using System.Text;
using System.Text.Json.Nodes;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Packs.Format;
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
    public void A_countersignature_keeps_the_digest_and_counts_where_the_policy_trusts_the_role()
    {
        // PACK-composer phase 3: a curator signs a built pack again; a host that requires a curator then loads it.
        var source = new Dictionary<string, byte[]>(PackAuthoring.FromModuleBundle(Directory.EnumerateFiles(MartialArts, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(MartialArts, f).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllBytes, StringComparer.Ordinal)), StringComparer.Ordinal);
        var built = PackAuthoring.Build(source, Key.PrivateKeyPem);
        var curator = PackKey.Create();
        var policy = new JsonObject
        {
            ["format"] = "agentmemory.pack.trust/1.1", ["requireRoles"] = new JsonArray("publisher", "curator"),
            ["keys"] = new JsonArray(
                new JsonObject { ["keyId"] = PackKey.KeyIdOf(Key.PublicKeyPem), ["publicKey"] = Key.PublicKeyPem, ["idPrefixes"] = new JsonArray("io.agentmemory"), ["roles"] = new JsonArray("publisher") },
                new JsonObject { ["keyId"] = curator.KeyId, ["publicKey"] = curator.PublicKeyPem, ["idPrefixes"] = new JsonArray("io.agentmemory"), ["roles"] = new JsonArray("curator") }),
        };
        var trust = PackTrust.Parse(Encoding.UTF8.GetBytes(policy.ToJsonString()));

        FluentActions.Invoking(() => PackLoader.Load(built, trust)).Should().Throw<PackLoadException>("a curator is required and none signed yet");
        var countersigned = PackAuthoring.Countersign(built, curator.PrivateKeyPem, "curator");
        var loaded = PackLoader.Load(countersigned, trust);

        loaded.Digest.Should().Be(PackLoader.Load(built, PackTrust.Parse(Encoding.UTF8.GetBytes(PackAuthoring.TrustPolicy(Key.PublicKeyPem, "io.agentmemory")))).Digest);
        loaded.Signers.Should().HaveCount(2);
        var tampered = (byte[])built.Clone();
        tampered[tampered.Length / 2] ^= 0x01;
        FluentActions.Invoking(() => PackAuthoring.Countersign(tampered, curator.PrivateKeyPem, "curator")).Should().Throw<PackLoadException>();
    }

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

    [Fact]
    public void A_federated_pack_carries_who_published_it_and_for_whom_through_loading_inspection_and_advice()
    {
        const string publisher = "https://pod.example/supplier/profile/card#me";
        const string buyer = "https://pod.example/buyer/profile/card#me";
        var source = new Dictionary<string, byte[]>(PackAuthoring.FromModuleBundle(Directory.EnumerateFiles(MartialArts, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(MartialArts, f).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllBytes, StringComparer.Ordinal)), StringComparer.Ordinal);
        var manifest = JsonNode.Parse(source["pack.json"])!.AsObject();
        manifest["extensions"] = new JsonObject
        {
            ["critical"] = new JsonArray("federation"),
            ["federation"] = new JsonObject { ["format"] = "agentmemory.pack.federation/1", ["publisher"] = publisher, ["audience"] = new JsonArray(buyer), ["purpose"] = "Study notes" },
        };
        source["pack.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        var archive = PackAuthoring.Build(source, Key.PrivateKeyPem);
        var policy = JsonNode.Parse(PackAuthoring.TrustPolicy(Key.PublicKeyPem, "io.agentmemory"))!.AsObject();
        policy["keys"]![0]!["webId"] = publisher;
        policy["host"] = new JsonObject { ["webIds"] = new JsonArray(buyer) };
        var trust = PackTrust.Parse(Encoding.UTF8.GetBytes(policy.ToJsonString()));

        var pack = PackLoader.Load(archive, trust);
        var inspection = PackInspection.Inspect(archive, trust);

        pack.Publication.Should().Be(new PackPublication(publisher, pack.Publication!.Audience, "Study notes"));
        pack.Publication.Audience.Should().Equal(buyer);
        inspection.Loaded.Should().BeTrue();
        inspection.Publication!.Publisher.Should().Be(publisher);
        inspection.Checks.Should().Contain(c => c.Name == "federation" && c.Passed);
        PackAdvice.Of(pack, DateTimeOffset.UtcNow).Should().Contain(a => a.Rule == "federation.placement" && a.Text.Contains("for 1 WebID(s)"));

        // A host outside the audience still sees the claim, unverified, and why it does not load.
        policy["host"] = new JsonObject { ["webIds"] = new JsonArray("https://pod.example/stranger/profile/card#me") };
        var stranger = PackInspection.Inspect(archive, PackTrust.Parse(Encoding.UTF8.GetBytes(policy.ToJsonString())));
        stranger.Loaded.Should().BeFalse();
        stranger.Publication!.Publisher.Should().Be(publisher);
        stranger.Problems.Should().Contain(p => p.Contains("this host is https://pod.example/stranger/profile/card#me"));
    }
}
