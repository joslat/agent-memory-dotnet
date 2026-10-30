#pragma warning disable AMEXT001
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Stubs;
using AgentMemory.Extensibility;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Extensibility.Context;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j;
using AgentMemory.Extensibility.Neo4j.Migrations;
using AgentMemory.Extensibility.Neo4j.Storage;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Extensibility.Reading;
using AgentMemory.Packs.CodeMap;
using AgentMemory.Packs.Coding;
using AgentMemory.Packs.Work;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// Memory packs end to end on a real Neo4j (pack format v1.2, PACK-e2e). Every kind is built and signed as an <c>.ampack</c>,
/// loaded through the host's verification, then written and read back through the store: the real martial arts knowledge
/// pack imports, is searched, upgrades and is removed, and the install ledger refuses its older version; a code map
/// definition and a snapshot of it, shared in one pack set, import exactly the records the code-built snapshot has and walk
/// from a file to its tests; the real Coding family, as a composite in a pack set, composes in a host and migrates to exactly
/// the schema of the compiled family; and a pack that was changed or comes from an untrusted key is refused with nothing written.
/// </summary>
public sealed partial class ModuleRecordWriteIntegrationTests
{
    private static readonly PackSigningKey AmpackKey = PackAuthoring.CreateKey();
    private static readonly string RealPacks = Path.Combine(AppContext.BaseDirectory, "Packs");

    private static PackTrust AmpackTrust(PackSigningKey? key = null) =>
        PackTrust.Parse(Encoding.UTF8.GetBytes(PackAuthoring.TrustPolicy((key ?? AmpackKey).PublicKeyPem, "io.agentmemory", "io.example")));

    private static Dictionary<string, byte[]> RealBundle(string folder)
    {
        var root = Path.Combine(RealPacks, folder);
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllBytes, StringComparer.Ordinal);
    }

    // A real pack's manifest, migrated to the v1.2 layout, built and signed: a definition pack, or with composite.json a composite source.
    private static IReadOnlyDictionary<string, byte[]> RealSource(string folder, string file = "manifest.json") => PackAuthoring.FromModuleBundle(
        new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["manifest.json"] = File.ReadAllBytes(Path.Combine(RealPacks, folder, file)) });

    [Fact]
    public async Task TheRealMartialArtsPackAsAnAmpackImportsIsSearchedUpgradesRefusesItsOlderVersionAndIsRemoved()
    {
        var folder = Directory.CreateTempSubdirectory("ampack-e2e-").FullName;
        try
        {
            var source = new Dictionary<string, byte[]>(PackAuthoring.FromModuleBundle(RealBundle("MartialArts")), StringComparer.Ordinal);
            var v1Path = Path.Combine(folder, "martialarts-1.0.0.ampack");
            await File.WriteAllBytesAsync(v1Path, PackAuthoring.Build(source, AmpackKey.PrivateKeyPem));
            var ledger = PackInstallLedger.Open(Path.Combine(folder, "ledger.json"));
            var v1 = PackLoader.Load(v1Path, AmpackTrust(), ledger);
            ledger.Record(v1);
            var count = v1.Knowledge!.Proposals.Count;
            var importer = new KnowledgePackImporter(new Neo4jKnowledgePackStore(fixture.TransactionRunner));

            // The .ampack's identity is what the store records: every record, shared, with its file and line in the pack.
            (await importer.ImportAsync(v1.Knowledge)).Should().Be(new KnowledgePackImportResult
                { PackId = v1.Id, Version = "1.0.0", Digest = v1.Digest, Created = count });
            (await importer.GetStateAsync(v1.Manifest)).LiveRecords.Should().Be(count);
            using var services = Policies();
            var reader = new Neo4jModuleReader(fixture.TransactionRunner, v1.Knowledge.Resolution, v1.Id, services.GetRequiredService<IMemoryIsolationPolicy>());
            var views = new ModuleViews(v1.Knowledge.Resolution, reader);
            var lea = new ModuleReadContext { Scope = MemoryScope.For("lea") };
            var kata = await views.EvaluateWithArgumentsAsync("kata", new() { Search = "Which kata do I learn for 8th kyu in Shotokan? Heian Shodan?" }, lea);
            kata.Status.Should().Be(ModuleReadStatus.Succeeded, string.Join("; ", kata.Errors));
            var shodan = kata.Items.Should().Contain(i => i.Record.Key == "shotokan/heian-shodan").Subject;
            shodan.Record.OwnerId.Should().BeNull();
            shodan.Record.Source.Pack.Should().BeEquivalentTo(new { Id = v1.Id, Version = "1.0.0", Digest = v1.Digest, File = "records/kata.jsonl" });

            // 1.0.1 changes one kata's meaning: the store writes only that record and keeps the version before it.
            var kataFile = Encoding.UTF8.GetString(source["records/kata.jsonl"]);
            kataFile.Should().Contain("The first of the five Heian ('peaceful mind') kata.");
            source["records/kata.jsonl"] = Encoding.UTF8.GetBytes(kataFile.Replace("The first of the five Heian ('peaceful mind') kata.",
                "The first of the five Heian kata; heian means peaceful mind.", StringComparison.Ordinal));
            var pack = JsonNode.Parse(source["pack.json"])!.AsObject();
            pack["version"] = "1.0.1";
            source["pack.json"] = Encoding.UTF8.GetBytes(pack.ToJsonString());
            var v2Path = Path.Combine(folder, "martialarts-1.0.1.ampack");
            await File.WriteAllBytesAsync(v2Path, PackAuthoring.Build(source, AmpackKey.PrivateKeyPem));
            var v2 = PackLoader.Load(v2Path, AmpackTrust(), ledger);
            ledger.Record(v2);
            (await importer.ImportAsync(v2.Knowledge!)).Should().Be(new KnowledgePackImportResult
                { PackId = v1.Id, Version = "1.0.1", Digest = v2.Digest, Changed = 1, Unchanged = count - 1 });
            var history = await views.EvaluateWithArgumentsAsync("kata", new() { Search = "Heian Shodan peaceful mind" }, lea);
            history.Items.Single(i => i.Record.Key == "shotokan/heian-shodan").Record.Source.Pack!.Version.Should().Be("1.0.1");

            // The install ledger, reopened as after a restart, refuses the older pack before any store sees it.
            var reopened = PackInstallLedger.Open(Path.Combine(folder, "ledger.json"));
            FluentActions.Invoking(() => PackLoader.Load(v1Path, AmpackTrust(), reopened)).Should().Throw<PackLoadException>()
                .Which.Problems.Should().Contain(p => p.Contains("older than 1.0.1", StringComparison.Ordinal));

            // Removal closes every live record; nothing is found any more, and the history stays.
            (await importer.RemoveAsync(v2.Manifest)).Closed.Should().Be(count);
            (await views.EvaluateWithArgumentsAsync("kata", new() { Search = "Heian Shodan" }, lea)).Items.Should().BeEmpty();
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task ACodeMapDefinitionAndASnapshotSharedInOnePackSetImportExactlyTheCodeBuiltRecordsAndWalkFromAFileToItsTests()
    {
        var store = new Neo4jModuleSnapshotStore(fixture.TransactionRunner);
        var at = DateTimeOffset.UtcNow.AddMinutes(8);
        await store.ImportAsync(NoCodeMap("git:clear"), at.AddSeconds(-1));
        var built = MapSnapshot("git:local", "Close Remove", withOther: true);
        var definition = PackAuthoring.Build(RealSource("CodeMap"), AmpackKey.PrivateKeyPem);
        var snapshot = PackAuthoring.Build(PackAuthoring.FromSnapshot(built, "io.example.acme-map", "1.0.0",
            repository: "https://example.com/acme/map", commit: "0a1b2c3d4e5f"), AmpackKey.PrivateKeyPem);

        var set = PackFamilies.LoadSet(PackAuthoring.BuildSet("io.example.acme-code", "1.0.0", [definition, snapshot], AmpackKey.PrivateKeyPem, AmpackTrust()), AmpackTrust());
        set.Families.Should().BeEmpty();
        var map = set.Packs.Should().ContainSingle(p => p.Kind == "definition").Subject;
        var shared = set.Packs.Should().ContainSingle(p => p.Kind == "snapshot").Subject;
        var fromPack = shared.ToSnapshot(map.Manifest);
        fromPack.Digest.Should().Be(built.Digest);

        (await store.ImportAsync(fromPack, at)).Should().Be(new ModuleSnapshotResult { ModuleId = CodeMapPack.Id, Source = "git:0a1b2c3d4e5f", Created = 6 });
        // The code-built snapshot of the same code writes nothing: the pack carried exactly its records.
        (await store.ImportAsync(built, at.AddSeconds(1))).Should().Be(new ModuleSnapshotResult { ModuleId = CodeMapPack.Id, Source = "git:local", Unchanged = 6 });

        using var services = Policies();
        var reader = new Neo4jModuleTraversalReader(fixture.TransactionRunner, KnowledgePack.ResolutionOf(map.Manifest), services.GetRequiredService<IMemoryIsolationPolicy>());
        var context = new ModuleReadContext { Scope = MemoryScope.For("lea") };
        var file = (await reader.MatchAsync(new() { ModuleId = CodeMapPack.Id, Type = CodeMapPack.FileType, Property = "path",
            Match = ModuleAnchorMatch.Exact, Values = ["src/Map/MapStore.cs"] }, context)).Records.Should().ContainSingle().Subject;
        file.Source.Pack!.Version.Should().Be("git:0a1b2c3d4e5f");
        file.Source.Pack.File.Should().StartWith("records/");
        // PACK-bind: the record also names the signed pack that carried it, and who signed it.
        file.Source.Pack.Carrier.Should().Be(new PackCarrier { Id = "io.example.acme-map", Version = "1.0.0", Digest = shared.Digest, Signer = shared.Signers.Single() });
        var symbol = (await reader.ExpandAsync(new() { ModuleId = CodeMapPack.Id, Relation = "CODEMAP_DEFINES",
            Direction = ModuleTraversalDirection.Out, FromIds = [file.Id] }, context)).Edges.Should().ContainSingle().Subject.To;
        var tests = await reader.ExpandAsync(new() { ModuleId = CodeMapPack.Id, Relation = "CODEMAP_TESTS",
            Direction = ModuleTraversalDirection.In, FromIds = [symbol.Id] }, context);
        tests.Edges.Select(e => e.To.Properties["name"].GetString()).Should().BeEquivalentTo(["MapStoreTests.Remove_keeps_history", "MapStoreTests.Other"]);

        // A snapshot of the code map does not import into another definition.
        FluentActions.Invoking(() => shared.ToSnapshot(WorkPack.Manifest)).Should().Throw<PackLoadException>();
        await store.ImportAsync(NoCodeMap("git:clear"), at.AddSeconds(2));
    }

    [Fact]
    public async Task TheRealCodingFamilyAsACompositeInAPackSetComposesInAHostAndMigratesToExactlyTheSchemaOfTheCompiledFamily()
    {
        var members = new[] { "Decisions", "Repository", "CodeMap", "Work" }.Select(f => PackAuthoring.Build(RealSource(f), AmpackKey.PrivateKeyPem)).ToList();
        var composite = PackAuthoring.BuildComposite(RealSource("Coding", "composite.json"), members, AmpackKey.PrivateKeyPem, AmpackTrust());
        var set = PackFamilies.LoadSet(PackAuthoring.BuildSet("io.example.coding-family", "1.0.0", [composite, .. members], AmpackKey.PrivateKeyPem, AmpackTrust()), AmpackTrust());

        var family = set.Families.Should().ContainSingle().Subject;
        family.Id.Should().Be(CodingPack.Id);
        family.Members.Select(m => m.Id).Should().BeEquivalentTo(CodingPack.Manifest.Members.Select(m => m.Id));
        set.Packs.Should().BeEmpty();

        // A host composes the family from the packs, as it composes the compiled one, and applies its schema.
        await using var provider = Host(m => m.UseNeo4jStore().GrantChatClient(_ => NeverCalled).AddFamily(family));
        await using var scope = provider.CreateAsyncScope();
        var fromPacks = scope.ServiceProvider.GetRequiredService<ModuleSet>();
        (await new ModuleMigrator(fixture.TransactionRunner, fromPacks.Resolution).ApplyAsync("ampack family")).Problems.Should().BeEmpty();

        // The compiled family finds nothing left to apply: the packs carried exactly its schema.
        await using var compiledProvider = Host(m => m.UseNeo4jStore().GrantChatClient(_ => NeverCalled).AddComposite<CodingPack>());
        await using var compiledScope = compiledProvider.CreateAsyncScope();
        var compiled = compiledScope.ServiceProvider.GetRequiredService<ModuleSet>();
        compiled.Resolution.Modules.Select(m => m.Id).Should().BeEquivalentTo(fromPacks.Resolution.Modules.Select(m => m.Id));
        var plan = await new ModuleMigrator(fixture.TransactionRunner, compiled.Resolution).PlanAsync();
        plan.Pending.Should().BeEmpty();
        plan.PendingNamespaces.Should().BeEmpty();
        plan.PendingBookkeeping.Should().BeEmpty();
    }

    [Fact]
    public async Task AKnowledgePackPastItsValidUntilIsSwitchedOffInAHostThatLoadedItWhileValid()
    {
        // PACK-staleness: the real martial arts pack, valid for one more day, loaded and imported; the host's clock then passes it.
        var source = new Dictionary<string, byte[]>(PackAuthoring.FromModuleBundle(RealBundle("MartialArts")), StringComparer.Ordinal);
        var until = DateTimeOffset.UtcNow.AddDays(1);
        var manifest = JsonNode.Parse(source["pack.json"])!.AsObject();
        manifest["validUntil"] = until.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        source["pack.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        var pack = PackLoader.Load(PackAuthoring.Build(source, AmpackKey.PrivateKeyPem), AmpackTrust());
        pack.ValidUntil.Should().BeCloseTo(until, TimeSpan.FromSeconds(1));
        var importer = new KnowledgePackImporter(new Neo4jKnowledgePackStore(fixture.TransactionRunner));
        await importer.ImportAsync(pack.Knowledge!);
        try
        {
            var clock = new SettableClock(DateTimeOffset.UtcNow);
            await using var provider = Host(m => m.UseNeo4jStore().AddPack(pack), s => s.AddSingleton<TimeProvider>(clock));
            var request = new ContextRequest { SessionId = "s", Owner = "lea", Query = "Which kata do I learn for 8th kyu in Shotokan? Heian Shodan" };

            var valid = await CompileAsync(provider, request);
            valid.Sections.Should().Contain(s => s.Items.Any(i => i.Provenance != null && i.Provenance.RecordModuleId == pack.Id));

            clock.Now = until.AddMinutes(1);
            var expired = await CompileAsync(provider, request);
            expired.Sections.Should().NotContain(s => s.Items.Any(i => i.Provenance != null && i.Provenance.RecordModuleId == pack.Id));
            expired.Omissions.Should().Contain(o => o.Reason == ContextOmissionReason.Denied && o.Detail != null && o.Detail.Contains("valid until", StringComparison.Ordinal));
        }
        finally { await importer.RemoveAsync(pack.Manifest); }
    }

    private static async Task<ContextEnvelope> CompileAsync(IServiceProvider provider, ContextRequest request)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IContextCompiler>().CompileAsync(request);
    }

    private sealed class SettableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task PackMemoryReadsWhatTheStoreReadsForTheSameRecords()
    {
        // The real martial arts pack in Neo4j and in memory: list order, key reads and the best search hit agree.
        var pack = PackLoader.Load(PackAuthoring.Build(PackAuthoring.FromModuleBundle(RealBundle("MartialArts")), AmpackKey.PrivateKeyPem), AmpackTrust());
        var importer = new KnowledgePackImporter(new Neo4jKnowledgePackStore(fixture.TransactionRunner));
        await importer.ImportAsync(pack.Knowledge!);
        var memory = PackMemory.From([pack.Knowledge!]);
        using var services = Policies();
        var stored = new ModuleViews(pack.Knowledge!.Resolution,
            new Neo4jModuleReader(fixture.TransactionRunner, pack.Knowledge.Resolution, pack.Id, services.GetRequiredService<IMemoryIsolationPolicy>()));
        var held = new ModuleViews(memory.Resolution, memory.ReaderFor(pack.Id));
        var lea = new ModuleReadContext { Scope = MemoryScope.For("lea") };
        async Task<List<string>> Keys(ModuleViews views, string view, ModuleViewArguments arguments) =>
            [.. (await views.EvaluateWithArgumentsAsync(view, arguments, lea)).Items.Select(i => i.Record.Key)];

        // Ranks of two arts share steps; within a step the stores break ties by their own record ids, so compare step by step.
        async Task<List<string>> Ladder(ModuleViews views) =>
            [.. (await views.EvaluateWithArgumentsAsync("ladder", new(), lea)).Items.GroupBy(i => i.Record.Properties["step"].GetInt32())
                .Select(g => g.Key + ":" + string.Join(",", g.Select(i => i.Record.Key).Order(StringComparer.Ordinal)))];
        (await Ladder(held)).Should().Equal(await Ladder(stored));
        (await Keys(held, "ladder", new() { Key = "itf-taekwondo/9-gup" })).Should().Equal(await Keys(stored, "ladder", new() { Key = "itf-taekwondo/9-gup" }))
            .And.ContainSingle();
        foreach (var question in new[] { "Heian Shodan", "Which kata do I need for 6th kyu?", "What is a front kick called?", "judo throws" })
            (await Keys(held, "kata", new() { Search = question })).FirstOrDefault()
                .Should().Be((await Keys(stored, "kata", new() { Search = question })).FirstOrDefault(), question);
        (await importer.RemoveAsync(pack.Manifest)).Closed.Should().Be(93);

        // A code map in Neo4j and in memory: every step of the walk from a file to its tests reads the same records and degrees.
        var store = new Neo4jModuleSnapshotStore(fixture.TransactionRunner);
        var at = DateTimeOffset.UtcNow.AddMinutes(16);
        await store.ImportAsync(NoCodeMap("git:clear"), at.AddSeconds(-1));
        var map = MapSnapshot("git:same", "Close Remove", withOther: true);
        await store.ImportAsync(map, at);
        var inStore = new Neo4jModuleTraversalReader(fixture.TransactionRunner, KnowledgePack.ResolutionOf(CodeMapPack.Manifest),
            services.GetRequiredService<IMemoryIsolationPolicy>());
        var inMemory = PackMemory.From([], [map]);
        async Task<(List<string> Keys, ModuleRecord First)> Match(IModuleTraversalReader reader)
        {
            var found = await reader.MatchAsync(new() { ModuleId = CodeMapPack.Id, Type = CodeMapPack.FileType, Property = "path",
                Match = ModuleAnchorMatch.Exact, Values = ["src/Map/MapStore.cs"] }, lea);
            return ([.. found.Records.Select(r => r.Key)], found.Records[0]);
        }
        async Task<List<(string Key, int Degree)>> Expand(IModuleTraversalReader reader, string relation, ModuleTraversalDirection direction, string from, params string[] words) =>
            [.. (await reader.ExpandAsync(new() { ModuleId = CodeMapPack.Id, Relation = relation, Direction = direction, FromIds = [from], Words = words }, lea))
                .Edges.Select(e => (e.To.Key, e.Degree))];
        var (storedFiles, storedFile) = await Match(inStore);
        var (heldFiles, heldFile) = await Match(inMemory);
        heldFiles.Should().Equal(storedFiles);
        var storedTypes = await Expand(inStore, "CODEMAP_DEFINES", ModuleTraversalDirection.Out, storedFile.Id);
        (await Expand(inMemory, "CODEMAP_DEFINES", ModuleTraversalDirection.Out, heldFile.Id)).Should().Equal(storedTypes);
        var storedSymbol = (await inStore.ExpandAsync(new() { ModuleId = CodeMapPack.Id, Relation = "CODEMAP_DEFINES", Direction = ModuleTraversalDirection.Out,
            FromIds = [storedFile.Id] }, lea)).Edges.Single().To.Id;
        var heldSymbol = (await inMemory.ExpandAsync(new() { ModuleId = CodeMapPack.Id, Relation = "CODEMAP_DEFINES", Direction = ModuleTraversalDirection.Out,
            FromIds = [heldFile.Id] }, lea)).Edges.Single().To.Id;
        (await Expand(inMemory, "CODEMAP_TESTS", ModuleTraversalDirection.In, heldSymbol, "other"))
            .Should().Equal(await Expand(inStore, "CODEMAP_TESTS", ModuleTraversalDirection.In, storedSymbol, "other"));
        await store.ImportAsync(NoCodeMap("git:clear"), at.AddSeconds(1));
    }

    [Fact]
    public async Task AnAmpackThatWasChangedOrComesFromAnUntrustedKeyIsRefusedAndNothingIsWritten()
    {
        var manifest = PackManifest.Replace("io.example.packdojo", "io.example.refused", StringComparison.Ordinal).Replace("VERSION", "1.0.0", StringComparison.Ordinal);
        var source = PackAuthoring.FromModuleBundle(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["manifest.json"] = Encoding.UTF8.GetBytes(manifest),
            ["seed/records.jsonl"] = Encoding.UTF8.GetBytes(string.Join("\n", Version1) + "\n"),
        });
        var archive = PackAuthoring.Build(source, AmpackKey.PrivateKeyPem);
        var importer = new KnowledgePackImporter(new Neo4jKnowledgePackStore(fixture.TransactionRunner));
        var declared = ModuleManifest.Parse(manifest);

        // One record changed inside the signed archive.
        var changed = Rewrite(archive, "records/records.jsonl", text => text.Replace("down block", "up block", StringComparison.Ordinal));
        FluentActions.Invoking(() => PackLoader.Load(changed, AmpackTrust())).Should().Throw<PackLoadException>();
        // Signed by a key this host does not trust.
        var stranger = PackAuthoring.CreateKey();
        FluentActions.Invoking(() => PackLoader.Load(PackAuthoring.Build(source, stranger.PrivateKeyPem), AmpackTrust()))
            .Should().Throw<PackLoadException>().Which.Problems.Should().Contain(p => p.Contains("publisher signature", StringComparison.Ordinal));

        (await importer.GetStateAsync(declared)).Should().Be(new KnowledgePackState { PackId = "io.example.refused" });
        // The intact archive from the trusted key is the one that loads.
        PackLoader.Load(archive, AmpackTrust()).Knowledge!.Proposals.Should().HaveCount(Version1.Length);
    }

    // Members that declare inference need a granted model to compose; nothing here asks it anything.
    private static readonly IChatClient NeverCalled = NSubstitute.Substitute.For<IChatClient>();

    private ServiceProvider Host(Action<ModuleSetBuilder> modules, Action<IServiceCollection>? more = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        more?.Invoke(services);
        services.AddNeo4jAgentMemory(_ => { }, o =>
        {
            o.Uri = fixture.ConnectionString;
            o.Username = fixture.User;
            o.Password = fixture.Password;
            o.Database = "neo4j";
            o.EmbeddingDimensions = Neo4jIntegrationFixture.TestEmbeddingDimensions;
        });
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
            new StubEmbeddingGenerator(sp.GetRequiredService<ILogger<StubEmbeddingGenerator>>(), Neo4jIntegrationFixture.TestEmbeddingDimensions));
        services.AddAgentMemoryModules(modules);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    // The archive with one entry's text changed and everything else, including the signature, kept.
    private static byte[] Rewrite(byte[] archive, string entry, Func<string, string> change)
    {
        using var stream = new MemoryStream();
        stream.Write(archive);
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
        {
            var found = zip.GetEntry(entry) ?? throw new InvalidOperationException($"{entry} is not in the archive.");
            string text;
            using (var reader = new StreamReader(found.Open())) text = reader.ReadToEnd();
            found.Delete();
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
            writer.Write(change(text));
        }
        return stream.ToArray();
    }
}
