#pragma warning disable AMEXT001
using System.Text;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Extensibility.Neo4j.Storage;
using AgentMemory.Extensibility.Packs;
using FluentAssertions;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// PACK-REMOVE-MISSING-ARCHIVE: a removal read the pack's own manifest, so a host whose stored copy of a pack was gone or broken
/// could never remove it, and its records stayed open. A removal by the pack's id reads what the store itself holds of it.
/// </summary>
public sealed partial class ModuleRecordWriteIntegrationTests
{
    /// <summary>
    /// The real martial arts pack under another id and other names, so a test owns its labels: every <c>MartialArts</c> and
    /// <c>MARTIAL_ARTS</c> in its manifest and seed becomes <paramref name="name"/> and <paramref name="relationName"/>; then
    /// <paramref name="edit"/> may change its manifest.
    /// </summary>
    private static LoadedPack RenamedMartialArts(string id, string name, string relationName, Action<System.Text.Json.Nodes.JsonObject>? edit = null)
    {
        var bundle = RealBundle("MartialArts").ToDictionary(file => file.Key, file => file.Key == "manifest.json" || file.Key.EndsWith(".jsonl", StringComparison.Ordinal)
            ? Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(file.Value).Replace("MartialArts", name, StringComparison.Ordinal)
                .Replace("MARTIAL_ARTS", relationName, StringComparison.Ordinal))
            : file.Value, StringComparer.Ordinal);
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(bundle["manifest.json"])!.AsObject();
        manifest["id"] = id;
        edit?.Invoke(manifest);
        bundle["manifest.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        return PackLoader.Load(PackAuthoring.Build(PackAuthoring.FromModuleBundle(bundle), AmpackKey.PrivateKeyPem), AmpackTrust());
    }

    private async Task<(long Live, long Closed)> PackRecordsAsync(string label, string packId)
    {
        var row = (await Rows($"MATCH (n:`{label}`) WHERE n.source_pack='{packId}' RETURN count(CASE WHEN n.invalidated_at IS NULL THEN 1 END) AS live, "
            + "count(CASE WHEN n.invalidated_at IS NOT NULL THEN 1 END) AS closed")).Single();
        return ((long)row["live"], (long)row["closed"]);
    }

    [Fact]
    public async Task APackWhoseBundleIsGoneIsRemovedByItsIdFromWhatTheStoreHoldsAndLoadsAgainLater()
    {
        var pack = RenamedMartialArts("io.example.packs.lostarts", "LostArts", "LOST_ARTS");
        var meaning = new PackMeaning(new ConceptEmbedder("concepts-1"), new() { MinimumScore = 0.8 });
        var importer = new KnowledgePackImporter(new Neo4jKnowledgePackStore(fixture.TransactionRunner, null, meaning));
        var count = pack.Knowledge!.Proposals.Count;
        var kata = pack.Knowledge.Proposals.Count(p => p.Type == "LostArtsKata");
        await importer.ImportAsync(pack.Knowledge);
        try
        {
            (await Rows("MATCH (v)-[:AMPACK_MEANS]->(n:LostArtsKata) RETURN count(v) AS n")).Single()["n"].Should().Be((long)kata, "searched by meaning");

            // Only the id: no manifest and no bundle.
            (await importer.RemoveByIdAsync(pack.Id)).Should().Be(new KnowledgePackImportResult { PackId = pack.Id, Closed = count });

            (await importer.GetStateAsync(pack.Manifest)).LiveRecords.Should().Be(0);
            (await PackRecordsAsync("LostArtsKata", pack.Id)).Should().Be((0L, (long)kata), "every record closed, its history kept");
            (await Rows("MATCH (k:ModuleKey {module_id:'io.example.packs.lostarts'}) WHERE k.current_id IS NOT NULL RETURN k")).Should().BeEmpty();
            (await Rows("MATCH (v)-[:AMPACK_MEANS]->(n:LostArtsKata) RETURN v")).Should().BeEmpty("a closed record keeps no vector");

            // The end state is a removal's: a second removal closes nothing, by either path, and the pack loads again.
            (await importer.RemoveByIdAsync(pack.Id)).Closed.Should().Be(0);
            (await importer.RemoveAsync(pack.Manifest)).Closed.Should().Be(0);
            var again = await importer.ImportAsync(pack.Knowledge);
            (again.Created + again.Changed).Should().Be(count);
            (await PackRecordsAsync("LostArtsKata", pack.Id)).Should().Be(((long)kata, (long)kata));

            // An id the store holds nothing of closes nothing, and writes nothing.
            (await importer.RemoveByIdAsync("io.example.packs.neverloaded")).Should().Be(new KnowledgePackImportResult { PackId = "io.example.packs.neverloaded" });
            (await Rows("MATCH (m:ModuleMigration) WHERE m.module_id='io.example.packs.neverloaded' OR m.owner_module_id='io.example.packs.neverloaded' RETURN m"))
                .Should().BeEmpty();
        }
        finally { await importer.RemoveAsync(pack.Manifest); }
    }

    [Fact]
    public async Task APackRemovedByItsIdOverInconsistentKeysIsRefusedAtItsRecordsStepAndNothingCloses()
    {
        var pack = RenamedMartialArts("io.example.packs.keyedarts", "KeyedArts", "KEYED_ARTS");
        var importer = new KnowledgePackImporter(new Neo4jKnowledgePackStore(fixture.TransactionRunner));
        var count = pack.Knowledge!.Proposals.Count;
        await importer.ImportAsync(pack.Knowledge);
        var current = (string)(await Rows("MATCH (n:KeyedArtsArt) WHERE n.invalidated_at IS NULL RETURN n.id AS id ORDER BY id LIMIT 1")).Single()["id"];
        Task Repair() => Execute($"MATCH (k:ModuleKey) WHERE k.current_id='{current}-elsewhere' SET k.current_id='{current}'");
        try
        {
            // One key no longer names its record as current, as a store changed by hand might: the removal by manifest refuses
            // this store, and the removal by id refuses it the same way, in one transaction, so no record closes.
            await Execute($"MATCH (k:ModuleKey) WHERE k.current_id='{current}' SET k.current_id='{current}-elsewhere'");

            var refused = await FluentActions.Awaiting(() => importer.RemoveByIdAsync(pack.Id)).Should().ThrowAsync<KnowledgePackException>();

            PackFailure.Of(refused.Which).Should().BeEquivalentTo(new
            {
                Code = "pack.remove.records.refused", PackId = pack.Id, Operation = PackOperations.Remove, Step = PackSteps.Records,
            });
            refused.Which.Problems.Single().Should().Be("Module key current reference does not match its last version.");
            (await importer.GetStateAsync(pack.Manifest)).LiveRecords.Should().Be(count, "nothing closed");

            await Repair();
            (await importer.RemoveByIdAsync(pack.Id)).Closed.Should().Be(count);
        }
        finally { await Repair(); await importer.RemoveAsync(pack.Manifest); }
    }
}
