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

    [Fact]
    public void An_authored_erasure_verifies_names_no_key_and_its_receipt_names_nothing_else()
    {
        // Feature 08: ampack erase's library half; the notice carries salted hashes only, and the host's receipt only the
        // erasure's identity and when it was applied.
        var publisher = PackAuthoring.CreateKey();
        var host = PackAuthoring.CreateKey();
        var policy = PackTrust.Parse(System.Text.Encoding.UTF8.GetBytes(new System.Text.Json.Nodes.JsonObject
        {
            ["format"] = "agentmemory.pack.trust/1.1",
            ["keys"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["keyId"] = publisher.KeyId, ["publicKey"] = publisher.PublicKeyPem, ["idPrefixes"] = new System.Text.Json.Nodes.JsonArray("io.example"), ["roles"] = new System.Text.Json.Nodes.JsonArray("publisher"),
            }),
        }.ToJsonString())).Policy;
        using var signer = AgentMemory.Packs.Format.PackSigner.FromPem(publisher.PrivateKeyPem);

        var envelope = AgentMemory.Packs.Format.PackErasure.Create("io.example.notes", [("NotesNote", "person/jose")], "legal", DateTimeOffset.UtcNow, signer);
        var (erasure, signedBy, problems) = AgentMemory.Packs.Format.PackErasure.Verify(envelope, "io.example.notes", policy, DateTimeOffset.UtcNow);

        problems.Should().BeEmpty();
        signedBy.Should().Be(publisher.KeyId);
        erasure!.Erases("NotesNote", "person/jose").Should().BeTrue();
        var payload = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(System.Text.Json.JsonDocument.Parse(envelope).RootElement.GetProperty("payload").GetString()!));
        payload.Should().NotContain("person/jose", "the notice names the record by its salted hash only");
        AgentMemory.Packs.Format.PackErasure.Verify(envelope, "io.example.other", policy, DateTimeOffset.UtcNow).Erasure.Should().BeNull("an erasure is for its pack");

        using var hostSigner = AgentMemory.Packs.Format.PackSigner.FromPem(host.PrivateKeyPem);
        var receipt = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(System.Text.Json.JsonDocument.Parse(
            AgentMemory.Packs.Format.PackErasure.Receipt(envelope, DateTimeOffset.UtcNow, hostSigner)).RootElement.GetProperty("payload").GetString()!));
        receipt.Should().Contain(AgentMemory.Packs.Format.PackCertificate.IdentityOf(envelope)!).And.NotContain("NotesNote").And.Contain("erasure-receipt");
    }

    [Fact]
    public async Task A_module_set_never_offers_a_record_an_erasure_it_holds_names_whatever_the_store_holds()
    {
        // Feature 08 (R-CORE-134): before an import applies it, and if a store still holds the record, a view leaves it out.
        var erasure = Erasure(new ModuleErasedRecord("T", "gone", null)) with { PackId = "m" };
        var set = new Inner();
        bool Erased(ModuleRecord r) => erasure.PackId == r.ModuleId && erasure.Erases(r.Type, r.Key);
        var reader = new ModuleSet.LiveReader(set, live: null, TimeProvider.System, Erased);

        (await reader.FindAsync(new() { ModuleId = "m", Type = "T", Limit = 5 }, new())).Records.Select(r => r.Key).Should().Equal("kept");
    }

    private sealed class Inner : IModuleReader
    {
        public string CallerModuleId => "m";

        public Task<ModuleReadResult> FindAsync(ModuleQuery query, ModuleReadContext context, CancellationToken cancellationToken = default)
        {
            ModuleRecord Record(string key) => new()
            {
                ModuleId = "m", Type = "T", Id = key, Key = key, CreatedAtUtc = DateTimeOffset.UnixEpoch, SourceSessionId = "pack", Source = new ProposalSource { Excerpt = key },
            };
            return Task.FromResult(new ModuleReadResult { Status = ModuleReadStatus.Succeeded, Records = [Record("gone"), Record("kept")] });
        }
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
