using FluentAssertions;
using Neo4j.AgentMemory.Abstractions.Domain.Schema;

namespace Neo4j.AgentMemory.Tests.Unit.Schema;

public sealed class DefaultSchemasTests
{
    // ── POLE+O entity types ──────────────────────────────────────────────────────

    [Fact]
    public void PoleoEntityTypes_ReturnsExactlyFiveTypes()
    {
        DefaultSchemas.GetPoleoEntityTypes().Should().HaveCount(5);
    }

    [Fact]
    public void PoleoEntityTypes_Person_HasExpectedSubtypes()
    {
        var person = DefaultSchemas.GetPoleoEntityTypes()
            .Single(t => t.Name == "PERSON");

        person.Subtypes.Should().BeEquivalentTo(
            ["INDIVIDUAL", "ALIAS", "PERSONA", "SUSPECT", "WITNESS", "VICTIM"]);
    }

    [Fact]
    public void PoleoEntityTypes_Object_HasExpectedSubtypes()
    {
        var obj = DefaultSchemas.GetPoleoEntityTypes()
            .Single(t => t.Name == "OBJECT");

        obj.Subtypes.Should().Contain("VEHICLE")
            .And.Contain("PHONE")
            .And.Contain("EMAIL")
            .And.Contain("DOCUMENT")
            .And.Contain("DEVICE")
            .And.Contain("WEAPON")
            .And.Contain("MONEY")
            .And.Contain("DRUG")
            .And.Contain("EVIDENCE")
            .And.Contain("SOFTWARE");
    }

    [Fact]
    public void PoleoEntityTypes_Location_HasExpectedSubtypes()
    {
        var loc = DefaultSchemas.GetPoleoEntityTypes()
            .Single(t => t.Name == "LOCATION");

        loc.Subtypes.Should().BeEquivalentTo(
            ["ADDRESS", "CITY", "REGION", "COUNTRY", "LANDMARK", "COORDINATES"]);
    }

    [Fact]
    public void PoleoEntityTypes_Event_HasExpectedSubtypes()
    {
        var evt = DefaultSchemas.GetPoleoEntityTypes()
            .Single(t => t.Name == "EVENT");

        evt.Subtypes.Should().BeEquivalentTo(
            ["INCIDENT", "MEETING", "TRANSACTION", "COMMUNICATION", "CRIME", "TRAVEL", "EMPLOYMENT", "OBSERVATION"]);
    }

    [Fact]
    public void PoleoEntityTypes_Organization_HasExpectedSubtypes()
    {
        var org = DefaultSchemas.GetPoleoEntityTypes()
            .Single(t => t.Name == "ORGANIZATION");

        org.Subtypes.Should().BeEquivalentTo(
            ["COMPANY", "NONPROFIT", "GOVERNMENT", "EDUCATIONAL", "CRIMINAL", "POLITICAL", "RELIGIOUS", "MILITARY"]);
    }

    [Fact]
    public void PoleoEntityTypes_AllHaveColors()
    {
        var types = DefaultSchemas.GetPoleoEntityTypes();
        types.Should().AllSatisfy(t => t.Color.Should().NotBeNullOrEmpty());
    }

    [Fact]
    public void PoleoEntityTypes_AllHaveDescriptions()
    {
        var types = DefaultSchemas.GetPoleoEntityTypes();
        types.Should().AllSatisfy(t => t.Description.Should().NotBeNullOrEmpty());
    }

    // ── POLE+O relation types ────────────────────────────────────────────────────

    [Fact]
    public void PoleoRelationTypes_HasExpectedCount()
    {
        DefaultSchemas.GetPoleoRelationTypes().Should().HaveCount(16);
    }

    [Fact]
    public void PoleoRelationTypes_ContainsExpectedNames()
    {
        var names = DefaultSchemas.GetPoleoRelationTypes().Select(r => r.Name).ToList();
        names.Should().Contain("KNOWS")
            .And.Contain("ALIAS_OF")
            .And.Contain("MEMBER_OF")
            .And.Contain("EMPLOYED_BY")
            .And.Contain("OWNS")
            .And.Contain("USES")
            .And.Contain("LOCATED_AT")
            .And.Contain("RESIDES_AT")
            .And.Contain("HEADQUARTERS_AT")
            .And.Contain("PARTICIPATED_IN")
            .And.Contain("OCCURRED_AT")
            .And.Contain("INVOLVED")
            .And.Contain("SUBSIDIARY_OF")
            .And.Contain("PARTNER_WITH")
            .And.Contain("RELATED_TO")
            .And.Contain("MENTIONS");
    }

    [Fact]
    public void PoleoRelationTypes_SourceTargetConstraints_Knows()
    {
        var knows = DefaultSchemas.GetPoleoRelationTypes().Single(r => r.Name == "KNOWS");
        knows.SourceTypes.Should().BeEquivalentTo(["PERSON"]);
        knows.TargetTypes.Should().BeEquivalentTo(["PERSON"]);
    }

    [Fact]
    public void PoleoRelationTypes_SourceTargetConstraints_MemberOf()
    {
        var memberOf = DefaultSchemas.GetPoleoRelationTypes().Single(r => r.Name == "MEMBER_OF");
        memberOf.SourceTypes.Should().BeEquivalentTo(["PERSON"]);
        memberOf.TargetTypes.Should().BeEquivalentTo(["ORGANIZATION"]);
        memberOf.Properties.Should().Contain("role");
    }

    [Fact]
    public void PoleoRelationTypes_SourceTargetConstraints_Owns()
    {
        var owns = DefaultSchemas.GetPoleoRelationTypes().Single(r => r.Name == "OWNS");
        owns.SourceTypes.Should().BeEquivalentTo(["PERSON", "ORGANIZATION"]);
        owns.TargetTypes.Should().BeEquivalentTo(["OBJECT"]);
    }

    [Fact]
    public void PoleoRelationTypes_SourceTargetConstraints_LocatedAt()
    {
        var locatedAt = DefaultSchemas.GetPoleoRelationTypes().Single(r => r.Name == "LOCATED_AT");
        locatedAt.SourceTypes.Should().BeEquivalentTo(["PERSON", "OBJECT", "ORGANIZATION", "EVENT"]);
        locatedAt.TargetTypes.Should().BeEquivalentTo(["LOCATION"]);
    }

    [Fact]
    public void PoleoRelationTypes_SourceTargetConstraints_RelatedTo()
    {
        var relatedTo = DefaultSchemas.GetPoleoRelationTypes().Single(r => r.Name == "RELATED_TO");
        var allTypes = new[] { "PERSON", "OBJECT", "LOCATION", "EVENT", "ORGANIZATION" };
        relatedTo.SourceTypes.Should().BeEquivalentTo(allTypes);
        relatedTo.TargetTypes.Should().BeEquivalentTo(allTypes);
    }

    // ── Legacy mapping ───────────────────────────────────────────────────────────

    [Fact]
    public void LegacyMapping_ConceptMapsToObject()
    {
        DefaultSchemas.LegacyToPoleoMapping["CONCEPT"].Should().Be("OBJECT");
    }

    [Fact]
    public void LegacyMapping_EmotionMapsToObject()
    {
        DefaultSchemas.LegacyToPoleoMapping["EMOTION"].Should().Be("OBJECT");
    }

    [Fact]
    public void LegacyMapping_PreferenceMapsToObject()
    {
        DefaultSchemas.LegacyToPoleoMapping["PREFERENCE"].Should().Be("OBJECT");
    }

    [Fact]
    public void LegacyMapping_FactMapsToObject()
    {
        DefaultSchemas.LegacyToPoleoMapping["FACT"].Should().Be("OBJECT");
    }

    [Fact]
    public void LegacyMapping_AllLegacyTypesMapped()
    {
        var expected = new[] { "PERSON", "ORGANIZATION", "LOCATION", "EVENT", "CONCEPT", "EMOTION", "PREFERENCE", "FACT" };
        DefaultSchemas.LegacyToPoleoMapping.Keys.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void LegacyMapping_IsCaseInsensitive()
    {
        DefaultSchemas.LegacyToPoleoMapping["concept"].Should().Be("OBJECT");
        DefaultSchemas.LegacyToPoleoMapping["Person"].Should().Be("PERSON");
    }
}
