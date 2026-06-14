using FluentAssertions;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Queries;

namespace AgentMemory.Tests.Unit.Infrastructure;

/// <summary>
/// cycle/CLI: the pure helpers behind the <c>schema-check</c> command — name extraction from the bootstrap
/// DDL and the expected-vs-existing diff.
/// </summary>
public sealed class SchemaConformanceTests
{
    [Theory]
    [InlineData("CREATE CONSTRAINT conversation_id IF NOT EXISTS FOR (c:Conversation) REQUIRE c.id IS UNIQUE", "conversation_id")]
    [InlineData("CREATE INDEX message_timestamp_idx IF NOT EXISTS FOR (m:Message) ON (m.timestamp)", "message_timestamp_idx")]
    [InlineData("CREATE FULLTEXT INDEX message_content IF NOT EXISTS FOR (m:Message) ON EACH [m.content]", "message_content")]
    [InlineData("CREATE POINT INDEX entity_location_idx IF NOT EXISTS FOR (e:Entity) ON (e.location)", "entity_location_idx")]
    [InlineData("CREATE VECTOR INDEX message_embedding_idx IF NOT EXISTS FOR (n:Message) ON (n.embedding) OPTIONS {indexConfig: {}}", "message_embedding_idx")]
    [InlineData("CREATE INDEX rel_owner_idx IF NOT EXISTS FOR ()-[r:RELATED_TO]-() ON (r.owner_id)", "rel_owner_idx")]
    public void ParseObjectName_ExtractsTheNameForEveryDdlKind(string ddl, string expected)
    {
        SchemaConformance.ParseObjectName(ddl).Should().Be(expected);
    }

    [Fact]
    public void ParseObjectName_ParsesEveryActualBootstrapStatement()
    {
        // Guard: every DDL the bootstrap runs must yield a non-empty name (so the check can compare it).
        var all = SchemaQueries.Constraints
            .Concat(SchemaQueries.FulltextIndexes)
            .Concat(SchemaQueries.BuildVectorIndexes(1536))
            .Concat(SchemaQueries.PropertyIndexes);

        foreach (var ddl in all)
            SchemaConformance.ParseObjectName(ddl).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ExpectedObjectNames_CoversAllConstraintAndIndexKinds_WithNoDuplicates()
    {
        var names = SchemaConformance.ExpectedObjectNames(1536);

        names.Should().HaveCount(
            SchemaQueries.Constraints.Length + SchemaQueries.FulltextIndexes.Length + 6 + SchemaQueries.PropertyIndexes.Length);
        names.Should().OnlyHaveUniqueItems();
        names.Should().Contain(new[]
        {
            "conversation_id",          // constraint
            "message_content",          // fulltext
            "message_embedding_idx",    // vector
            "entity_location_idx",      // point
            "fact_owner_idx",           // R1 owner index
        });
    }

    [Fact]
    public void MissingObjects_ReturnsExpectedNotPresent_CaseSensitive_PreservingOrder()
    {
        var expected = new[] { "a", "b", "c", "d" };
        var existing = new[] { "b", "D" }; // note: "D" != "d" (Neo4j names are case-sensitive)

        SchemaConformance.MissingObjects(expected, existing)
            .Should().ContainInOrder("a", "c", "d").And.NotContain("b");
    }

    [Fact]
    public void MissingObjects_AllPresent_ReturnsEmpty()
    {
        var expected = SchemaConformance.ExpectedObjectNames(1536);
        SchemaConformance.MissingObjects(expected, expected.ToList()).Should().BeEmpty();
    }
}
