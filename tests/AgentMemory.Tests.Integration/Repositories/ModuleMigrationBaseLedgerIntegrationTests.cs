#pragma warning disable AMEXT001
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j.Migrations;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Schema.Extensions;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.Driver;

namespace AgentMemory.Tests.Integration.Repositories;

[Trait("Category", "Integration")]
public sealed class ModuleMigrationBaseLedgerIntegrationTests(Neo4jIntegrationFixture fixture)
    : IClassFixture<Neo4jIntegrationFixture>
{
    [Fact]
    public async Task ActualBaseAndExtensionMigrationRowsStayUnchangedAfterModuleApplication()
    {
        var registry = SchemaExtensionRegistry.CreateDefault();
        var active = registry.Active(["working-memory", "arithmetic"]);
        var builtIn = new MigrationRunner(fixture.TransactionRunner, NullLogger<MigrationRunner>.Instance,
            Path.Combine(AppContext.BaseDirectory, "Schema/Migrations"), active);
        await builtIn.RunMigrationsAsync();
        async Task<IReadOnlyList<Dictionary<string, object>>> Rows() =>
            await fixture.TransactionRunner.ReadAsync(async r =>
            {
                var rows = await (await r.RunAsync("MATCH (m:Migration) RETURN properties(m) AS row ORDER BY m.version")).ToListAsync();
                return (IReadOnlyList<Dictionary<string, object>>)rows.Select(r => global::Neo4j.Driver.ValueExtensions.As<Dictionary<string, object>>(r["row"])).ToArray();
            });
        var before = await Rows();
        before.Should().NotBeEmpty();
        before.Should().Contain(r => ((string)r["version"]).StartsWith("ext/working-memory/", StringComparison.Ordinal));
        before.Should().Contain(r => ((string)r["version"]).StartsWith("ext/arithmetic/", StringComparison.Ordinal));
        var resolution = ModuleResolver.Resolve([new ModuleManifest
        {
            Format = "agentmemory.module/v1alpha1", Id = "com.example.baseledger",
            Version = ModuleVersion.Parse("1.0.0"), Sdk = ModuleVersionRange.Parse("[0.1,0.2)"),
            Ontology = new ModuleOntology { LabelPrefix = "LedgerProof", Types = [new ModuleType { Name = "LedgerProofItem" }] }
        }], ModuleVersion.Parse("0.1.0"), new HashSet<string> { "ModuleSchema" });
        var migrator = new ModuleMigrator(fixture.TransactionRunner, resolution);
        (await migrator.ApplyAsync("ledger-proof")).Applied.Should().HaveCount(3);
        (await migrator.ApplyAsync("second-operator")).Pending.Should().BeEmpty();
        (await Rows()).Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }
}
