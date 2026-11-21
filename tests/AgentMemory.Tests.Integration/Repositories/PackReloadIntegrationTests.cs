#pragma warning disable AMEXT001
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Extensibility.Neo4j.Storage;
using AgentMemory.Extensibility.Packs;
using FluentAssertions;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// PACK-RELOAD-RCA: at the 19 Nov talk, the martial arts pack was removed and loaded again, and the host reported a Neo4j that
/// was not reachable. These run the same sequence on a real Neo4j, with and without search by meaning, so a failure names its
/// step (DIAG-PACK-FAILURES) instead of a database that is down.
/// </summary>
public sealed partial class ModuleRecordWriteIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheMartialArtsPackRemovedAndLoadedAgainImportsAgainWithEveryRecordLive(bool byMeaning)
    {
        var pack = PackLoader.Load(PackAuthoring.Build(PackAuthoring.FromModuleBundle(RealBundle("MartialArts")), AmpackKey.PrivateKeyPem), AmpackTrust());
        var meaning = byMeaning ? new PackMeaning(new ConceptEmbedder("concepts-1"), new() { MinimumScore = 0.8 }) : null;
        var importer = new KnowledgePackImporter(new Neo4jKnowledgePackStore(fixture.TransactionRunner, null, meaning));
        var count = pack.Knowledge!.Proposals.Count;
        try
        {
            await importer.ImportAsync(pack.Knowledge);
            (await importer.RemoveAsync(pack.Manifest)).Closed.Should().Be(count);
            (await importer.GetStateAsync(pack.Manifest)).LiveRecords.Should().Be(0);

            var again = await FluentActions.Awaiting(() => importer.ImportAsync(pack.Knowledge)).Should().NotThrowAsync(
                "a pack that was removed loads again; when it does not, the failure must name its step");

            (again.Subject.Created + again.Subject.Changed).Should().Be(count, "every closed key is written again");
            (await importer.GetStateAsync(pack.Manifest)).LiveRecords.Should().Be(count);

            // And a third time round, as a person toggling the pack in a demo would.
            await importer.RemoveAsync(pack.Manifest);
            await importer.ImportAsync(pack.Knowledge);
            (await importer.GetStateAsync(pack.Manifest)).LiveRecords.Should().Be(count);
        }
        finally { await importer.RemoveAsync(pack.Manifest); }
    }

    [Fact]
    public async Task TheMartialArtsPackUnderANewIdIsRefusedTheOldIdsNamesAndTheFailureNamesThePackTheStepAndTheOwner()
    {
        // The fault at the talk, reproduced: the packs moved from io.agentmemory.* to io.github.joslat.* on 18 Nov with the same
        // ontology. A removal keeps the schema and who owns it, so the old id still holds the MartialArts names, and the pack under
        // its new id is refused at its schema step. The host showed that as a Neo4j that was not reachable.
        var source = new Dictionary<string, byte[]>(PackAuthoring.FromModuleBundle(RealBundle("MartialArts")), StringComparer.Ordinal);
        var old = PackLoader.Load(PackAuthoring.Build(source, AmpackKey.PrivateKeyPem), AmpackTrust());
        var renamed = System.Text.Json.Nodes.JsonNode.Parse(source["pack.json"])!.AsObject();
        renamed["id"] = "io.example.packs.martialarts";
        source["pack.json"] = System.Text.Encoding.UTF8.GetBytes(renamed.ToJsonString());
        var moved = PackLoader.Load(PackAuthoring.Build(source, AmpackKey.PrivateKeyPem), AmpackTrust());
        var importer = new KnowledgePackImporter(new Neo4jKnowledgePackStore(fixture.TransactionRunner));
        await importer.ImportAsync(old.Knowledge!);
        try
        {
            await importer.RemoveAsync(old.Manifest);

            var refused = await FluentActions.Awaiting(() => importer.ImportAsync(moved.Knowledge!)).Should().ThrowAsync<InvalidOperationException>();

            PackFailure.Of(refused.Which).Should().BeEquivalentTo(new
            {
                Code = "pack.import.schema.refused", PackId = "io.example.packs.martialarts", Step = PackSteps.Schema, Kind = PackFailureKinds.Refused,
            });
            refused.Which.Message.Should().Contain($"Namespace is owned by another module ({old.Id}): MartialArtsKata");
            (await importer.GetStateAsync(moved.Manifest)).LiveRecords.Should().Be(0, "nothing of the refused pack was written");
        }
        finally
        {
            await importer.ImportAsync(old.Knowledge!);
            await importer.RemoveAsync(old.Manifest);
        }
    }
}
