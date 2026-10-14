using AgentMemory.Extensibility.Neo4j.Migrations;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

public sealed class ModuleSchemaStatementTests
{
    [Fact]
    public void CompositeRangeIndexPreservesExactOrderedProperties()
    {
        var shape = ModuleSchemaStatement.Parse("CREATE INDEX review_due IF NOT EXISTS FOR (n:ReviewItem) ON (n.owner_id, n.next_review);");
        shape.Name.Should().Be("review_due");
        shape.Kind.Should().Be("RANGE");
        shape.EntityType.Should().Be("NODE");
        shape.Labels.Should().Equal("ReviewItem");
        shape.Properties.Should().Equal("owner_id", "next_review");
        shape.IsConstraint.Should().BeFalse();
    }

    [Theory]
    [InlineData("CREATE CONSTRAINT review_key IF NOT EXISTS FOR (n:ReviewItem) REQUIRE n.id IS UNIQUE", "NODE", "UNIQUENESS")]
    [InlineData("CREATE CONSTRAINT review_key IF NOT EXISTS FOR ()-[n:REVIEW_LINK]-() REQUIRE n.id IS UNIQUE", "RELATIONSHIP", "RELATIONSHIP_UNIQUENESS")]
    public void UniquenessRecordsTheDatabaseConstraintKind(string source, string entity, string kind)
    {
        var shape = ModuleSchemaStatement.Parse(source);
        shape.IsConstraint.Should().BeTrue();
        shape.EntityType.Should().Be(entity);
        shape.Kind.Should().Be(kind);
        shape.Properties.Should().Equal("id");
    }

    [Fact]
    public void CompositeUniquenessUsesEveryProperty()
    {
        ModuleSchemaStatement.Parse("CREATE CONSTRAINT review_key IF NOT EXISTS FOR (n:ReviewItem) REQUIRE (n.owner_id, n.key_id) IS UNIQUE")
            .Properties.Should().Equal("owner_id", "key_id");
    }

    [Fact]
    public void RelationshipRangeIndexDoesNotClaimAnEndpointLabel()
    {
        var shape = ModuleSchemaStatement.Parse("CREATE RANGE INDEX review_link IF NOT EXISTS FOR ()-[r:REVIEW_LINK]-() ON (r.since, r.owner_id)");
        shape.EntityType.Should().Be("RELATIONSHIP");
        shape.Labels.Should().Equal("REVIEW_LINK");
        shape.Properties.Should().Equal("since", "owner_id");
    }

    [Fact]
    public void CommentsAndQuotedKeywordsAreNotOperations()
    {
        var shape = ModuleSchemaStatement.Parse("/* DELETE ignored */ create index `review;index` if not exists for (`match`:`ReviewItem`) on (`match`.`delete`); // trailing");
        shape.Name.Should().Be("review;index");
        shape.Properties.Should().Equal("delete");
    }

    [Theory]
    [InlineData("CREATE INDEX review_due FOR (n:ReviewItem) ON (n.due)")]
    [InlineData("CREATE INDEX IF NOT EXISTS FOR (n:ReviewItem) ON (n.due)")]
    [InlineData("CREATE OR REPLACE INDEX review_due IF NOT EXISTS FOR (n:ReviewItem) ON (n.due)")]
    [InlineData("CREATE INDEX review_due IF NOT EXISTS FOR (n:ReviewItem) ON (other.due)")]
    [InlineData("CREATE INDEX review_due IF NOT EXISTS FOR (n:ReviewItem) ON (n.due, n.due)")]
    [InlineData("CREATE INDEX review_due IF NOT EXISTS FOR (n:ReviewItem) ON ()")]
    [InlineData("CREATE INDEX review_due IF NOT EXISTS FOR (n:ReviewItem) ON (n[$property])")]
    [InlineData("CREATE INDEX review_due IF NOT EXISTS FOR (n:ReviewItem) ON (n.due) DELETE n")]
    [InlineData("CREATE INDEX review_due IF NOT EXISTS FOR (n:ReviewItem) ON (n.due); DROP INDEX review_due")]
    [InlineData("CREATE INDEX review_due IF NOT EXISTS FOR (n:ReviewItem:Fact) ON (n.due)")]
    [InlineData("CREATE INDEX $name IF NOT EXISTS FOR (n:ReviewItem) ON (n.due)")]
    [InlineData("CREATE INDEX review_due IF NOT EXISTS FOR (n:$(label)) ON (n.due)")]
    [InlineData("CREATE INDEX review_due IF NOT EXISTS FOR ()-[r:REVIEW_LINK|RELATED_TO]-() ON (r.since)")]
    [InlineData("CREATE CONSTRAINT review_key IF NOT EXISTS FOR (n:ReviewItem) REQUIRE other.id IS UNIQUE")]
    [InlineData("CREATE CONSTRAINT review_key IF NOT EXISTS FOR (n:ReviewItem) REQUIRE n.id IS UNIQUE CALL db.labels()")]
    [InlineData("MATCH (n:ReviewItem) SET n.x = 1")]
    [InlineData("")]
    public void UnsupportedOrAmbiguousShapeCannotBeMistakenForValidatedDdl(string source)
    {
        var action = () => ModuleSchemaStatement.Parse(source);
        action.Should().Throw<FormatException>();
    }
}
