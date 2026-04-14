using FluentAssertions;
using Neo4j.AgentMemory.Abstractions.Domain.Schema;

namespace Neo4j.AgentMemory.Tests.Unit.Schema;

public sealed class EntitySchemaConfigTests
{
    // ── Default schema ───────────────────────────────────────────────────────────

    [Fact]
    public void DefaultSchema_HasFivePoleoTypes()
    {
        var schema = new EntitySchemaConfig();
        schema.EntityTypes.Should().HaveCount(5);
    }

    [Fact]
    public void DefaultSchema_HasExpectedRelationTypes()
    {
        var schema = new EntitySchemaConfig();
        schema.RelationTypes.Should().HaveCount(16);
    }

    [Fact]
    public void DefaultSchema_DefaultEntityType_IsObject()
    {
        var schema = new EntitySchemaConfig();
        schema.DefaultEntityType.Should().Be("OBJECT");
    }

    [Fact]
    public void DefaultSchema_EnableSubtypes_IsTrue()
    {
        new EntitySchemaConfig().EnableSubtypes.Should().BeTrue();
    }

    [Fact]
    public void DefaultSchema_StrictTypes_IsFalse()
    {
        new EntitySchemaConfig().StrictTypes.Should().BeFalse();
    }

    // ── GetEntityTypeNames ───────────────────────────────────────────────────────

    [Fact]
    public void GetEntityTypeNames_ReturnsAllTypes()
    {
        var schema = new EntitySchemaConfig();
        schema.GetEntityTypeNames().Should().BeEquivalentTo(
            ["PERSON", "OBJECT", "LOCATION", "EVENT", "ORGANIZATION"]);
    }

    // ── GetSubtypes ──────────────────────────────────────────────────────────────

    [Fact]
    public void GetSubtypes_Person_ReturnsExpectedSubtypes()
    {
        var schema = new EntitySchemaConfig();
        schema.GetSubtypes("PERSON").Should().BeEquivalentTo(
            ["INDIVIDUAL", "ALIAS", "PERSONA", "SUSPECT", "WITNESS", "VICTIM"]);
    }

    [Fact]
    public void GetSubtypes_IsCaseInsensitive()
    {
        var schema = new EntitySchemaConfig();
        schema.GetSubtypes("person").Should().NotBeEmpty();
    }

    [Fact]
    public void GetSubtypes_UnknownType_ReturnsEmpty()
    {
        var schema = new EntitySchemaConfig();
        schema.GetSubtypes("ANIMAL").Should().BeEmpty();
    }

    // ── IsValidType ──────────────────────────────────────────────────────────────

    [Fact]
    public void IsValidType_NonStrictMode_AcceptsAnything()
    {
        var schema = new EntitySchemaConfig { StrictTypes = false };
        schema.IsValidType("FOOBAR").Should().BeTrue();
        schema.IsValidType("PERSON").Should().BeTrue();
    }

    [Fact]
    public void IsValidType_StrictMode_AcceptsKnownTypes()
    {
        var schema = new EntitySchemaConfig { StrictTypes = true };
        schema.IsValidType("PERSON").Should().BeTrue();
        schema.IsValidType("ORGANIZATION").Should().BeTrue();
    }

    [Fact]
    public void IsValidType_StrictMode_RejectsUnknown()
    {
        var schema = new EntitySchemaConfig { StrictTypes = true };
        schema.IsValidType("ANIMAL").Should().BeFalse();
        schema.IsValidType("FOOBAR").Should().BeFalse();
    }

    [Fact]
    public void IsValidType_StrictMode_IsCaseInsensitive()
    {
        var schema = new EntitySchemaConfig { StrictTypes = true };
        schema.IsValidType("person").Should().BeTrue();
    }

    // ── NormalizeType ────────────────────────────────────────────────────────────

    [Fact]
    public void NormalizeType_KnownType_ReturnsCanonicalized()
    {
        var schema = new EntitySchemaConfig();
        schema.NormalizeType("person").Should().Be("PERSON");
        schema.NormalizeType("Person").Should().Be("PERSON");
        schema.NormalizeType("PERSON").Should().Be("PERSON");
    }

    [Fact]
    public void NormalizeType_UnknownType_NonStrictMode_ReturnsUppercased()
    {
        var schema = new EntitySchemaConfig { StrictTypes = false };
        schema.NormalizeType("animal").Should().Be("ANIMAL");
        schema.NormalizeType("custom-type").Should().Be("CUSTOM-TYPE");
    }

    [Fact]
    public void NormalizeType_UnknownType_StrictMode_ReturnsDefault()
    {
        var schema = new EntitySchemaConfig { StrictTypes = true, DefaultEntityType = "OBJECT" };
        schema.NormalizeType("ANIMAL").Should().Be("OBJECT");
        schema.NormalizeType("FOOBAR").Should().Be("OBJECT");
    }

    // ── GetRelationTypeNames ─────────────────────────────────────────────────────

    [Fact]
    public void GetRelationTypeNames_ReturnsAllRelations()
    {
        var schema = new EntitySchemaConfig();
        var names = schema.GetRelationTypeNames();
        names.Should().HaveCount(16);
        names.Should().Contain("KNOWS").And.Contain("RELATED_TO").And.Contain("MENTIONS");
    }
}
