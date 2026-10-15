using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j.Migrations;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

public sealed class ModuleMigrationOperationTests
{
    private static ModuleManifest Module() => new()
    {
        Format = "agentmemory.module/v1alpha1", Id = "com.example.reviews",
        Version = ModuleVersion.Parse("1.0.0"), Sdk = ModuleVersionRange.Parse("[0.1,0.2)"),
        Ontology = new ModuleOntology { LabelPrefix = "Review" }
    };

    [Fact]
    public void SchemaCompilationChecksOwnershipAndPreservesCompositeShape()
    {
        var result = ModuleMigrationOperations.Compile(Module(), "CREATE RANGE INDEX due IF NOT EXISTS FOR (n:ReviewItem) ON (n.owner_id, n.due)");
        result.Shape!.Properties.Should().Equal("owner_id", "due");
        result.Labels.Should().Equal("ReviewItem");
        result.RelationshipTypes.Should().BeEmpty();
        result.IsSchema.Should().BeTrue();
    }

    [Fact]
    public void RelationshipSchemaClaimsTheType()
    {
        var result = ModuleMigrationOperations.Compile(Module(), "CREATE INDEX due IF NOT EXISTS FOR ()-[r:REVIEW_LINK]-() ON (r.due)");
        result.RelationshipTypes.Should().Equal("REVIEW_LINK");
        result.Labels.Should().BeEmpty();
    }

    [Theory]
    [InlineData("MATCH (n:ReviewItem) WHERE n.note IS NULL SET n.note = 'https://example.org/a;b'")]
    [InlineData("MATCH (n:ReviewItem) SET n.count = coalesce(n.count, 0) + 1")]
    [InlineData("MATCH (a:ReviewItem)-[r:REVIEW_LINK]->(b:ReviewArchive) WHERE a.owner_id = b.owner_id AND r.due IS NOT NULL SET r.due = date('2026-10-14'), b.note = a.note")]
    [InlineData("MATCH (a:ReviewItem), (b:ReviewArchive) WHERE a.kind IN ['x', 'y'] AND NOT (b.count < 0) SET a.count = -2, b.count = 1.25")]
    [InlineData("/* DELETE ignored */ MATCH (`match`:`ReviewItem`) WHERE `match`.`delete` = 'CALL evil()' SET `match`.`delete` = 'DROP INDEX index;'")]
    public void ScopedBackfillsCompileWithoutPretendingTheyAreSchema(string source)
    {
        var result = ModuleMigrationOperations.Compile(Module(), source);
        result.IsSchema.Should().BeFalse();
        result.Shape.Should().BeNull();
        result.Labels.Should().Contain("ReviewItem");
        result.Text.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("CREATE INDEX due IF NOT EXISTS FOR (n:Fact) ON (n.content)")]
    [InlineData("CREATE INDEX fact_id IF NOT EXISTS FOR (n:ReviewItem) ON (n.note)")]
    [InlineData("CREATE INDEX due IF NOT EXISTS FOR ()-[r:HAS_FACT]-() ON (r.note)")]
    [InlineData("MATCH (n:Fact) SET n.content = 'owned?'")]
    [InlineData("MATCH (n) SET n.note = 'unscoped'")]
    [InlineData("MATCH (n:ReviewItem), (other:Fact) SET other.content = n.note")]
    [InlineData("MATCH (n:ReviewItem:Fact) SET n.note = 'mixed labels'")]
    [InlineData("MATCH (n:ReviewItem) SET other.note = 'unbound'")]
    [InlineData("MATCH (n:ReviewItem) SET n.note = other.content")]
    [InlineData("MATCH (n:ReviewItem) SET n.note = apoc.cypher.run('MATCH (f:Fact) RETURN f', {})")]
    [InlineData("MATCH (n:ReviewItem) CALL { MATCH (f:Fact) SET f.content = n.note }")]
    [InlineData("MATCH (n:ReviewItem) SET n.note = COLLECT { MATCH (f:Fact) RETURN f.content }")]
    [InlineData("MATCH (n:ReviewItem) WHERE EXISTS { MATCH (f:Fact) } SET n.note = 'x'")]
    [InlineData("MATCH (n:ReviewItem) WITH n AS other MATCH (f:Fact) SET f.content = 'x'")]
    [InlineData("MATCH (n:ReviewItem) SET n.note = 'x' UNION MATCH (f:Fact) SET f.content = 'x'")]
    [InlineData("MATCH (n:ReviewItem) SET n[$key] = 'x'")]
    [InlineData("MATCH (n:ReviewItem) SET n = {note:'x'}")]
    [InlineData("MATCH (n:ReviewItem) SET n:Fact")]
    [InlineData("MATCH (n:$(label)) SET n.note = 'x'")]
    [InlineData("MATCH (n:ReviewItem) REMOVE n.note")]
    [InlineData("MATCH (n:ReviewItem) DETACH DELETE n")]
    [InlineData("MATCH (n:ReviewItem) SET n.note = 'x'; MATCH (f:Fact) SET f.content = 'x'")]
    [InlineData("MATCH (a:ReviewItem)-[r]->(b:ReviewItem) SET r.note = 'x'")]
    [InlineData("MATCH (a:ReviewItem)-[r:OTHER_LINK]->(b:ReviewItem) SET r.note = 'x'")]
    [InlineData("MATCH (a:ReviewItem)-[r:REVIEW_LINK]->(b:Entity) SET r.note = 'x'")]
    [InlineData("MATCH (a:ReviewItem)-[r:REVIEW_LINK*]->(b:ReviewItem) SET a.note = 'x'")]
    [InlineData("MATCH (n:ReviewItem) SET n.owner_id = 'other'")]
    [InlineData("MATCH (n:ReviewItem) SET n.`invalidated_at` = datetime()")]
    [InlineData("MATCH (n:ReviewItem) SET n.note = userFunction(n.note)")]
    [InlineData("MATCH (n:ReviewItem) SET n.note = [x IN [(f:Fact) | f.content] | x]")]
    public void UnsafeOrUnsupportedOperationsAreRejectedBeforeExecution(string source)
    {
        var action = () => ModuleMigrationOperations.Compile(Module(), source);
        action.Should().Throw<FormatException>();
    }
}
