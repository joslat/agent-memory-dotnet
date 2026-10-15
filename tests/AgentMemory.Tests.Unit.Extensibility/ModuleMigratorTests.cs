using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j.Migrations;
using AgentMemory.Neo4j.Infrastructure;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

public sealed class ModuleMigratorTests
{
    [Fact]
    public async Task EmptyCompositionHasZeroDatabaseCallsForPlanAndApply()
    {
        var runner = Substitute.For<INeo4jTransactionRunner>();
        var resolution = ModuleResolver.Resolve([], ModuleVersion.Parse("0.1.0"), new HashSet<string>());
        var migrator = new ModuleMigrator(runner, resolution);
        (await migrator.PlanAsync()).Pending.Should().BeEmpty();
        (await migrator.ApplyAsync("unit-test")).Problems.Should().BeEmpty();
        runner.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task MissingActorIsRejectedBeforeDatabaseAccess()
    {
        var runner = Substitute.For<INeo4jTransactionRunner>();
        var resolution = ModuleResolver.Resolve([], ModuleVersion.Parse("0.1.0"), new HashSet<string>());
        var migrator = new ModuleMigrator(runner, resolution);
        var act = () => migrator.ApplyAsync(" ");
        await act.Should().ThrowAsync<ArgumentException>();
        runner.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task CancelledOperationDoesNotContactDatabase()
    {
        var runner = Substitute.For<INeo4jTransactionRunner>();
        var resolution = ModuleResolver.Resolve([], ModuleVersion.Parse("0.1.0"), new HashSet<string>());
        var migrator = new ModuleMigrator(runner, resolution);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var act = () => migrator.PlanAsync(cancellation.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        runner.ReceivedCalls().Should().BeEmpty();
    }
    [Fact]
    public void BootstrapReadinessListMatchesTheActualBackendBootstrap()
    {
        var backend = typeof(INeo4jTransactionRunner).Assembly;
        var queries = backend.GetType("AgentMemory.Neo4j.Queries.SchemaQueries")!;
        var statements = (string[])queries.GetField("Constraints", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        var names = statements.Select(sql => System.Text.RegularExpressions.Regex.Match(sql, @"CREATE CONSTRAINT (\w+)").Groups[1].Value);
        ModuleMigrationAssessment.BaseConstraints.Should().BeEquivalentTo(names);
    }

    [Fact]
    public void BookkeepingIdentityCannotBeUsedAsAnApplicationModule()
    {
        var runner = Substitute.For<INeo4jTransactionRunner>();
        var resolution = ModuleResolver.Resolve([new ModuleManifest
        {
            Format = "agentmemory.module/v1alpha1", Id = "io.agentmemory.sdk",
            Version = ModuleVersion.Parse("1.0.0"), Sdk = ModuleVersionRange.Parse("[0.1,0.2)")
        }], ModuleVersion.Parse("0.1.0"), new HashSet<string>());
        resolution.Succeeded.Should().BeTrue("publisher authorization is separate from metadata syntax");
        var act = () => new ModuleMigrator(runner, resolution);
        act.Should().Throw<ArgumentException>().WithMessage("*bookkeeping*");
        runner.ReceivedCalls().Should().BeEmpty();
    }

}
