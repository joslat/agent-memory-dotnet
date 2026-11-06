#pragma warning disable AMEXT001
using System.Text;
using System.Text.Json.Nodes;
using AgentMemory.Extensibility;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Reading;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// Feature 10: what a receiver may do with a pack (R-CORE-138), asked at the door where each action happens (R-CORE-139).
/// </summary>
public sealed class PolicyTests
{
    private static readonly PackSigningKey Key = PackAuthoring.CreateKey();
    private const string Host = "https://host.example/profile#me";

    // A knowledge pack, or (definition) one that only defines the module, whose records snapshots carry.
    private static LoadedPack Load(string policy, bool hostNamed = false, bool definition = false)
    {
        var module = definition
            ? """{"format":"agentmemory.module/v1alpha1","id":"com.example.lantern-notes","version":"1.0.0","sdk":"[0.1,0.2)"}"""
            : """{"format":"agentmemory.module/v1alpha1","id":"com.example.lantern-notes","version":"1.0.0","sdk":"[0.1,0.2)","seed":["records/notes.jsonl"]}""";
        var bundle = new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["manifest.json"] = Encoding.UTF8.GetBytes(module) };
        if (!definition) bundle["records/notes.jsonl"] = Encoding.UTF8.GetBytes("""{"type":"LanternNote","key":"a","properties":{"text":"a"}}""" + "\n");
        var source = new Dictionary<string, byte[]>(PackAuthoring.FromModuleBundle(bundle), StringComparer.Ordinal)
        {
            ["ontology.json"] = Encoding.UTF8.GetBytes("""{"labelPrefix":"Lantern","types":[{"name":"LanternNote","scope":"shared","properties":{"text":{"type":"string"}}}]}"""),
            ["policy.json"] = Encoding.UTF8.GetBytes(policy),
        };
        // A definition with no types composes with no store, which is all the door test needs.
        if (definition) source.Remove("ontology.json");
        var trust = JsonNode.Parse(PackAuthoring.TrustPolicy(Key.PublicKeyPem, "com.example"))!;
        if (hostNamed) trust["host"] = new JsonObject { ["webIds"] = new JsonArray(Host) };
        return PackLoader.Load(PackAuthoring.Build(source, Key.PrivateKeyPem), PackTrust.Parse(Encoding.UTF8.GetBytes(trust.ToJsonString())));
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void Deny_wins_and_an_ended_rule_stops_granting_but_never_stops_refusing()
    {
        var pack = Load("""{"format":"agentmemory.pack.policy/1","rules":[{"allow":["export","read","rehydrate:local"],"audience":["*"]},{"audience":["*"],"deny":["export"]},{"allow":["derive"],"audience":["*"],"notAfter":"2020-01-01T00:00:00Z"},{"audience":["*"],"deny":["train"],"notAfter":"2020-01-01T00:00:00Z"},{"allow":["train"],"audience":["*"]}]}""");
        PolicyGate.Allows(pack, "read", Now).Should().BeTrue();
        PolicyGate.Allows(pack, "export", Now).Should().BeFalse("a denial beats a grant");
        PolicyGate.Allows(pack, "derive", Now).Should().BeFalse("its rule ended in 2020");
        PolicyGate.Allows(pack, "derive", new DateTimeOffset(2019, 12, 31, 0, 0, 0, TimeSpan.Zero)).Should().BeTrue("before its rule ended");
        PolicyGate.Allows(pack, "train", Now).Should().BeFalse("an ended rule still refuses");
        PolicyGate.Allows(pack, "rehydrate:external", Now).Should().BeFalse("what no rule grants is not allowed");
        FluentActions.Invoking(() => PolicyGate.Allows(pack, "sell", Now)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_rule_for_named_WebIDs_speaks_only_to_a_host_acting_as_one_of_them()
    {
        const string ForHost = """{"format":"agentmemory.pack.policy/1","rules":[{"allow":["read"],"audience":["*"]},{"allow":["export"],"audience":["https://host.example/profile#me"]}]}""";
        PolicyGate.Allows(Load(ForHost), "export", Now).Should().BeFalse("this host names no WebID");
        PolicyGate.Allows(Load(ForHost, hostNamed: true), "export", Now).Should().BeTrue();
        FluentActions.Invoking(() => Load("""{"format":"agentmemory.pack.policy/1","rules":[{"allow":["read"],"audience":["https://host.example/profile#me"]}]}"""))
            .Should().Throw<PackLoadException>("the read door is at load").Which.Problems.Should().Contain(p => p.Contains("does not let this host read it"));
    }

    [Fact]
    public void A_module_set_withholds_a_pack_s_records_from_a_model_its_policy_keeps_them_from()
    {
        // The rehydrate door (R-CORE-139): by the endpoint class the host states; unstated is external.
        var pack = Load("""{"format":"agentmemory.pack.policy/1","rules":[{"allow":["read","rehydrate:local"],"audience":["*"]}]}""", definition: true);
        ModuleSet Compose(Action<ModuleSetBuilder> add)
        {
            var services = new ServiceCollection();
            services.AddAgentMemoryModules(add);
            return services.BuildServiceProvider().GetRequiredService<ModuleSet>();
        }
        var record = new ModuleRecord
        {
            ModuleId = pack.Id, Type = "LanternNote", Id = "a", Key = "a", CreatedAtUtc = DateTimeOffset.UnixEpoch, SourceSessionId = "pack",
            Source = new ProposalSource
            {
                Excerpt = "a",
                Pack = new PackSource { Id = pack.Id, Version = pack.Version, Digest = pack.Digest, File = "records/notes.jsonl", Line = 1, UsagePolicy = pack.PolicyDigest },
            },
        };
        pack.PolicyDigest.Should().StartWith("sha256:");
        Compose(m => m.AddPack(pack)).Withheld(record, Now).Should().BeTrue("an unstated endpoint is external");
        Compose(m => m.AddPack(pack).UseModelEndpoint(ModelEndpoint.Private)).Withheld(record, Now).Should().BeTrue();
        Compose(m => m.AddPack(pack).UseModelEndpoint(ModelEndpoint.Local)).Withheld(record, Now).Should().BeFalse();
        // Review F10, H1: a record governed by a policy this host does not hold is withheld, whatever composed its module.
        Compose(m => m.UseModelEndpoint(ModelEndpoint.Local)).Withheld(record, Now).Should().BeTrue("the host does not hold its policy");
        var free = record with { Source = record.Source with { Pack = record.Source.Pack! with { UsagePolicy = null } } };
        Compose(m => m.AddPack(pack)).Withheld(free, Now).Should().BeFalse("no policy governs it, as a host-built record (review F10, M3)");
    }

    [Fact]
    public void Read_is_a_condition_of_every_other_verb_and_the_loader_names_the_policy_on_every_record()
    {
        // Review F10, M2: a grant to rehydrate outlives an ended read grant no longer.
        var pack = Load("""{"format":"agentmemory.pack.policy/1","rules":[{"allow":["read"],"audience":["*"],"notAfter":"2099-01-01T00:00:00Z"},{"allow":["rehydrate:external"],"audience":["*"]}]}""");
        pack.Knowledge!.Proposals.Should().OnlyContain(r => r.Source.Pack!.UsagePolicy == pack.PolicyDigest);
        var services = new ServiceCollection();
        services.AddAgentMemoryModules(m => m.UsePackPolicy(pack));
        var set = services.BuildServiceProvider().GetRequiredService<ModuleSet>();
        var record = new ModuleRecord
        {
            ModuleId = pack.Id, Type = "LanternNote", Id = "a", Key = "a", CreatedAtUtc = DateTimeOffset.UnixEpoch, SourceSessionId = "pack",
            Source = pack.Knowledge.Proposals[0].Source,
        };
        set.Withheld(record, Now).Should().BeFalse();
        set.Withheld(record, new DateTimeOffset(2099, 1, 2, 0, 0, 0, TimeSpan.Zero)).Should().BeTrue("its read grant has ended");
    }
}
