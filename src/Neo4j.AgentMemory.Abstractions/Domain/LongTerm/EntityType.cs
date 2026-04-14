namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// POLE+O entity classification model used in intelligence analysis.
/// Person, Object, Location, Event + Organization.
/// </summary>
public static class EntityType
{
    /// <summary>A person entity.</summary>
    public const string Person = "PERSON";

    /// <summary>An object entity.</summary>
    public const string Object = "OBJECT";

    /// <summary>A location entity.</summary>
    public const string Location = "LOCATION";

    /// <summary>An event entity.</summary>
    public const string Event = "EVENT";

    /// <summary>An organization entity.</summary>
    public const string Organization = "ORGANIZATION";

    /// <summary>Unknown or unclassified entity type.</summary>
    public const string Unknown = "UNKNOWN";

    /// <summary>
    /// All known POLE+O entity types.
    /// </summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        Person, Object, Location, Event, Organization
    };

    /// <summary>
    /// Returns true if the type is a recognized POLE+O type.
    /// Case-insensitive comparison.
    /// </summary>
    public static bool IsKnownType(string type)
        => All.Any(t => string.Equals(t, type, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Normalizes a type string to its canonical POLE+O form.
    /// Returns the input unchanged if not a known type.
    /// </summary>
    public static string Normalize(string type)
        => All.FirstOrDefault(t => string.Equals(t, type, StringComparison.OrdinalIgnoreCase)) ?? type;
}
