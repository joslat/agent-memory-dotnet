#pragma warning disable AMEXT001
using System.Security.Cryptography;
using System.Text;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j.Migrations;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Neo4j.Driver;

namespace AgentMemory.Tests.Integration.Repositories;

[Trait("Category", "Integration")]
public sealed class ModuleHandwrittenMigrationIntegrationTests(Neo4jIntegrationFixture fixture)
    : IClassFixture<Neo4jIntegrationFixture>
{
    private static ModuleResolution Resolve(string prefix, string id) => ModuleResolver.Resolve([new ModuleManifest
    {
        Format = "agentmemory.module/v1alpha1", Id = "com.example." + id,
        Version = ModuleVersion.Parse("1.0.0"), Sdk = ModuleVersionRange.Parse("[0.1,0.2)"),
        Ontology = new ModuleOntology { LabelPrefix = prefix, Types = [new() { Name = prefix + "Item" }] }
    }], ModuleVersion.Parse("0.1.0"), new HashSet<string> { "ModuleSchema" });

    private static ModuleMigrationStep Script(ModuleResolution resolution, string source) => new()
    {
        ModuleId = resolution.Modules[0].Id, Key = "0001_update", Statement = source,
        Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.Replace("\r\n", "\n").Replace('\r', '\n')))).ToLowerInvariant()
    };

    private Task Write(string query) => fixture.TransactionRunner.WriteAsync(async r => { await (await r.RunAsync(query)).ConsumeAsync(); });
    private Task<List<IRecord>> Read(string query, object? args = null) => fixture.TransactionRunner.ReadAsync(async r =>
        await (await r.RunAsync(query, args)).ToListAsync());

    private async Task<ModuleResolution> Prepare(string prefix, string id)
    {
        var resolution = Resolve(prefix, id);
        await new ModuleMigrator(fixture.TransactionRunner, resolution).ApplyAsync("setup");
        await Write($"CREATE (n:{prefix}Item {{id:'one', owner_id:'fixture', count:0, source_date:'2026-10-14'}})");
        return resolution;
    }

    private static string Mixed(string prefix, string index) =>
        $"CREATE INDEX {index} IF NOT EXISTS FOR (n:{prefix}Item) ON (n.owner_id, n.next_review); " +
        $"MATCH (n:{prefix}Item) SET n.count = n.count + 1; " +
        $"MATCH (n:{prefix}Item) SET n.note = 'https://example.org/a;b'";

    private async Task<long> Count(string prefix) => (long)(await Read($"MATCH (n:{prefix}Item) RETURN n.count AS count")).Single()["count"];
    private async Task<Dictionary<string, object>> Ledger(ModuleResolution resolution) =>
        global::Neo4j.Driver.ValueExtensions.As<Dictionary<string, object>>((await Read("MATCH (m:ModuleMigration {module_id:$id,key:'0001_update'}) RETURN properties(m) AS row", new { id = resolution.Modules[0].Id })).Single()["row"]);

    [Fact]
    public async Task MixedScriptPlansReadOnlyAppliesOnceAndPreservesBaseLedger()
    {
        var resolution = await Prepare("HandFirst", "handfirst");
        var before = await Read("MATCH (m:Migration) RETURN properties(m) AS row ORDER BY m.version");
        var script = Script(resolution, Mixed("HandFirst", "hand_first_due"));
        var m = new ModuleMigrator(fixture.TransactionRunner, resolution, [script], null);
        (await m.PlanAsync()).Pending.Should().ContainSingle().Which.Key.Should().Be(script.Key);
        (await Read("MATCH (m:ModuleMigration {module_id:'com.example.handfirst',key:'0001_update'}) RETURN m")).Should().BeEmpty();
        (await m.ApplyAsync("first")).Problems.Should().BeEmpty();
        (await Count("HandFirst")).Should().Be(1);
        var row = await Ledger(resolution);
        ((long)row["completed_statements"]).Should().Be(3);
        row["state"].Should().Be("applied");
        (await m.ApplyAsync("second")).Pending.Should().BeEmpty();
        (await Ledger(resolution)).Should().BeEquivalentTo(row);
        (await Count("HandFirst")).Should().Be(1);
        global::Neo4j.Driver.ValueExtensions.As<List<string>>((await Read("SHOW INDEXES YIELD name, properties WHERE name='hand_first_due' RETURN properties"))
            .Single()["properties"]).Should().Equal("owner_id", "next_review");
        (await Read("MATCH (m:Migration) RETURN properties(m) AS row ORDER BY m.version"))
            .Select(r => r["row"]).Should().BeEquivalentTo(before.Select(r => r["row"]));
        (await new ModuleMigrator(fixture.TransactionRunner, resolution).PlanAsync()).UndeclaredAppliedKeys
            .Should().Contain("com.example.handfirst/0001_update");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task LostCommitAcknowledgementResumesWithoutReplayingCompletedStatements(int completed)
    {
        var prefix = "HandLost" + completed;
        var resolution = await Prepare(prefix, "handlost" + completed);
        var script = Script(resolution, Mixed(prefix, "hand_lost_due_" + completed));
        var fault = new ProgressFailure(fixture.TransactionRunner, resolution.Modules[0].Id, completed, beforeCommit: false,
            schemaName: completed == 0 ? "hand_lost_due_0" : null);
        var act = () => new ModuleMigrator(fault, resolution, [script], null).ApplyAsync("interrupted");
        await act.Should().ThrowAsync<IOException>();
        ((long)(await Ledger(resolution))["completed_statements"]).Should().Be(completed);
        var retry = new ModuleMigrator(fixture.TransactionRunner, resolution, [script], null);
        (await retry.ApplyAsync("recovery")).Problems.Should().BeEmpty();
        (await Count(prefix)).Should().Be(1);
        (await Ledger(resolution))["state"].Should().Be("applied");
    }

    [Fact]
    public async Task BackfillAndProgressRollBackTogetherOnFailureBeforeCommit()
    {
        var resolution = await Prepare("HandRollback", "handrollback");
        var script = Script(resolution, "MATCH (n:HandRollbackItem) SET n.count = n.count + 1");
        var fault = new ProgressFailure(fixture.TransactionRunner, resolution.Modules[0].Id, 1, beforeCommit: true);
        var act = () => new ModuleMigrator(fault, resolution, [script], null).ApplyAsync("interrupted");
        await act.Should().ThrowAsync<IOException>();
        (await Count("HandRollback")).Should().Be(0);
        ((long)(await Ledger(resolution))["completed_statements"]).Should().Be(0);
        await new ModuleMigrator(fixture.TransactionRunner, resolution, [script], null).ApplyAsync("recovery");
        (await Count("HandRollback")).Should().Be(1);
    }

    [Fact]
    public async Task ARealLaterStatementFailurePreservesEarlierBackfillProgress()
    {
        var resolution = await Prepare("HandInvalidDate", "handinvaliddate");
        await Write("MATCH (n:HandInvalidDateItem) SET n.source_date='not-a-date'");
        var script = Script(resolution, "MATCH (n:HandInvalidDateItem) SET n.count=n.count+1; MATCH (n:HandInvalidDateItem) SET n.due=date(n.source_date)");
        var migrator = new ModuleMigrator(fixture.TransactionRunner, resolution, [script], null);
        await FluentActions.Awaiting(() => migrator.ApplyAsync("bad-data")).Should().ThrowAsync<ClientException>();
        (await Count("HandInvalidDate")).Should().Be(1);
        ((long)(await Ledger(resolution))["completed_statements"]).Should().Be(1);
        await Write("MATCH (n:HandInvalidDateItem) SET n.source_date='2026-10-14'");
        await migrator.ApplyAsync("fixed-data");
        (await Count("HandInvalidDate")).Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentMatchingAppliesIncrementOnlyOnce()
    {
        var resolution = await Prepare("HandRace", "handrace");
        var script = Script(resolution, Mixed("HandRace", "hand_race_due"));
        var first = new ModuleMigrator(fixture.TransactionRunner, resolution, [script], null);
        var second = new ModuleMigrator(fixture.TransactionRunner, resolution, [script], null);
        var results = await Task.WhenAll(first.ApplyAsync("first"), second.ApplyAsync("second"));
        results.Should().OnlyContain(p => p.Problems.Count == 0 && p.Pending.Count == 0);
        (await Count("HandRace")).Should().Be(1);
    }

    [Fact]
    public async Task AModuleRuleCommittedByAConcurrentMatchingMigratorIsAccepted()
    {
        var resolution = await Prepare("HandTwin", "handtwin");
        var racing = new SchemaRace(fixture.TransactionRunner, resolution.Modules[0].Id,
            "CREATE INDEX hand_twin_due IF NOT EXISTS FOR (n:HandTwinItem) ON (n.owner_id, n.next_review)");
        var migrator = new ModuleMigrator(racing, resolution, [Script(resolution, Mixed("HandTwin", "hand_twin_due"))], null);
        (await migrator.ApplyAsync("slower")).Problems.Should().BeEmpty();
        racing.Raced.Should().BeTrue();
        (await Count("HandTwin")).Should().Be(1);
        var row = await Ledger(resolution);
        row["state"].Should().Be("applied");
        ((long)row["completed_statements"]).Should().Be(3);
    }

    [Theory]
    [InlineData("HandOther", "CREATE INDEX hand_other_elsewhere FOR (n:HandOtherItem) ON (n.owner_id, n.next_review)")]
    [InlineData("HandWrong", "CREATE INDEX hand_wrong_due FOR (n:HandWrongItem) ON (n.note)")]
    public async Task AConcurrentRuleThatIsNotTheDeclaredObjectKeepsTheRefusal(string prefix, string concurrent)
    {
        var resolution = await Prepare(prefix, prefix.ToLowerInvariant());
        var index = prefix == "HandOther" ? "hand_other_due" : "hand_wrong_due";
        var racing = new SchemaRace(fixture.TransactionRunner, resolution.Modules[0].Id, concurrent);
        var migrator = new ModuleMigrator(racing, resolution, [Script(resolution, Mixed(prefix, index))], null);
        (await FluentActions.Awaiting(() => migrator.ApplyAsync("slower")).Should().ThrowAsync<ClientException>())
            .Which.Code.Should().Be(SchemaRace.Equivalent);
        racing.Raced.Should().BeTrue();
        (await Count(prefix)).Should().Be(0);
        var row = await Ledger(resolution);
        row["state"].Should().Be("applying");
        ((long)row["completed_statements"]).Should().Be(0);
    }

    [Fact]
    public async Task ABookkeepingConstraintCommittedByAConcurrentMigratorIsAccepted()
    {
        var resolution = await Prepare("HandBook", "handbook");
        var racing = new FirstWriteRefused(fixture.TransactionRunner);
        var script = Script(resolution, "MATCH (n:HandBookItem) SET n.count = n.count + 1");
        (await new ModuleMigrator(racing, resolution, [script], null).ApplyAsync("slower")).Problems.Should().BeEmpty();
        racing.Refused.Should().BeTrue();
        (await Count("HandBook")).Should().Be(1);
    }

    [Fact]
    public async Task AChangedSourceOrMissingProgressCannotReplayAnInterruptedBackfill()
    {
        var resolution = await Prepare("HandTamper", "handtamper");
        var source = "MATCH (n:HandTamperItem) SET n.count=n.count+1; MATCH (n:HandTamperItem) SET n.note='ready'";
        var script = Script(resolution, source);
        var fault = new ProgressFailure(fixture.TransactionRunner, resolution.Modules[0].Id, 1, beforeCommit: false);
        await FluentActions.Awaiting(() => new ModuleMigrator(fault, resolution, [script], null).ApplyAsync("first"))
            .Should().ThrowAsync<IOException>();
        var changed = new ModuleMigrator(fixture.TransactionRunner, resolution, [Script(resolution, source + " // different")], null);
        await FluentActions.Awaiting(() => changed.ApplyAsync("changed")).Should().ThrowAsync<InvalidOperationException>();
        await Write("MATCH (m:ModuleMigration {module_id:'com.example.handtamper',key:'0001_update'}) REMOVE m.completed_statements");
        var same = new ModuleMigrator(fixture.TransactionRunner, resolution, [script], null);
        await FluentActions.Awaiting(() => same.ApplyAsync("missing-progress")).Should().ThrowAsync<InvalidOperationException>();
        (await Count("HandTamper")).Should().Be(1);
    }

    [Fact]
    public async Task CompositeAndRelationshipSchemaMatchActualNeo4jMetadata()
    {
        var resolution = await Prepare("HandShape", "handshape");
        var script = Script(resolution,
            "CREATE CONSTRAINT hand_shape_pair IF NOT EXISTS FOR (n:HandShapeItem) REQUIRE (n.owner_id,n.pair) IS UNIQUE; " +
            "CREATE CONSTRAINT hand_shape_link_id IF NOT EXISTS FOR ()-[r:HAND_SHAPE_LINK]-() REQUIRE r.id IS UNIQUE; " +
            "CREATE INDEX hand_shape_since IF NOT EXISTS FOR ()-[r:HAND_SHAPE_LINK]-() ON (r.since)");
        var migrator = new ModuleMigrator(fixture.TransactionRunner, resolution, [script], null);
        (await migrator.ApplyAsync("shape-check")).Problems.Should().BeEmpty();
        (await migrator.PlanAsync()).Pending.Should().BeEmpty();
        var constraints = await Read("SHOW CONSTRAINTS YIELD name,type,entityType,properties WHERE name IN ['hand_shape_pair','hand_shape_link_id'] RETURN name,type,entityType,properties");
        constraints.Should().HaveCount(2);
        constraints.Single(r => (string)r["name"] == "hand_shape_link_id")["type"].Should().Be("RELATIONSHIP_UNIQUENESS");
        global::Neo4j.Driver.ValueExtensions.As<List<string>>(constraints.Single(r => (string)r["name"] == "hand_shape_pair")["properties"])
            .Should().Equal("owner_id", "pair");
    }

    [Fact]
    public async Task EquivalentForeignIndexIsRefusedBeforeAHandwrittenIntentIsCreated()
    {
        var resolution = await Prepare("HandEquivalent", "handequivalent");
        await Write("CREATE INDEX hand_equivalent_other FOR (n:HandEquivalentItem) ON (n.due)");
        var script = Script(resolution, "CREATE INDEX hand_equivalent_wanted IF NOT EXISTS FOR (n:HandEquivalentItem) ON (n.due)");
        var migrator = new ModuleMigrator(fixture.TransactionRunner, resolution, [script], null);
        (await migrator.PlanAsync()).Problems.Should().Contain(p => p.Contains("equivalent"));
        await FluentActions.Awaiting(() => migrator.ApplyAsync("refused")).Should().ThrowAsync<InvalidOperationException>();
        (await Read("MATCH (m:ModuleMigration {module_id:'com.example.handequivalent',key:'0001_update'}) RETURN m")).Should().BeEmpty();
    }

    [Fact]
    public async Task PreviouslyAppliedGeneratedRowsWithoutProgressRemainCompatible()
    {
        var resolution = await Prepare("HandLegacy", "handlegacy");
        await Write("MATCH (m:ModuleMigration {module_id:'com.example.handlegacy',state:'applied'}) REMOVE m.completed_statements");
        var before = await Read("MATCH (m:ModuleMigration {module_id:'com.example.handlegacy'}) RETURN properties(m) AS row ORDER BY m.key");
        (await new ModuleMigrator(fixture.TransactionRunner, resolution).ApplyAsync("legacy-reader")).Problems.Should().BeEmpty();
        (await Read("MATCH (m:ModuleMigration {module_id:'com.example.handlegacy'}) RETURN properties(m) AS row ORDER BY m.key"))
            .Select(r => r["row"]).Should().BeEquivalentTo(before.Select(r => r["row"]));
    }

    // Two migrators can both pass IF NOT EXISTS; Neo4j then refuses the slower commit. This commits the faster
    // one's statement just before the migrator's own first DDL and returns that refusal, deterministically.
    private sealed class SchemaRace(INeo4jTransactionRunner inner, string module, string concurrentStatement) : INeo4jTransactionRunner
    {
        internal const string Equivalent = "Neo.ClientError.Schema.EquivalentSchemaRuleAlreadyExists";
        internal bool Raced { get; private set; }
        public Task<T> ReadAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default) => inner.ReadAsync(work, cancellationToken);
        public Task ReadAsync(Func<IAsyncQueryRunner, Task> work, CancellationToken cancellationToken = default) => inner.ReadAsync(work, cancellationToken);
        private async Task RaceAsync(CancellationToken cancellationToken)
        {
            if (Raced) return;
            // The first write after the intent is reserved, before any statement progress, is the schema statement.
            var reserved = await inner.ReadAsync(async r => (await (await r.RunAsync(
                "MATCH (m:ModuleMigration {module_id:$module,key:'0001_update',state:'applying',completed_statements:0}) RETURN m",
                new { module })).ToListAsync()).Count == 1, cancellationToken);
            if (!reserved) return;
            Raced = true;
            await inner.WriteAsync(async r => { await (await r.RunAsync(concurrentStatement)).ConsumeAsync(); }, cancellationToken);
            throw new ClientException(Equivalent, "An equivalent schema rule was committed by another transaction.");
        }
        public async Task<T> WriteAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default)
        {
            await RaceAsync(cancellationToken);
            return await inner.WriteAsync(work, cancellationToken);
        }
        public async Task WriteAsync(Func<IAsyncQueryRunner, Task> work, CancellationToken cancellationToken = default)
        {
            await RaceAsync(cancellationToken);
            await inner.WriteAsync(work, cancellationToken);
        }
    }

    // ApplyAsync's first write creates the shared ledger constraint, which a concurrent first-time migrator may
    // already have committed (it exists here from Prepare): refuse that one write as Neo4j would.
    private sealed class FirstWriteRefused(INeo4jTransactionRunner inner) : INeo4jTransactionRunner
    {
        internal bool Refused { get; private set; }
        public Task<T> ReadAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default) => inner.ReadAsync(work, cancellationToken);
        public Task ReadAsync(Func<IAsyncQueryRunner, Task> work, CancellationToken cancellationToken = default) => inner.ReadAsync(work, cancellationToken);
        private void Refuse()
        {
            if (Refused) return;
            Refused = true;
            throw new ClientException(SchemaRace.Equivalent, "An equivalent schema rule was committed by another transaction.");
        }
        public Task<T> WriteAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default)
        {
            Refuse();
            return inner.WriteAsync(work, cancellationToken);
        }
        public Task WriteAsync(Func<IAsyncQueryRunner, Task> work, CancellationToken cancellationToken = default)
        {
            Refuse();
            return inner.WriteAsync(work, cancellationToken);
        }
    }

    private sealed class ProgressFailure(INeo4jTransactionRunner inner, string module, int completed, bool beforeCommit, string? schemaName = null) : INeo4jTransactionRunner
    {
        private bool _failed;
        public Task<T> ReadAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default) => inner.ReadAsync(work, cancellationToken);
        public Task ReadAsync(Func<IAsyncQueryRunner, Task> work, CancellationToken cancellationToken = default) => inner.ReadAsync(work, cancellationToken);
        private async Task Inspect(IAsyncQueryRunner runner)
        {
            if (_failed) return;
            if (schemaName is not null && (await (await runner.RunAsync("SHOW INDEXES YIELD name WHERE name=$name RETURN name", new { name = schemaName })).ToListAsync()).Count == 0) return;
            var rows = await (await runner.RunAsync("MATCH (m:ModuleMigration {module_id:$module,key:'0001_update',state:'applying',completed_statements:$completed}) RETURN m", new { module, completed })).ToListAsync();
            if (rows.Count == 0) return;
            _failed = true;
            throw new IOException(beforeCommit ? "Failure before transaction commit" : "Commit acknowledgement lost");
        }
        public async Task<T> WriteAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default)
        {
            var result = await inner.WriteAsync(async r =>
            {
                var value = await work(r);
                if (beforeCommit) await Inspect(r);
                return value;
            }, cancellationToken);
            if (!beforeCommit) await inner.ReadAsync(Inspect, cancellationToken);
            return result;
        }
        public async Task WriteAsync(Func<IAsyncQueryRunner, Task> work, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(async r => { await work(r); if (beforeCommit) await Inspect(r); }, cancellationToken);
            if (!beforeCommit) await inner.ReadAsync(Inspect, cancellationToken);
        }
    }
}
