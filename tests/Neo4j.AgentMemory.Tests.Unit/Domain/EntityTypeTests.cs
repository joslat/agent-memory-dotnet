using FluentAssertions;
using Neo4j.AgentMemory.Abstractions.Domain;

namespace Neo4j.AgentMemory.Tests.Unit.Domain;

public sealed class EntityTypeTests
{
    // ── Constants ──

    [Fact]
    public void Person_IsUppercase()
    {
        EntityType.Person.Should().Be("PERSON");
    }

    [Fact]
    public void Object_IsUppercase()
    {
        EntityType.Object.Should().Be("OBJECT");
    }

    [Fact]
    public void Location_IsUppercase()
    {
        EntityType.Location.Should().Be("LOCATION");
    }

    [Fact]
    public void Event_IsUppercase()
    {
        EntityType.Event.Should().Be("EVENT");
    }

    [Fact]
    public void Organization_IsUppercase()
    {
        EntityType.Organization.Should().Be("ORGANIZATION");
    }

    [Fact]
    public void Unknown_IsUppercase()
    {
        EntityType.Unknown.Should().Be("UNKNOWN");
    }

    // ── All collection ──

    [Fact]
    public void All_ContainsExactlyFivePoleOTypes()
    {
        EntityType.All.Should().HaveCount(5);
    }

    [Fact]
    public void All_ContainsAllPoleOTypes()
    {
        EntityType.All.Should().Contain(EntityType.Person);
        EntityType.All.Should().Contain(EntityType.Object);
        EntityType.All.Should().Contain(EntityType.Location);
        EntityType.All.Should().Contain(EntityType.Event);
        EntityType.All.Should().Contain(EntityType.Organization);
    }

    [Fact]
    public void All_DoesNotContainUnknown()
    {
        EntityType.All.Should().NotContain(EntityType.Unknown);
    }

    // ── IsKnownType ──

    [Theory]
    [InlineData("PERSON")]
    [InlineData("OBJECT")]
    [InlineData("LOCATION")]
    [InlineData("EVENT")]
    [InlineData("ORGANIZATION")]
    public void IsKnownType_ReturnsTrueForKnownTypes(string type)
    {
        EntityType.IsKnownType(type).Should().BeTrue();
    }

    [Theory]
    [InlineData("person")]
    [InlineData("Person")]
    [InlineData("pERSON")]
    [InlineData("organization")]
    [InlineData("Location")]
    public void IsKnownType_IsCaseInsensitive(string type)
    {
        EntityType.IsKnownType(type).Should().BeTrue();
    }

    [Theory]
    [InlineData("UNKNOWN")]
    [InlineData("Animal")]
    [InlineData("")]
    [InlineData("CONCEPT")]
    public void IsKnownType_ReturnsFalseForUnknownTypes(string type)
    {
        EntityType.IsKnownType(type).Should().BeFalse();
    }

    // ── Normalize ──

    [Theory]
    [InlineData("person", "PERSON")]
    [InlineData("Person", "PERSON")]
    [InlineData("PERSON", "PERSON")]
    [InlineData("organization", "ORGANIZATION")]
    [InlineData("location", "LOCATION")]
    [InlineData("event", "EVENT")]
    [InlineData("object", "OBJECT")]
    public void Normalize_ReturnsCanonicalFormForKnownTypes(string input, string expected)
    {
        EntityType.Normalize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("Animal")]
    [InlineData("CONCEPT")]
    [InlineData("custom-type")]
    public void Normalize_PreservesUnknownTypesUnchanged(string input)
    {
        EntityType.Normalize(input).Should().Be(input);
    }

    [Fact]
    public void Normalize_ReturnsUnknownUnchanged()
    {
        EntityType.Normalize("UNKNOWN").Should().Be("UNKNOWN");
    }
}
