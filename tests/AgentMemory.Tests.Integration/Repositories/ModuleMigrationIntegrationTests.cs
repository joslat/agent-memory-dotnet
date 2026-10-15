#pragma warning disable AMEXT001
using System.Collections.Concurrent;
using System.Diagnostics;
using AgentMemory.Abstractions.Diagnostics;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j.Migrations;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Neo4j.Driver;

namespace AgentMemory.Tests.Integration.Repositories;

// Dedicated fixture/container: module ownership fixtures never share the existing experiment store.
[Trait("Category", "Integration")]
public sealed class ModuleMigrationIntegrationTests(Neo4jIntegrationFixture fixture)
    : IClassFixture<Neo4jIntegrationFixture>
{
    private static ModuleResolution Resolve(string prefix, string id = "sample", params string[] extra) =>
        ModuleResolver.Resolve([new ModuleManifest
        {
            Format = "agentmemory.module/v1alpha1", Id = "com.example." + id,
            Version = ModuleVersion.Parse("1.0.0"), Sdk = ModuleVersionRange.Parse("[0.1,0.2)"),
            Ontology = new ModuleOntology
            {
                LabelPrefix = prefix,
                Types = new[] { prefix + "Item" }.Concat(extra).Select(n => new ModuleType { Name = n }).ToArray()
            }
        }], ModuleVersion.Parse("0.1.0"), new HashSet<string> { "ModuleSchema" });

    private async Task<List<IRecord>> Read(string query) => await fixture.TransactionRunner.ReadAsync(async r =>
        await (await r.RunAsync(query)).ToListAsync());

    private Task Write(string query) => fixture.TransactionRunner.WriteAsync(async r =>
        { await (await r.RunAsync(query)).ConsumeAsync(); });

    [Fact]
    public async Task BootstrapOnlyPlanIsReadOnlyApplyIsRepeatableAndRollsForward()
    {
        var before = await Read("MATCH (m:Migration) RETURN properties(m) AS row ORDER BY m.version");
        var m = new ModuleMigrator(fixture.TransactionRunner, Resolve("MigFirst", "first"));
        var plan = await m.PlanAsync();
        plan.Problems.Should().BeEmpty();
        plan.Pending.Should().HaveCount(3);
        (await Read("MATCH (m:ModuleMigration {module_id:'com.example.first'}) RETURN m")).Should().BeEmpty();
        (await m.ApplyAsync("integration-first")).Pending.Should().BeEmpty();
        var applied = await m.PlanAsync();
        applied.Applied.Should().HaveCount(3);
        applied.Problems.Should().BeEmpty();
        var ledger = await Read("MATCH (m:ModuleMigration {module_id:'com.example.first'}) RETURN properties(m) AS row ORDER BY m.key");
        (await m.ApplyAsync("second-operator")).Applied.Should().HaveCount(3);
        (await Read("MATCH (m:ModuleMigration {module_id:'com.example.first'}) RETURN properties(m) AS row ORDER BY m.key"))
            .Select(r => r["row"]).Should().BeEquivalentTo(ledger.Select(r => r["row"]));
        var upgrade = new ModuleMigrator(fixture.TransactionRunner, Resolve("MigFirst", "first", "MigFirstArchive"));
        (await upgrade.PlanAsync()).Pending.Should().HaveCount(3);
        (await upgrade.ApplyAsync("upgrade")).Applied.Should().HaveCount(6);
        (await Read("MATCH (m:Migration) RETURN properties(m) AS row ORDER BY m.version"))
            .Select(r => r["row"]).Should().BeEquivalentTo(before.Select(r => r["row"]));
    }

    [Fact]
    public async Task MigrationTelemetryAttributesRealStepsAndKeepsRepeatApplyReadOnly()
    {
        var resolution = ModuleResolver.Resolve(
            Resolve("MigTraceA", "tracea").Modules.Concat(Resolve("MigTraceB", "traceb").Modules).ToArray(),
            ModuleVersion.Parse("0.1.0"), new HashSet<string> { "ModuleSchema" });
        var migrator = new ModuleMigrator(fixture.TransactionRunner, resolution);
        using var root = new Activity("migration-trace-integration").Start();
        var stopped = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => ReferenceEquals(source, AgentMemoryDiagnostics.Source),
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => options.Parent.TraceId == root.TraceId
                ? ActivitySamplingResult.AllDataAndRecorded : ActivitySamplingResult.None,
            ActivityStopped = activity => { if (activity.TraceId == root.TraceId) stopped.Add(activity); }
        };
        ActivitySource.AddActivityListener(listener);
        var plan = await migrator.PlanAsync();
        plan.Problems.Should().BeEmpty(); plan.Pending.Should().HaveCount(6);
        stopped.Where(a => a.OperationName == "memory.db.tx").Should().NotBeEmpty()
            .And.OnlyContain(a => Equals(a.GetTagItem("db.mode"), "read"));
        stopped.Clear();
        var result = await migrator.ApplyAsync("private-trace-actor");
        result.Applied.Should().HaveCount(6); result.Pending.Should().BeEmpty();
        var apply = stopped.Single(a => Equals(a.GetTagItem("memory.module.operation"), "apply"));
        apply.GetTagItem("memory.module.outcome").Should().Be("Completed");
        apply.GetTagItem("memory.module.id").Should().BeNull("shared ledger work is not owned by the first module");
        var steps = stopped.Where(a => Equals(a.GetTagItem("memory.module.operation"), "step")).ToArray();
        steps.Should().HaveCount(6).And.OnlyContain(a => a.ParentSpanId == apply.SpanId);
        foreach (var module in resolution.Modules)
        {
            var owned = steps.Where(a => Equals(a.GetTagItem("memory.module.id"), module.Id)).ToArray();
            owned.Should().HaveCount(3).And.OnlyContain(a => Equals(a.GetTagItem("memory.module.version"), "1.0.0")
                && Equals(a.GetTagItem("memory.module.outcome"), "Completed"));
            foreach (var step in owned)
                stopped.Should().Contain(a => a.OperationName == "memory.db.tx" && a.ParentSpanId == step.SpanId);
        }
        stopped.Should().Contain(a => a.OperationName == "memory.db.tx" && a.ParentSpanId == apply.SpanId
            && Equals(a.GetTagItem("db.mode"), "write"), "shared bookkeeping belongs to the set-level apply");
        string.Join("|", stopped.SelectMany(a => a.TagObjects.Select(t => t.Value?.ToString())
            .Concat(a.Events.SelectMany(e => e.Tags.Select(t => t.Value?.ToString()))))).Should().NotContain("private-trace-actor");
        const string ledgerQuery = "MATCH (m:ModuleMigration) RETURN properties(m) AS row ORDER BY m.module_id, m.key";
        var before = (await Read(ledgerQuery)).Select(r => r["row"]).ToArray();
        stopped.Clear();
        (await migrator.ApplyAsync("private-repeat-actor")).Applied.Should().HaveCount(6);
        stopped.Where(a => a.OperationName == "memory.db.tx").Should().NotBeEmpty()
            .And.OnlyContain(a => Equals(a.GetTagItem("db.mode"), "read"));
        stopped.Should().NotContain(a => Equals(a.GetTagItem("memory.module.operation"), "step"));
        stopped.Single(a => Equals(a.GetTagItem("memory.module.operation"), "apply"))
            .GetTagItem("memory.module.outcome").Should().Be("NoOp");
        (await Read(ledgerQuery)).Select(r => r["row"]).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task ExistingUnownedLabelIsRefusedWithoutTakingOwnership()
    {
        await Write("CREATE (:MigForeignItem {id:'outside'})");
        var m = new ModuleMigrator(fixture.TransactionRunner, Resolve("MigForeign", "foreign"));
        (await m.PlanAsync()).Problems.Should().Contain(p => p.Contains("MigForeignItem"));
        var act = () => m.ApplyAsync("test");
        await act.Should().ThrowAsync<InvalidOperationException>();
        (await Read("MATCH (m:ModuleMigration {module_id:'com.example.foreign'}) RETURN m")).Should().BeEmpty();
    }

    [Fact]
    public async Task ExistingForeignIndexNameIsRefusedEvenWithNoData()
    {
        await Write("CREATE INDEX mig_squat_item_owner_id_idx FOR (n:UnrelatedShape) ON (n.x)");
        var m = new ModuleMigrator(fixture.TransactionRunner, Resolve("MigSquat", "squat"));
        (await m.PlanAsync()).Problems.Should().NotBeEmpty();
        var act = () => m.ApplyAsync("test");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AppliedHashMutationIsRefused()
    {
        var m = new ModuleMigrator(fixture.TransactionRunner, Resolve("MigHash", "hash"));
        await m.ApplyAsync("test");
        await Write("MATCH (m:ModuleMigration {module_id:'com.example.hash'}) SET m.sha256='changed'");
        (await m.PlanAsync()).Problems.Should().Contain(p => p.Contains("hash", StringComparison.OrdinalIgnoreCase));
        var act = () => m.ApplyAsync("test");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CrashAfterDdlKeepsIntentAndMatchingRetryResumes()
    {
        var resolution = Resolve("MigCrash", "crash");
        var fault = new AfterDdlFailure(fixture.TransactionRunner, "mig_crash_item_id");
        var act = () => new ModuleMigrator(fault, resolution).ApplyAsync("first");
        await act.Should().ThrowAsync<IOException>();
        (await Read("MATCH (m:ModuleMigration {module_id:'com.example.crash', state:'applying'}) RETURN m")).Should().NotBeEmpty();
        var m = new ModuleMigrator(fixture.TransactionRunner, resolution);
        (await m.PlanAsync()).Problems.Should().BeEmpty();
        (await m.ApplyAsync("recovery")).Applied.Should().HaveCount(3);
    }

    [Fact]
    public async Task MismatchedInterruptedIntentIsRefused()
    {
        var resolution = Resolve("MigBadRetry", "badretry");
        var fault = new AfterDdlFailure(fixture.TransactionRunner, "mig_bad_retry_item_id");
        var act = () => new ModuleMigrator(fault, resolution).ApplyAsync("first");
        await act.Should().ThrowAsync<IOException>();
        await Write("MATCH (m:ModuleMigration {module_id:'com.example.badretry'}) SET m.sha256='changed'");
        var retry = () => new ModuleMigrator(fixture.TransactionRunner, resolution).ApplyAsync("retry");
        await retry.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ConcurrentModulesCannotBothClaimTheSameNamespace()
    {
        var a = new ModuleMigrator(fixture.TransactionRunner, Resolve("MigRace", "racea"));
        var b = new ModuleMigrator(fixture.TransactionRunner, Resolve("MigRace", "raceb"));
        static async Task<bool> TryApply(ModuleMigrator m)
        {
            try { await m.ApplyAsync("race"); return true; }
            catch (InvalidOperationException) { return false; }
        }
        var results = await Task.WhenAll(TryApply(a), TryApply(b));
        results.Count(success => success).Should().Be(1);
        var owners = await Read("MATCH (m:ModuleMigration {key:'namespace/label/MigRaceItem'}) RETURN m.owner_module_id AS owner");
        owners.Should().HaveCount(1);
    }

    [Fact]
    public async Task RelationOnlyUpgradeReservesOwnershipWithoutNewDdl()
    {
        var initial = Resolve("MigRelation", "relation");
        await new ModuleMigrator(fixture.TransactionRunner, initial).ApplyAsync("initial");
        var manifest = initial.Modules[0];
        var changed = manifest with { Ontology = manifest.Ontology! with
        { Relations = [new ModuleRelation { Name = "MIG_RELATION_LINK", From = "MigRelationItem", To = "MigRelationItem" }] } };
        var upgraded = ModuleResolver.Resolve([changed], ModuleVersion.Parse("0.1.0"), new HashSet<string> { "ModuleSchema" });
        upgraded.Succeeded.Should().BeTrue(string.Join(";", upgraded.Errors));
        var m = new ModuleMigrator(fixture.TransactionRunner, upgraded);
        (await m.ApplyAsync("relation-upgrade")).Pending.Should().BeEmpty();
        (await Read("MATCH (m:ModuleMigration {key:'namespace/relation/MIG_RELATION_LINK'}) RETURN m.owner_module_id AS owner"))
            .Should().ContainSingle().Which["owner"].Should().Be("com.example.relation");
    }

    [Fact]
    public async Task MissingBaseBootstrapRefusesApplication()
    {
        await Write("DROP CONSTRAINT memory_read_audit_id");
        try
        {
            var m = new ModuleMigrator(fixture.TransactionRunner, Resolve("MigNoBase", "nobase"));
            (await m.PlanAsync()).Problems.Should().Contain(p => p.Contains("Bootstrap"));
            var act = () => m.ApplyAsync("test");
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
        finally { await Write("CREATE CONSTRAINT memory_read_audit_id IF NOT EXISTS FOR (a:MemoryReadAudit) REQUIRE a.id IS UNIQUE"); }
    }

    [Fact]
    public async Task OmittedOldKeysAreReportedAndRetained()
    {
        var full = new ModuleMigrator(fixture.TransactionRunner, Resolve("MigOld", "old", "MigOldArchive"));
        await full.ApplyAsync("first");
        var reduced = new ModuleMigrator(fixture.TransactionRunner, Resolve("MigOld", "old"));
        var result = await reduced.ApplyAsync("reduced");
        result.Problems.Should().BeEmpty();
        result.UndeclaredAppliedKeys.Should().HaveCount(3);
        (await Read("MATCH (m:ModuleMigration {module_id:'com.example.old',state:'applied'}) RETURN m")).Should().HaveCount(6);
    }

    [Fact]
    public async Task SamePrefixCannotBeClaimedByASecondModuleWithDifferentLabels()
    {
        var original = Resolve("MigPrefix", "prefixa");
        await new ModuleMigrator(fixture.TransactionRunner, original).ApplyAsync("first");
        var other = original.Modules[0] with
        {
            Id = "com.example.prefixb", Ontology = new ModuleOntology
            { LabelPrefix = "MigPrefix", Types = [new ModuleType { Name = "MigPrefixOther" }] }
        };
        var resolution = ModuleResolver.Resolve([other], ModuleVersion.Parse("0.1.0"), new HashSet<string> { "ModuleSchema" });
        var m = new ModuleMigrator(fixture.TransactionRunner, resolution);
        (await m.PlanAsync()).Problems.Should().Contain(p => p.Contains("MigPrefix"));
        var act = () => m.ApplyAsync("second");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AppliedIndexReplacedWithDifferentPropertiesIsRefused()
    {
        var m = new ModuleMigrator(fixture.TransactionRunner, Resolve("MigTamper", "tamper"));
        await m.ApplyAsync("first");
        await Write("DROP INDEX mig_tamper_item_owner_id_idx");
        await Write("CREATE INDEX mig_tamper_item_owner_id_idx FOR (n:MigTamperItem) ON (n.wrong)");
        (await m.PlanAsync()).Problems.Should().Contain(p => p.Contains("differs from declared shape"));
        var act = () => m.ApplyAsync("repair");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private sealed class AfterDdlFailure(INeo4jTransactionRunner inner, string constraint) : INeo4jTransactionRunner
    {
        private bool _failed;
        public Task<T> ReadAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default) => inner.ReadAsync(work, cancellationToken);
        public Task ReadAsync(Func<IAsyncQueryRunner, Task> work, CancellationToken cancellationToken = default) => inner.ReadAsync(work, cancellationToken);
        public Task<T> WriteAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default) => inner.WriteAsync(work, cancellationToken);
        public async Task WriteAsync(Func<IAsyncQueryRunner, Task> work, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(work, cancellationToken);
            if (!_failed && await inner.ReadAsync(async r =>
                (await (await r.RunAsync("SHOW CONSTRAINTS YIELD name WHERE name = $name RETURN name", new { name = constraint })).ToListAsync()).Count > 0, cancellationToken))
            { _failed = true; throw new IOException("Simulated crash after committed DDL, before the applied ledger stamp."); }
        }
    }
}
