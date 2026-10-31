using System.Text;
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
         "traversal":{"relations":[{"name":"WORK_CHANGED","direction":"in","weightMicro":600000,"fanOut":3}],"maxItems":2,"hops":1}}
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
    public void Review7_W9_a_snapshot_whose_links_reach_a_dependency_that_holds_nothing_is_refused_as_the_store_refuses_it()
    {
        // The store resolved links as it wrote and memory once every pack was in, so a work snapshot imported before the code map
        // lost its links in the store and kept them in memory. Both refuse it now, and with the code map's records both link.
        FluentActions.Invoking(() => PackMemory.From([], [Ledger(consents: true)])).Should().Throw<KnowledgePackException>()
            .Which.Problems.Should().ContainSingle().Which.Should().Contain("import com.example.code first");
        var both = PackMemory.From([], [Codebase(consents: true), Ledger(consents: true)]);
        both.Records.Single(r => r.Type == "WorkItem").Links.Should().ContainSingle();
    }
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
    public void Review12_serve_holds_one_live_snapshot_of_a_module()
    {
        // Snapshots are no modules, so the host's composition passes them through; the mount plan holds one live snapshot of a
        // module, and serve must still apply it (round 12: no test sent two snapshots of one module through serve).
        var key = PackAuthoring.CreateKey();
        var trust = PackTrust.Parse(Encoding.UTF8.GetBytes(PackAuthoring.TrustPolicy(key.PublicKeyPem, "com.example", "io.example")));
        byte[][] archives =
        [
            PackAuthoring.Build(PackAuthoring.FromModuleBundle(new Dictionary<string, byte[]> { ["manifest.json"] = Encoding.UTF8.GetBytes(Code.Replace("CONSENT", "", StringComparison.Ordinal)) }), key.PrivateKeyPem),
            PackAuthoring.Build(PackAuthoring.FromSnapshot(Codebase(consents: false), "io.example.code-map-a", "1.0.0"), key.PrivateKeyPem),
            PackAuthoring.Build(PackAuthoring.FromSnapshot(Codebase(consents: false), "io.example.code-map-b", "1.0.0"), key.PrivateKeyPem),
        ];

        FluentActions.Invoking(() => PackMemory.Mount(archives, trust)).Should().Throw<PackLoadException>()
            .Which.Problems.Should().Contain(p => p.Contains("are snapshots of com.example.code"));
    }

    [Fact]
    public void Review13_the_store_installs_only_what_serve_then_holds()
    {
        // R13-1: a definition and two snapshots of it each installed, then serve --store refused the whole store; a snapshot with
        // no definition installed too. Install now checks the store as it will be, by the one decider serve uses.
        var key = PackAuthoring.CreateKey();
        var trust = PackTrust.Parse(Encoding.UTF8.GetBytes(PackAuthoring.TrustPolicy(key.PublicKeyPem, "com.example", "io.example")));
        var definition = PackAuthoring.Build(PackAuthoring.FromModuleBundle(new Dictionary<string, byte[]> { ["manifest.json"] = Encoding.UTF8.GetBytes(Code.Replace("CONSENT", "", StringComparison.Ordinal)) }), key.PrivateKeyPem);
        byte[] Shot(string id) => PackAuthoring.Build(PackAuthoring.FromSnapshot(Codebase(consents: false), id, "1.0.0"), key.PrivateKeyPem);
        var store = PackStore.Open(Directory.CreateTempSubdirectory("pack-store-r13-").FullName);

        FluentActions.Invoking(() => store.Install(Shot("io.example.code-map-a"), trust)).Should().Throw<PackLoadException>()
            .Which.Problems.Should().Contain(p => p.Contains("hold that definition pack"));
        store.Install(definition, trust);
        store.Install(Shot("io.example.code-map-a"), trust);
        FluentActions.Invoking(() => store.Install(Shot("io.example.code-map-b"), trust)).Should().Throw<PackLoadException>()
            .Which.Problems.Should().Contain(p => p.Contains("are snapshots of com.example.code"));
        store.Installed.Should().HaveCount(2);
        PackMemory.Mount(store.Archives(), trust).Packs.Should().HaveCount(2, "what the store holds, serve holds");
    }

    [Fact]
    public void Review14_install_runs_the_snapshot_imports_serve_runs()
    {
        // R14-1: a work snapshot linking into code records, with no code records held: install accepted it and serve --store then
        // refused the whole store ("holds no records here yet"). Install now runs the same import step; consent is left to serve.
        var key = PackAuthoring.CreateKey();
        var trust = PackTrust.Parse(Encoding.UTF8.GetBytes(PackAuthoring.TrustPolicy(key.PublicKeyPem, "com.example", "io.example")));
        byte[] Definition(string manifest) =>
            PackAuthoring.Build(PackAuthoring.FromModuleBundle(new Dictionary<string, byte[]> { ["manifest.json"] = Encoding.UTF8.GetBytes(manifest) }), key.PrivateKeyPem);
        var store = PackStore.Open(Directory.CreateTempSubdirectory("pack-store-r14-").FullName);
        // Authored as Review6_M1 authors it: the snapshot is checked as it is written, so it carries the grant its links need.
        var grant = new ModuleConsentGrant("com.example.work", "com.example.code");
        var ledger = PackAuthoring.Build(PackAuthoring.FromSnapshot(Ledger(consents: false) with { ConsentGrants = [grant] }, "io.example.work-ledger", "1.0.0"), key.PrivateKeyPem);
        store.Install(Definition(Code.Replace("CONSENT", "", StringComparison.Ordinal)), trust);
        store.Install(Definition(Work), trust);

        FluentActions.Invoking(() => store.Install(ledger, trust))
            .Should().Throw<PackLoadException>().Which.Problems.Should().Contain(p => p.Contains("holds no records here yet"), "no grant would help: there is nothing to link to");
        store.Installed.Should().HaveCount(2);

        // A link a host may grant is not refused at install: serve decides it with the host's grants.
        store.Install(PackAuthoring.Build(PackAuthoring.FromSnapshot(Codebase(consents: false), "io.example.code-map", "1.0.0"), key.PrivateKeyPem), trust);
        store.Install(ledger, trust);
        PackMemory.Mount(store.Archives(), trust, [grant]).Packs.Should().HaveCount(4);
    }

    [Fact]
    public async Task Review6_M1_mount_takes_the_hosts_grants_for_a_snapshot_pack_as_the_walk_does()
    {
        // The consent rule was checked twice with different grants: Mount made the pack a snapshot with none of the host's
        // grants before they reached the walk, so ampack serve --grant could not mount what it granted. Tested through Mount.
        var key = PackAuthoring.CreateKey();
        var trust = PackTrust.Parse(Encoding.UTF8.GetBytes(PackAuthoring.TrustPolicy(key.PublicKeyPem, "com.example", "io.example")));
        byte[] Definition(string manifest) =>
            PackAuthoring.Build(PackAuthoring.FromModuleBundle(new Dictionary<string, byte[]> { ["manifest.json"] = Encoding.UTF8.GetBytes(manifest) }), key.PrivateKeyPem);
        var grant = new ModuleConsentGrant("com.example.work", "com.example.code");
        byte[][] archives =
        [
            Definition(Code.Replace("CONSENT", "", StringComparison.Ordinal)), Definition(Work),
            PackAuthoring.Build(PackAuthoring.FromSnapshot(Codebase(consents: false), "io.example.code-map", "1.0.0"), key.PrivateKeyPem),
            PackAuthoring.Build(PackAuthoring.FromSnapshot(Ledger(consents: false) with { ConsentGrants = [grant] }, "io.example.work-ledger", "1.0.0"), key.PrivateKeyPem),
        ];

        FluentActions.Invoking(() => PackMemory.Mount(archives, trust)).Should().Throw<PackLoadException>()
            .Which.Problems.Should().Contain(p => p.Contains("does not list it in containableBy"));
        var mount = PackMemory.Mount(archives, trust, [grant]);
        var hits = await mount.Memory.WalkAsync([ContextAnchor.Path("src/Store.cs")], "why is the store exact", CancellationToken.None);
        hits.Should().Contain(h => h.Type == "WorkItem" && h.Key == "A-1");
    }

    [Fact]
    public void Review6_L8_packs_in_memory_check_every_snapshot_as_an_import_does()
    {
        var duplicate = Codebase(consents: true) with { Records = [.. Codebase(consents: true).Records, .. Codebase(consents: true).Records] };
        FluentActions.Invoking(() => PackMemory.From([], [duplicate])).Should().Throw<KnowledgePackException>();
    }

    [Fact]
    public async Task Packs_in_memory_take_the_hosts_own_grants_as_a_host_does()
    {
        // Review 5, L5: ampack serve took only the grants a snapshot carries; a host's own grant walks the relation there too.
        var memory = PackMemory.From([], [Codebase(consents: false), Ledger(consents: false)], [new ModuleConsentGrant("com.example.work", "com.example.code")]);
        var hits = await memory.WalkAsync([ContextAnchor.Path("src/Store.cs")], "why is the store exact", CancellationToken.None);

        hits.Should().Contain(h => h.Type == "WorkItem" && h.Key == "A-1");
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
