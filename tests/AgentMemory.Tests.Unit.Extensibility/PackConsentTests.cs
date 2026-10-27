using System.Text.Json;
using AgentMemory.Extensibility;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Extensibility.Context;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Packs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// PACK-consent (review 1.11): a pack's containableBy governs links another pack writes into its records at import, as it
/// already governed walks into them; a host may grant a pair by name, and the grant joins the set fingerprint.
/// </summary>
public sealed class PackConsentTests
{
    private const string Code = """
        {"format":"agentmemory.module/v1alpha1","id":"com.example.code","version":"1.0.0","sdk":"[0.1,0.2)",
         "ontology":{"labelPrefix":"Code","types":[{"name":"CodeFile","scope":"shared","render":"{path}","properties":{"path":{"type":"string","required":true}}}]},
         "traversal":{"anchors":[{"type":"CodeFile","property":"path","match":"exact"}],"hubs":["CodeFile"],"maxItems":3}CONSENT}
        """;
    private const string Work = """
        {"format":"agentmemory.module/v1alpha1","id":"com.example.work","version":"1.0.0","sdk":"[0.1,0.2)",
         "requires":{"modules":{"com.example.code":"[1,2)"}},
         "ontology":{"labelPrefix":"Work","types":[{"name":"WorkItem","scope":"shared","render":"Work item {item}: {title}","properties":{"item":{"type":"string","required":true},"title":{"type":"string","required":true}}}],
          "relations":[{"name":"WORK_CHANGED","from":"WorkItem","to":"com.example.code:CodeFile"}]},
         "traversal":{"relations":[{"name":"WORK_CHANGED","direction":"in","weight":0.6,"fanOut":3}],"maxItems":2,"hops":1}}
        """;

    private static ModuleManifest CodeModule(bool consents) =>
        ModuleManifest.Parse(Code.Replace("CONSENT", consents ? ",\"containableBy\":[\"com.example.work\"]" : "", StringComparison.Ordinal));

    private static ModuleSnapshot Codebase(bool consents) => new()
    {
        Module = CodeModule(consents), Source = "git:abc",
        Records = [Record("CodeFile", "src/Store.cs", ("path", "src/Store.cs"))],
    };

    private static ModuleSnapshot Ledger(bool consents) => new()
    {
        Module = ModuleManifest.Parse(Work), Dependencies = [CodeModule(consents)], Source = "git:abc",
        Records = [Record("WorkItem", "A-1", ("item", "A-1"), ("title", "Make the store exact")) with
        {
            Links = [new ProposedLink { Relation = "WORK_CHANGED", Target = "src/Store.cs" }],
        }],
    };

    private static ModuleProposal Record(string type, string key, params (string Name, string Value)[] properties) => new()
    {
        Kind = ProposalKind.Assert, Type = type, Key = key,
        Properties = properties.ToDictionary(p => p.Name, p => JsonSerializer.SerializeToElement(p.Value), StringComparer.Ordinal),
        Source = new ProposalSource { Excerpt = key },
    };

    [Fact]
    public void A_snapshot_that_links_into_a_pack_without_its_consent_is_refused_before_anything_is_written()
    {
        Ledger(consents: true).Problems().Should().BeEmpty();

        Ledger(consents: false).Problems().Should().ContainSingle().Which.Should()
            .Contain("WORK_CHANGED reaches into com.example.code, which does not list it in containableBy").And.Contain("No link of it is written");
    }

    [Fact]
    public void A_host_grant_lets_the_links_in_and_travels_with_the_resolution()
    {
        var granted = Ledger(consents: false) with { ConsentGrants = [new ModuleConsentGrant("com.example.work", "com.example.code")] };

        granted.Problems().Should().BeEmpty();
        var resolution = granted.Resolve();
        resolution.ConsentGrants.Should().ContainSingle();
        var work = resolution.Modules.Single(m => m.Id == "com.example.work");
        resolution.ConsentRefusal(work, work.Ontology!.Relations.Single()).Should().BeNull();
        (resolution with { ConsentGrants = [] }).ConsentRefusal(work, work.Ontology!.Relations.Single()).Should().NotBeNull();
    }

    [Fact]
    public async Task Packs_in_memory_refuse_the_same_links_and_walk_through_them_once_granted()
    {
        FluentActions.Invoking(() => PackMemory.From([], [Codebase(consents: false), Ledger(consents: false)])).Should().Throw<KnowledgePackException>()
            .Which.Problems.Should().ContainSingle().Which.Should().Contain("does not list it in containableBy");

        var granted = Ledger(consents: false) with { ConsentGrants = [new ModuleConsentGrant("com.example.work", "com.example.code")] };
        var memory = PackMemory.From([], [Codebase(consents: false), granted]);
        var hits = await memory.WalkAsync([ContextAnchor.Path("src/Store.cs")], "why is the store exact", CancellationToken.None);

        hits.Should().Contain(h => h.Type == "WorkItem" && h.Key == "A-1", "the grant applies to the walk as it does to the links");
    }

    [Fact]
    public void A_grant_joins_the_set_fingerprint()
    {
        static ModuleSet Compose(Action<ModuleSetBuilder> more)
        {
            var services = new ServiceCollection();
            services.AddAgentMemoryModules(m =>
            {
                m.Add(ModuleManifest.Parse("""{"format":"agentmemory.module/v1alpha1","id":"com.example.notes","version":"1.0.0","sdk":"[0.1,0.2)"}"""), _ => { });
                more(m);
            });
            return services.BuildServiceProvider().GetRequiredService<ModuleSet>();
        }

        var plain = Compose(_ => { });
        var granted = Compose(m => m.GrantConsent("com.example.work", "com.example.code"));

        granted.Fingerprint.Should().NotBe(plain.Fingerprint);
        granted.Resolution.ConsentGrants.Should().Equal(new ModuleConsentGrant("com.example.work", "com.example.code"));
        FluentActions.Invoking(() => new ServiceCollection().AddAgentMemoryModules(m => m.GrantConsent("not an id", "com.example.code")))
            .Should().Throw<ArgumentException>();
    }
}
