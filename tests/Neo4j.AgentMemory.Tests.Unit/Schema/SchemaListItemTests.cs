using FluentAssertions;
using Neo4j.AgentMemory.Abstractions.Domain.Schema;

namespace Neo4j.AgentMemory.Tests.Unit.Schema;

public sealed class SchemaListItemTests
{
    [Fact]
    public void RequiredProperties_CanBeSet()
    {
        var item = new SchemaListItem
        {
            Name          = "poleo",
            LatestVersion = "1.0",
        };

        item.Name.Should().Be("poleo");
        item.LatestVersion.Should().Be("1.0");
    }

    [Fact]
    public void Description_DefaultsToNull()
    {
        var item = new SchemaListItem { Name = "x", LatestVersion = "1.0" };
        item.Description.Should().BeNull();
    }

    [Fact]
    public void VersionCount_DefaultsToZero()
    {
        var item = new SchemaListItem { Name = "x", LatestVersion = "1.0" };
        item.VersionCount.Should().Be(0);
    }

    [Fact]
    public void IsActive_DefaultsToFalse()
    {
        var item = new SchemaListItem { Name = "x", LatestVersion = "1.0" };
        item.IsActive.Should().BeFalse();
    }

    [Fact]
    public void AllProperties_CanBeSet()
    {
        var item = new SchemaListItem
        {
            Name          = "medical",
            LatestVersion = "2.0",
            Description   = "Medical schema",
            VersionCount  = 3,
            IsActive      = true,
        };

        item.Name.Should().Be("medical");
        item.LatestVersion.Should().Be("2.0");
        item.Description.Should().Be("Medical schema");
        item.VersionCount.Should().Be(3);
        item.IsActive.Should().BeTrue();
    }

    [Fact]
    public void Record_EqualityByValue()
    {
        var a = new SchemaListItem { Name = "x", LatestVersion = "1.0", VersionCount = 2, IsActive = true };
        var b = new SchemaListItem { Name = "x", LatestVersion = "1.0", VersionCount = 2, IsActive = true };
        a.Should().Be(b);
    }
}
