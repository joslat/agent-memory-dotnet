using FluentAssertions;
using Neo4j.AgentMemory.Abstractions.Domain.Schema;

namespace Neo4j.AgentMemory.Tests.Unit.Schema;

public sealed class EntityTypeConfigTests
{
    [Fact]
    public void Name_IsRequired_CanBeSet()
    {
        var config = new EntityTypeConfig { Name = "PERSON" };
        config.Name.Should().Be("PERSON");
    }

    [Fact]
    public void Description_DefaultsToNull()
    {
        var config = new EntityTypeConfig { Name = "PERSON" };
        config.Description.Should().BeNull();
    }

    [Fact]
    public void Subtypes_DefaultsToEmpty()
    {
        var config = new EntityTypeConfig { Name = "PERSON" };
        config.Subtypes.Should().BeEmpty();
    }

    [Fact]
    public void Attributes_DefaultsToEmpty()
    {
        var config = new EntityTypeConfig { Name = "PERSON" };
        config.Attributes.Should().BeEmpty();
    }

    [Fact]
    public void Color_DefaultsToNull()
    {
        var config = new EntityTypeConfig { Name = "PERSON" };
        config.Color.Should().BeNull();
    }

    [Fact]
    public void AllProperties_CanBeSet()
    {
        var config = new EntityTypeConfig
        {
            Name        = "PERSON",
            Description = "A person",
            Subtypes    = ["INDIVIDUAL", "ALIAS"],
            Attributes  = ["name", "dob"],
            Color       = "#4CAF50",
        };

        config.Name.Should().Be("PERSON");
        config.Description.Should().Be("A person");
        config.Subtypes.Should().BeEquivalentTo(["INDIVIDUAL", "ALIAS"]);
        config.Attributes.Should().BeEquivalentTo(["name", "dob"]);
        config.Color.Should().Be("#4CAF50");
    }

    [Fact]
    public void Record_EqualityByValue()
    {
        var a = new EntityTypeConfig { Name = "PERSON", Color = "#FFF" };
        var b = new EntityTypeConfig { Name = "PERSON", Color = "#FFF" };
        a.Should().Be(b);
    }
}
