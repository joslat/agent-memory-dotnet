#pragma warning disable AMEXT001
using System.Text.Json.Nodes;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Extensibility.Neo4j.Storage;
using AgentMemory.Extensibility.Packs;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// PACK-NAME-TAKEOVER, the owner's decision D-06 (22 Nov 2026): a pack that comes back under a new id cannot take the names its
/// removed old id still owns (<see cref="TheMartialArtsPackUnderANewIdIsRefusedTheOldIdsNamesAndTheFailureNamesThePackTheStepAndTheOwner"/>
/// pins that refusal). An operator transfers them, explicitly: the names, the records with their history, and the tombstones.
/// </summary>
public sealed partial class ModuleRecordWriteIntegrationTests
{
    private const string TransferActor = "operator:integration-test";

    // The live records of a pack's labels and their links, by key: what a walk over the pack finds.
    private async Task<IReadOnlyList<string>> LiveWalkAsync(string prefix, string relationPrefix) =>
        [.. (await Rows($"MATCH (s)-[r]->(t) WHERE any(l IN labels(s) WHERE l STARTS WITH '{prefix}') AND type(r) STARTS WITH '{relationPrefix}' "
            + "AND s.invalidated_at IS NULL AND t.invalidated_at IS NULL "
            + "RETURN labels(s)[0] + '/' + s.key + ' ' + type(r) + ' ' + labels(t)[0] + '/' + t.key AS edge ORDER BY edge"))
            .Select(r => (string)r["edge"])];

    // Every record of a pack's labels, by id, with its relationships to any node: the store's history of the pack, as it is.
    private async Task<IReadOnlyList<string>> HistoryWalkAsync(string prefix) =>
        [.. (await Rows($"MATCH (n) WHERE any(l IN labels(n) WHERE l STARTS WITH '{prefix}') OPTIONAL MATCH (n)-[r]->(m) "
            + "RETURN n.id + ' ' + coalesce(type(r) + ' ' + m.id, '-') AS edge ORDER BY edge")).Select(r => (string)r["edge"])];

    // The ledger rows that name a pack id, as text: what a refused transfer must leave as it was.
    private async Task<IReadOnlyList<string>> LedgerAsync(params string[] packs) =>
        [.. (await Rows("MATCH (m:ModuleMigration) WHERE m.module_id IN $packs OR m.owner_module_id IN $packs OR any(p IN $packs WHERE m.key CONTAINS p) "
            .Replace("$packs", "[" + string.Join(",", packs.Select(p => "'" + p + "'")) + "]", StringComparison.Ordinal)
            + "RETURN m.module_id + '|' + m.key + '|' + coalesce(m.owner_module_id, '') + '|' + coalesce(m.state, '') AS row ORDER BY row"))
            .Select(r => (string)r["row"])];

    [Fact]
    public async Task ARemovedPacksNamesRecordsAndTombstonesTransferToItsNewIdWithTheHistoryKeptAndTheWalkUnchanged()
    {
        var old = RenamedMartialArts("io.agentmemory.packs.handover", "Handover", "HANDOVER");
        var moved = RenamedMartialArts("io.example.packs.handover", "Handover", "HANDOVER");
        var log = new ListLogger<KnowledgePackImporter>();
        var meaning = new PackMeaning(new ConceptEmbedder("concepts-1"), new() { MinimumScore = 0.8 });
        var importer = new KnowledgePackImporter(new Neo4jKnowledgePackStore(fixture.TransactionRunner, null, meaning), null, log);
        var count = old.Knowledge!.Proposals.Count;

        // The old pack imported, one of its kata erased (every version removed, a tombstone kept), then removed: what a host held
        // before the packs moved from io.agentmemory.* to their new ids.
        var erased = old.Knowledge.Proposals.First(p => p.Type == "HandoverKata").Key;
        await importer.ImportAsync(old.Knowledge);
        await importer.ImportAsync(old.Knowledge, [new ModuleErasure
        {
            PackId = old.Id, Statement = "sha256:transfer-test", IssuedAt = DateTimeOffset.UtcNow, Reason = "withdrawn", Salt = "salt-1",
            Records = [new ModuleErasedRecord("HandoverKata", erased, null)],
        }]);
        var walk = await LiveWalkAsync("Handover", "HANDOVER_");
        walk.Should().NotBeEmpty();
        await importer.RemoveAsync(old.Manifest);
        var history = await HistoryWalkAsync("Handover");
        var names = (await Rows($"MATCH (m:ModuleMigration {{module_id:'io.agentmemory.sdk'}}) WHERE m.owner_module_id='{old.Id}' RETURN m.key AS key")).Count;
        var steps = (await Rows($"MATCH (m:ModuleMigration {{module_id:'{old.Id}'}}) RETURN m.key AS key")).Count;
        var relationships = (long)(await Rows("MATCH (n)-[r]-() WHERE any(l IN labels(n) WHERE l STARTS WITH 'Handover') RETURN count(DISTINCT r) AS n")).Single()["n"];

        // Today's refusal first: the new id cannot take the names.
        (await FluentActions.Awaiting(() => importer.ImportAsync(moved.Knowledge!)).Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain($"Namespace is owned by another module ({old.Id})");

        var result = await importer.TransferAsync(old.Id, moved.Knowledge!, TransferActor);

        result.Should().BeEquivalentTo(new KnowledgePackTransferResult
        {
            FromPackId = old.Id, ToPackId = moved.Id, Actor = TransferActor, AtUtc = result.AtUtc, Names = names, SchemaSteps = steps,
            Records = count - 1, Relationships = (int)relationships, Tombstones = 1,
        });
        names.Should().BeGreaterThan(4); steps.Should().BeGreaterThan(4);

        // The history kept: the same records, the same ids and the same relationships, now the new id's, each naming the old one.
        (await HistoryWalkAsync("Handover")).Should().Equal(history);
        (await PackRecordsAsync("HandoverKata", moved.Id)).Should().Be((0L, (long)old.Knowledge.Proposals.Count(p => p.Type == "HandoverKata") - 1));
        (await Rows($"MATCH (n) WHERE any(l IN labels(n) WHERE l STARTS WITH 'Handover') AND (coalesce(n.source_pack, '')<>'{moved.Id}' "
            + $"OR coalesce(n.source_pack_transferred_from, '')<>'{old.Id}') RETURN n")).Should().BeEmpty();

        // The names are the new id's, the old id owns none, and the store keeps who transferred what and when.
        (await Rows($"MATCH (m:ModuleMigration) WHERE m.module_id='{old.Id}' OR m.owner_module_id='{old.Id}' RETURN m")).Should().BeEmpty();
        var audit = (await Rows($"MATCH (m:ModuleMigration {{module_id:'io.agentmemory.sdk'}}) WHERE m.key STARTS WITH 'transfer/{old.Id}/{moved.Id}/' "
            + "RETURN m.state AS state, m.owner_module_id AS owner, m.actor AS actor")).Single();
        ((string)audit["state"], (string)audit["owner"], (string)audit["actor"]).Should().Be(("transferred", moved.Id, TransferActor));
        log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information && e.Message.Contains($"transfer completed", StringComparison.Ordinal)
            && e.Message.Contains($"the names of {old.Id} transferred by {TransferActor}", StringComparison.Ordinal));

        try
        {
            // The new pack imports: every record new under its id, the erased kata not written again, and the same walk as before.
            var imported = await importer.ImportAsync(moved.Knowledge!);
            (imported.Created, imported.Changed).Should().Be((count - 1, 0));
            (await LiveWalkAsync("Handover", "HANDOVER_")).Should().Equal(walk);
            (await Rows($"MATCH (n:HandoverKata {{key:'{erased}'}}) RETURN n")).Should().BeEmpty("the tombstone moved with the names");
            var kata = old.Knowledge.Proposals.Last(p => p.Type == "HandoverKata").Key;
            (await Rows($"MATCH (n:HandoverKata {{key:'{kata}'}}) RETURN n.invalidated_at IS NULL AS live, n.source_pack_transferred_from AS was ORDER BY live"))
                .Select(r => ((bool)r["live"], (string?)r["was"])).Should().Equal((false, old.Id), (true, null));

            // The old id can no longer take the names back, and holds nothing to remove.
            (await FluentActions.Awaiting(() => importer.ImportAsync(old.Knowledge)).Should().ThrowAsync<InvalidOperationException>())
                .Which.Message.Should().Contain($"Namespace is owned by another module ({moved.Id})");
            (await importer.RemoveByIdAsync(old.Id)).Closed.Should().Be(0);
            (await importer.RemoveAsync(old.Manifest)).Closed.Should().Be(0);
        }
        finally { await importer.RemoveAsync(moved.Manifest); }
    }

    [Fact]
    public async Task APackNameTransferIsRefusedWithNothingChangedUnlessTheOldIdIsRemovedAndTheNewPackOwnsEveryNameAsItWas()
    {
        var old = RenamedMartialArts("io.agentmemory.packs.relay", "Relay", "RELAY");
        var moved = RenamedMartialArts("io.example.packs.relay", "Relay", "RELAY");
        var importer = new KnowledgePackImporter(new Neo4jKnowledgePackStore(fixture.TransactionRunner));
        await importer.ImportAsync(old.Knowledge!);
        async Task<(IReadOnlyList<string> Ledger, IReadOnlyList<string> History)> StateAsync() =>
            (await LedgerAsync(old.Id, moved.Id), await HistoryWalkAsync("Relay"));
        async Task RefusedAsync(KnowledgePack to, string step, string problem)
        {
            var before = await StateAsync();
            var refused = await FluentActions.Awaiting(() => importer.TransferAsync(old.Id, to, TransferActor)).Should().ThrowAsync<KnowledgePackException>();
            PackFailure.Of(refused.Which).Should().BeEquivalentTo(new { Code = $"pack.transfer.{step}.refused", PackId = to.Id, Operation = PackOperations.Transfer });
            refused.Which.Problems.Should().Contain(p => p.Contains(problem, StringComparison.Ordinal));
            var after = await StateAsync();
            after.Ledger.Should().Equal(before.Ledger, "a refused transfer changes no name"); after.History.Should().Equal(before.History, "and no record");
            (await Rows("MATCH (n) WHERE any(l IN labels(n) WHERE l STARTS WITH 'Relay') AND n.source_pack_transferred_from IS NOT NULL RETURN n")).Should().BeEmpty();
        }

        try
        {
            // The old id still has live records: it is not removed.
            await RefusedAsync(moved.Knowledge!, PackSteps.Validate, $"{old.Id} still has 3 live RelayArt records: remove it first.");
            await importer.RemoveAsync(old.Manifest);

            // The new pack does not declare a name the old id owns: here, the full-text index of a search view it dropped.
            var narrower = RenamedMartialArts(moved.Id, "Relay", "RELAY", manifest =>
            {
                var views = manifest["views"]!.AsArray();
                views.Remove(views.Single(v => (string?)v!["id"] == "kata"));
            });
            await RefusedAsync(narrower.Knowledge!, PackSteps.Validate, $"{moved.Id} does not declare relay_kata_kata_search: it must declare every name {old.Id} owns.");

            // The new pack declares the old names as they were, and one more whose label this store already holds with no owner: the
            // records and the names move in the transaction, the new pack's own plan finds the stray label, and all of it is undone.
            var wider = RenamedMartialArts(moved.Id, "Relay", "RELAY", manifest => manifest["ontology"]!["types"]!.AsArray().Add(JsonNode.Parse(
                """{"name":"RelayStray","scope":"shared","render":"{name}","properties":{"name":{"type":"string","required":true}}}""")));
            await Execute("CREATE (:RelayStray {name:'left by hand'})");
            await RefusedAsync(wider.Knowledge!, PackSteps.Records, "Existing namespace has no matching module ownership: RelayStray");
            await Execute("MATCH (n:RelayStray) DELETE n");

            // Nothing was half done: the transfer of the same pack goes through, and it imports.
            (await importer.TransferAsync(old.Id, moved.Knowledge!, TransferActor)).Records.Should().Be(old.Knowledge!.Proposals.Count);
            (await importer.ImportAsync(moved.Knowledge!)).Created.Should().Be(old.Knowledge.Proposals.Count);
        }
        finally
        {
            await importer.RemoveAsync(moved.Manifest);
            await importer.RemoveAsync(old.Manifest);
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public sealed record Entry(LogLevel Level, string Message);
        public List<Entry> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add(new Entry(logLevel, formatter(state, exception)));
    }
}
