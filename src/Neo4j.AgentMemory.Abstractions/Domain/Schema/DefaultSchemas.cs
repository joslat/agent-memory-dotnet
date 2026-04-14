#pragma warning disable CS1591
namespace Neo4j.AgentMemory.Abstractions.Domain.Schema;

/// <summary>
/// Default schema definitions for the POLE+O and legacy knowledge graph models.
/// Mirrors the Python reference implementation's _get_poleo_entity_types() and
/// _get_poleo_relation_types() exactly.
/// </summary>
public static class DefaultSchemas
{
    /// <summary>Mapping from legacy entity type names to their POLE+O equivalents.</summary>
    public static readonly IReadOnlyDictionary<string, string> LegacyToPoleoMapping =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PERSON"]       = "PERSON",
            ["ORGANIZATION"] = "ORGANIZATION",
            ["LOCATION"]     = "LOCATION",
            ["EVENT"]        = "EVENT",
            ["CONCEPT"]      = "OBJECT",
            ["EMOTION"]      = "OBJECT",
            ["PREFERENCE"]   = "OBJECT",
            ["FACT"]         = "OBJECT",
        };

    /// <summary>Returns the five POLE+O entity type configurations matching the Python reference.</summary>
    public static IReadOnlyList<EntityTypeConfig> GetPoleoEntityTypes() =>
    [
        new EntityTypeConfig
        {
            Name        = "PERSON",
            Description = "Individuals involved in events or associated with objects/locations",
            Subtypes    = ["INDIVIDUAL", "ALIAS", "PERSONA", "SUSPECT", "WITNESS", "VICTIM"],
            Attributes  = ["name", "aliases", "date_of_birth", "nationality", "occupation"],
            Color       = "#4CAF50",
        },
        new EntityTypeConfig
        {
            Name        = "OBJECT",
            Description = "Physical or digital items such as vehicles, phones, documents",
            Subtypes    = ["VEHICLE", "PHONE", "EMAIL", "DOCUMENT", "DEVICE", "WEAPON", "MONEY", "DRUG", "EVIDENCE", "SOFTWARE"],
            Attributes  = ["identifier", "make", "model", "serial_number", "description"],
            Color       = "#2196F3",
        },
        new EntityTypeConfig
        {
            Name        = "LOCATION",
            Description = "Geographical areas, addresses, or specific places",
            Subtypes    = ["ADDRESS", "CITY", "REGION", "COUNTRY", "LANDMARK", "COORDINATES"],
            Attributes  = ["address", "city", "country", "coordinates", "type"],
            Color       = "#FF9800",
        },
        new EntityTypeConfig
        {
            Name        = "EVENT",
            Description = "Incidents that connect entities across time and place",
            Subtypes    = ["INCIDENT", "MEETING", "TRANSACTION", "COMMUNICATION", "CRIME", "TRAVEL", "EMPLOYMENT", "OBSERVATION"],
            Attributes  = ["date", "time", "duration", "description", "outcome"],
            Color       = "#9C27B0",
        },
        new EntityTypeConfig
        {
            Name        = "ORGANIZATION",
            Description = "Companies, non-profits, government agencies, criminal groups",
            Subtypes    = ["COMPANY", "NONPROFIT", "GOVERNMENT", "EDUCATIONAL", "CRIMINAL", "POLITICAL", "RELIGIOUS", "MILITARY"],
            Attributes  = ["name", "type", "jurisdiction", "registration_number"],
            Color       = "#F44336",
        },
    ];

    /// <summary>Returns the 16 POLE+O relation type configurations matching the Python reference.</summary>
    public static IReadOnlyList<RelationTypeConfig> GetPoleoRelationTypes() =>
    [
        // Person relationships
        new RelationTypeConfig
        {
            Name        = "KNOWS",
            Description = "Personal relationship between people",
            SourceTypes = ["PERSON"],
            TargetTypes = ["PERSON"],
        },
        new RelationTypeConfig
        {
            Name        = "ALIAS_OF",
            Description = "Alternative identity of a person",
            SourceTypes = ["PERSON"],
            TargetTypes = ["PERSON"],
        },
        new RelationTypeConfig
        {
            Name        = "MEMBER_OF",
            Description = "Person is member of organization",
            SourceTypes = ["PERSON"],
            TargetTypes = ["ORGANIZATION"],
            Properties  = ["role", "start_date", "end_date"],
        },
        new RelationTypeConfig
        {
            Name        = "EMPLOYED_BY",
            Description = "Person employed by organization",
            SourceTypes = ["PERSON"],
            TargetTypes = ["ORGANIZATION"],
            Properties  = ["position", "start_date", "end_date"],
        },
        // Object relationships
        new RelationTypeConfig
        {
            Name        = "OWNS",
            Description = "Ownership of an object",
            SourceTypes = ["PERSON", "ORGANIZATION"],
            TargetTypes = ["OBJECT"],
            Properties  = ["acquisition_date", "status"],
        },
        new RelationTypeConfig
        {
            Name        = "USES",
            Description = "Usage of an object",
            SourceTypes = ["PERSON"],
            TargetTypes = ["OBJECT"],
        },
        // Location relationships
        new RelationTypeConfig
        {
            Name        = "LOCATED_AT",
            Description = "Entity is located at a place",
            SourceTypes = ["PERSON", "OBJECT", "ORGANIZATION", "EVENT"],
            TargetTypes = ["LOCATION"],
            Properties  = ["from_date", "to_date", "status"],
        },
        new RelationTypeConfig
        {
            Name        = "RESIDES_AT",
            Description = "Person resides at location",
            SourceTypes = ["PERSON"],
            TargetTypes = ["LOCATION"],
            Properties  = ["from_date", "to_date"],
        },
        new RelationTypeConfig
        {
            Name        = "HEADQUARTERS_AT",
            Description = "Organization headquarters location",
            SourceTypes = ["ORGANIZATION"],
            TargetTypes = ["LOCATION"],
        },
        // Event relationships
        new RelationTypeConfig
        {
            Name        = "PARTICIPATED_IN",
            Description = "Entity participated in an event",
            SourceTypes = ["PERSON", "ORGANIZATION"],
            TargetTypes = ["EVENT"],
            Properties  = ["role"],
        },
        new RelationTypeConfig
        {
            Name        = "OCCURRED_AT",
            Description = "Event occurred at location",
            SourceTypes = ["EVENT"],
            TargetTypes = ["LOCATION"],
        },
        new RelationTypeConfig
        {
            Name        = "INVOLVED",
            Description = "Object involved in event",
            SourceTypes = ["EVENT"],
            TargetTypes = ["OBJECT"],
            Properties  = ["role"],
        },
        // Organization relationships
        new RelationTypeConfig
        {
            Name        = "SUBSIDIARY_OF",
            Description = "Organization is subsidiary of another",
            SourceTypes = ["ORGANIZATION"],
            TargetTypes = ["ORGANIZATION"],
        },
        new RelationTypeConfig
        {
            Name        = "PARTNER_WITH",
            Description = "Organizations have partnership",
            SourceTypes = ["ORGANIZATION"],
            TargetTypes = ["ORGANIZATION"],
        },
        // Generic relationships
        new RelationTypeConfig
        {
            Name        = "RELATED_TO",
            Description = "Generic relationship between entities",
            SourceTypes = ["PERSON", "OBJECT", "LOCATION", "EVENT", "ORGANIZATION"],
            TargetTypes = ["PERSON", "OBJECT", "LOCATION", "EVENT", "ORGANIZATION"],
            Properties  = ["type", "description", "confidence"],
        },
        new RelationTypeConfig
        {
            Name        = "MENTIONS",
            Description = "Entity mentions another entity",
            SourceTypes = ["PERSON", "OBJECT", "LOCATION", "EVENT", "ORGANIZATION"],
            TargetTypes = ["PERSON", "OBJECT", "LOCATION", "EVENT", "ORGANIZATION"],
        },
    ];

    /// <summary>Returns the legacy entity type list for backward compatibility.</summary>
    public static IReadOnlyList<EntityTypeConfig> GetLegacyEntityTypes() =>
    [
        new EntityTypeConfig { Name = "PERSON" },
        new EntityTypeConfig { Name = "ORGANIZATION" },
        new EntityTypeConfig { Name = "LOCATION" },
        new EntityTypeConfig { Name = "EVENT" },
        new EntityTypeConfig { Name = "CONCEPT" },
        new EntityTypeConfig { Name = "EMOTION" },
        new EntityTypeConfig { Name = "PREFERENCE" },
        new EntityTypeConfig { Name = "FACT" },
    ];
}
