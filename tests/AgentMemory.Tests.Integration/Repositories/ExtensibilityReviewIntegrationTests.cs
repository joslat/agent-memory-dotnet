#pragma warning disable AMEXT001
using AgentMemory.Extensibility.Neo4j.Storage;
using AgentMemory.Extensibility.Packs;

namespace AgentMemory.Tests.Integration.Repositories;

public sealed partial class ModuleRecordWriteIntegrationTests
{
    // RVW-REST-EXTENSIBILITY (the review's extensibility-0): a later version that drops a type keeps that type's records as
    // history; an erasure naming such a record, issued with that later version, deleted nothing and left no tombstone,
    // because only the version's own types were visited.
    [Fact]
    public async Task An_erasure_reaches_a_record_whose_type_the_current_version_no_longer_declares()
    {
        await LocalSchema(); var pack = FoundationPack();
        await new KnowledgePackImporter(new Neo4jKnowledgePackStore(fixture.TransactionRunner)).ImportAsync(pack);
        await using (var session = fixture.Driver.AsyncSession())
            await (await session.RunAsync("CREATE (:RetiredPerson {key: 'john-doe', source_pack: $pack})", new { pack = pack.Id })).ConsumeAsync();
        var erasure = new ModuleErasure { PackId = pack.Id, Statement = "sha256:" + new string('f', 64), IssuedAt = DateTimeOffset.UtcNow,
            Reason = "legal", Salt = "0123456789abcdef0123456789abcdef", Records = [new("RetiredPerson", "john-doe", null)] };

        var result = await new Neo4jModuleStore(fixture.TransactionRunner, pack.Resolution)
            .ErasePackAsync(pack.Id, [erasure], DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(1, result.Erased);
        Assert.Equal(1, result.Tombstones);
        Assert.Empty(await Rows("MATCH (n:RetiredPerson {key: 'john-doe'}) RETURN n"));
        Assert.Single(await Rows("MATCH (t:ModuleTombstone {pack: '" + pack.Id + "', type: 'RetiredPerson'}) RETURN t"));
    }

    [Fact]
    public async Task An_erasure_type_that_is_not_a_label_is_never_used_as_one()
    {
        await LocalSchema(); var pack = FoundationPack();
        await new KnowledgePackImporter(new Neo4jKnowledgePackStore(fixture.TransactionRunner)).ImportAsync(pack);
        var erasure = new ModuleErasure { PackId = pack.Id, Statement = "sha256:" + new string('a', 64), IssuedAt = DateTimeOffset.UtcNow,
            Reason = "legal", Salt = "0123456789abcdef0123456789abcdef", Records = [new("X` DETACH DELETE n //", "k", null)] };

        var result = await new Neo4jModuleStore(fixture.TransactionRunner, pack.Resolution)
            .ErasePackAsync(pack.Id, [erasure], DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(0, result.Erased);
        Assert.Equal(0, result.Tombstones);
    }

    // RVW-REST-EXTENSIBILITY (the review's extensibility-1): two imports of one pack at once. The second read what the pack
    // held before the first committed, so it never closed the record the first wrote and it does not hold: both versions'
    // records stayed live. Version 1 holds every kata, version 2 all but the first. Both are imported once the usual way (their
    // migrations applied, the store at version 2); then version 1 again and, while it waits to commit, version 2.
    [Fact]
    public async Task A_concurrent_import_of_another_version_closes_what_the_first_wrote_and_it_does_not_hold()
    {
        await LocalSchema(); var v1 = FoundationPack(); var v2 = FoundationPack("2.0.0", v1, removeFirst: true);
        var importer = new KnowledgePackImporter(new Neo4jKnowledgePackStore(fixture.TransactionRunner));
        await importer.ImportAsync(v1); await importer.ImportAsync(v2);
        var pause = new PauseLocalCommit(fixture.TransactionRunner);
        var first = new Neo4jModuleStore(pause, v1.Resolution).ImportPackAsync(v1, DateTimeOffset.UtcNow.AddSeconds(1), CancellationToken.None);
        await Task.WhenAny(pause.HoldingLock.Task, first).WaitAsync(TimeSpan.FromSeconds(30));
        if (first.IsCompleted) await first;
        var second = new Neo4jModuleStore(fixture.TransactionRunner, v2.Resolution).ImportPackAsync(v2, DateTimeOffset.UtcNow.AddSeconds(2), CancellationToken.None);
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (DateTimeOffset.UtcNow < deadline && !second.IsCompleted)
            {
                var rows = await Rows("SHOW TRANSACTIONS YIELD currentQuery,status WHERE currentQuery CONTAINS 'ModuleKey' AND status STARTS WITH 'Blocked' RETURN count(*) AS count");
                if ((long)rows.Single()["count"] > 0) break;
                await Task.Delay(25);
            }
        }
        finally { pause.Release.TrySetResult(); }
        await first; await second;

        var module = v2.Resolution.Modules.Single(m => m.Id == v2.Id);
        var live = await fixture.TransactionRunner.ReadAsync(tx => new Neo4jModuleStore(fixture.TransactionRunner, v2.Resolution).PackLiveAsync(tx, module, CancellationToken.None));
        Assert.Equal(v2.Proposals.Select(p => (p.Type, p.Key)).OrderBy(k => k.Key).ToArray(), live.Keys.OrderBy(k => k.Key).ToArray());
    }

    // RVW-REST-EXTENSIBILITY (the review's extensibility-4): removing a pack by a manifest that shares no type with the version
    // the store holds (its types renamed, its own import refused) found no applied step, closed nothing and reported success.
    [Fact]
    public async Task Removing_by_a_manifest_whose_types_the_store_never_held_closes_the_version_it_holds()
    {
        await LocalSchema(); var v1 = FoundationPack();
        await new KnowledgePackImporter(new Neo4jKnowledgePackStore(fixture.TransactionRunner)).ImportAsync(v1);
        var ontology = v1.Manifest.Ontology!;
        var renamed = v1.Manifest with
        {
            Version = AgentMemory.Extensibility.Modules.ModuleVersion.Parse("2.0.0"),
            Ontology = ontology with { Types = [ontology.Types[0] with { Name = ontology.LabelPrefix + "Dish" }], Relations = [] },
            Views = [], Traversal = null,
        };

        var removed = await new Neo4jKnowledgePackStore(fixture.TransactionRunner).RemoveAsync(renamed, DateTimeOffset.UtcNow);

        Assert.Equal(v1.Proposals.Count, removed.Closed);
        var module = v1.Resolution.Modules.Single(m => m.Id == v1.Id);
        Assert.Empty(await fixture.TransactionRunner.ReadAsync(tx => new Neo4jModuleStore(fixture.TransactionRunner, v1.Resolution).PackLiveAsync(tx, module, CancellationToken.None)));
    }

    // RVW-REST-EXTENSIBILITY (the review's extensibility-6): an upgrade that changed no record left every record stamped with the
    // older version, so the store reported that one and an import between the two passed the downgrade check.
    [Fact]
    public async Task An_upgrade_that_changed_no_record_still_refuses_an_older_version()
    {
        await LocalSchema(); var v1 = FoundationPack(); var v2 = FoundationPack("2.0.0", v1); var v15 = FoundationPack("1.5.0", v1);
        var store = new Neo4jKnowledgePackStore(fixture.TransactionRunner); var importer = new KnowledgePackImporter(store);
        await importer.ImportAsync(v1);
        var upgrade = await importer.ImportAsync(v2);
        Assert.Equal(0, upgrade.Created + upgrade.Changed);

        Assert.Equal("2.0.0", (await store.GetStateAsync(v2.Manifest)).Version);
        var refused = await Assert.ThrowsAsync<KnowledgePackException>(() => importer.ImportAsync(v15));
        Assert.Contains("newer than 1.5.0", Assert.Single(refused.Problems));
    }
}
