using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Schema.Extensions;
using AgentMemory.Neo4j.Schema.Parity;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Schema.Extensions;

public sealed class ModuleBookkeepingOwnershipTests
{
    [Fact]
    public void ModuleLedgerHasARegisteredOwnerWithoutCoreMigrationScripts()
    {
        var extension = SchemaExtensionRegistry.CreateDefault().Find("module-bookkeeping");
        extension.Should().NotBeNull();
        extension!.DeclaredLabels.Should().Equal("ModuleMigration");
        extension.DeclaredProperties["ModuleMigration"].Should().Contain(["module_id", "key", "sha256", "state", "actor"]);
        extension.MigrationScripts.Should().BeEmpty("module apply has its own ledger and explicit entrypoint");
        extension.BaseResidentMigrations.Should().BeEmpty();
    }

    [Fact]
    public void DefaultSchemaRemainsTheSameObjectAndDoesNotIncludeTheLedger()
    {
        var baseline = DotNetSchema.Describe();
        var active = SchemaExtensionRegistry.CreateDefault().Active(new Neo4jOptions());
        active.Should().BeEmpty();
        EffectiveSchema.Describe(baseline, active).Should().BeSameAs(baseline);
        baseline.NodeLabels.Should().NotContain("ModuleMigration");
    }

    [Fact]
    public void ExplicitSchemaReportingNamesTheLedgerOwner()
    {
        var registry = SchemaExtensionRegistry.CreateDefault();
        var active = registry.Active(["module-bookkeeping"]);
        EffectiveSchema.Describe(active).NodeLabels.Should().Contain("ModuleMigration");
        var report = SchemaOwnersReport.Build(registry, ["module-bookkeeping"], new Dictionary<string, string?>());
        report.HasOwners.Should().BeTrue();
        report.Shapes.Should().Contain(shape => shape.Kind == "label" && shape.Name == "ModuleMigration"
            && shape.Owner == "module-bookkeeping" && shape.IsActive);
    }
}
