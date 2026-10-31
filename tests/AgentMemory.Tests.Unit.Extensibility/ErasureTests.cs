#pragma warning disable AMEXT001
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Extensibility.Reading;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// Feature 08: an erasure names records by key or salted key hash; a store removes them and keeps tombstones that no import
/// passes; memory never holds them. The verifiers read the signed statement (the kit); these hold the store's side.
/// </summary>
public sealed class ErasureTests
{
    private const string Salt = "0123456789abcdef0123456789abcdef";

    private static ModuleErasure Erasure(params ModuleErasedRecord[] records) => new()
    {
        PackId = "com.example.code", Statement = "sha256:" + new string('e', 64), IssuedAt = DateTimeOffset.UnixEpoch, Reason = "withdrawn", Salt = Salt,
        Records = records,
    };

    [Fact]
    public void A_key_hash_is_the_salted_sha256_both_verifiers_and_the_stores_compute()
    {
        // One source (shared/ErasureHash.cs) for the format and the stores; pinned, so neither can drift alone.
        ModuleErasure.KeyHash(Salt, "note-1").Should().Be("4c1f78b0b5ebc40072f1381351240626baf85912ac83d2a211a0a76f998fb4fb");
        Erasure(new ModuleErasedRecord("T", null, ModuleErasure.KeyHash(Salt, "k"))).Erases("T", "k").Should().BeTrue("by its hash");
        Erasure(new ModuleErasedRecord("T", "k", null)).Erases("T", "k").Should().BeTrue("by its key");
        Erasure(new ModuleErasedRecord("T", "k", null)).Erases("U", "k").Should().BeFalse("only records of its type");
    }

    [Fact]
    public async Task Memory_never_holds_an_erased_record_and_links_into_it_are_left_out()
    {
        const string Code = """
            {"format":"agentmemory.module/v1alpha1","id":"com.example.code","version":"1.0.0","sdk":"[0.1,0.2)",
             "ontology":{"labelPrefix":"Code","types":[{"name":"CodeFile","scope":"shared","render":"{path}","properties":{"path":{"type":"string","required":true}}}],
              "relations":[{"name":"CODE_USES","from":"CodeFile","to":"CodeFile"}]}}
            """;
        ModuleProposal File(string path, params string[] uses) => new()
        {
            Kind = ProposalKind.Assert, Type = "CodeFile", Key = path,
            Properties = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal) { ["path"] = System.Text.Json.JsonSerializer.SerializeToElement(path) },
            Links = [.. uses.Select(u => new ProposedLink { Relation = "CODE_USES", Target = u })], Source = new ProposalSource { Excerpt = path },
        };
        var shot = new ModuleSnapshot { Module = ModuleManifest.Parse(Code), Source = "git:abc", Records = [File("src/secret.cs"), File("src/a.cs", "src/secret.cs")] };

        var memory = PackMemory.From([], [shot], erasures: [Erasure(new ModuleErasedRecord("CodeFile", null, ModuleErasure.KeyHash(Salt, "src/secret.cs")))]);

        var held = (await memory.ReaderFor("com.example.code").FindAsync(new ModuleQuery { ModuleId = "com.example.code", Type = "CodeFile", Limit = 10 },
            new ModuleReadContext { Scope = AgentMemory.Abstractions.Options.MemoryScope.For("pack") })).Records;
        held.Select(r => r.Key).Should().Equal("src/a.cs");
        held.Single().Links.Should().BeEmpty("a link into an erased record goes with it");
    }

    [Fact]
    public async Task An_import_applies_the_erasures_that_name_its_pack_before_it_writes()
    {
        var store = new Store();
        var pack = KnowledgePack.Open(KnowledgePackTests.Signed(), KnowledgePackTests.Trust());
        var importer = new KnowledgePackImporter(store);

        await importer.ImportAsync(pack, [Erasure(new ModuleErasedRecord("T", "k", null)) with { PackId = pack.Id }, Erasure(new ModuleErasedRecord("T", "x", null)) with { PackId = "io.example.other" }]);

        store.Calls.Should().Equal("erase 1", "import");
    }

    private sealed class Store : IKnowledgePackStore
    {
        public List<string> Calls { get; } = [];
        public Task<KnowledgePackState> GetStateAsync(ModuleManifest pack, CancellationToken cancellationToken = default) => Task.FromResult(new KnowledgePackState { PackId = pack.Id });
        public Task<KnowledgePackImportResult> ImportAsync(KnowledgePack pack, DateTimeOffset atUtc, CancellationToken cancellationToken = default)
        { Calls.Add("import"); return Task.FromResult(new KnowledgePackImportResult { PackId = pack.Id }); }
        public Task<KnowledgePackImportResult> RemoveAsync(ModuleManifest pack, DateTimeOffset atUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult(new KnowledgePackImportResult { PackId = pack.Id });
        public Task<KnowledgePackEraseResult> EraseAsync(ModuleManifest pack, IReadOnlyList<ModuleErasure> erasures, DateTimeOffset atUtc, CancellationToken cancellationToken = default)
        { Calls.Add("erase " + erasures.Count); return Task.FromResult(new KnowledgePackEraseResult { PackId = pack.Id }); }
    }
}
