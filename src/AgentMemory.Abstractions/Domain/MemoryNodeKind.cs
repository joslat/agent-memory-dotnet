namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// The kind of long-term memory node a decay/maintenance operation targets. The enum name matches the
/// Neo4j node label exactly (<c>Entity</c>/<c>Fact</c>/<c>Preference</c>). Replaces the earlier free-form
/// <c>string nodeLabel</c> parameters with a closed set — invalid labels are now a compile error.
/// </summary>
public enum MemoryNodeKind
{
    /// <summary>An <c>:Entity</c> node.</summary>
    Entity = 0,

    /// <summary>A <c>:Fact</c> node.</summary>
    Fact = 1,

    /// <summary>A <c>:Preference</c> node.</summary>
    Preference = 2
}
