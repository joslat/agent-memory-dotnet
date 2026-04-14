using FluentAssertions;
using Neo4j.AgentMemory.Abstractions.Domain.Schema;

namespace Neo4j.AgentMemory.Tests.Unit.Schema;

public sealed class RelationTypeConfigTests
{
    [Fact]
    public void Name_IsRequired_CanBeSet()
    {
        var config = new RelationTypeConfig { Name = "KNOWS" };
        config.Name.Should().Be("KNOWS");
    }

    [Fact]
    public void Description_DefaultsToNull()
    {
        var config = new RelationTypeConfig { Name = "KNOWS" };
        config.Description.Should().BeNull();
    }

    [Fact]
    public void SourceTypes_DefaultsToEmpty()
    {
        var config = new RelationTypeConfig { Name = "KNOWS" };
        config.SourceTypes.Should().BeEmpty();
    }

    [Fact]
    public void TargetTypes_DefaultsToEmpty()
    {
        var config = new RelationTypeConfig { Name = "KNOWS" };
        config.TargetTypes.Should().BeEmpty();
    }

    [Fact]
    public void Properties_DefaultsToEmpty()
    {
        var config = new RelationTypeConfig { Name = "KNOWS" };
        config.Properties.Should().BeEmpty();
    }

    [Fact]
    public void AllProperties_CanBeSet()
    {
        var config = new RelationTypeConfig
        {
            Name        = "MEMBER_OF",
            Description = "Member of org",
            SourceTypes = ["PERSON"],
            TargetTypes = ["ORGANIZATION"],
            Properties  = ["role", "start_date"],
        };

        config.Name.Should().Be("MEMBER_OF");
        config.Description.Should().Be("Member of org");
        config.SourceTypes.Should().BeEquivalentTo(["PERSON"]);
        config.TargetTypes.Should().BeEquivalentTo(["ORGANIZATION"]);
        config.Properties.Should().BeEquivalentTo(["role", "start_date"]);
    }

    [Fact]
    public void Record_EqualityByValue()
    {
        var a = new RelationTypeConfig { Name = "KNOWS", Description = "Knows" };
        var b = new RelationTypeConfig { Name = "KNOWS", Description = "Knows" };
        a.Should().Be(b);
    }
}
