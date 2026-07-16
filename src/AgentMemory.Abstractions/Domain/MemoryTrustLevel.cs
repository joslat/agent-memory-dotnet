namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// How much a stored <see cref="Entity"/>/<see cref="Fact"/>/<see cref="Preference"/>/
/// <see cref="ReasoningTrace"/> should be trusted when it is later recalled and formatted for a model (#92
/// Phase 3). Ordered so a numeric comparison (<c>&gt;=</c>) means "at least this trusted" -- consumers such
/// as an admission policy's minimum-trust gate rely on that ordering.
/// </summary>
public enum MemoryTrustLevel
{
    /// <summary>No trust signal is available (the default when nothing was stamped).</summary>
    Untrusted = 0,

    /// <summary>Derived from content a user authored during a real conversation.</summary>
    UserProvided = 1,

    /// <summary>Derived from content the model itself generated (e.g. an assistant response).</summary>
    ModelGenerated = 2,

    /// <summary>Derived from a tool/function result rather than conversational text.</summary>
    ToolDerived = 3,

    /// <summary>Derived from an external source whose accuracy has been independently checked.</summary>
    VerifiedExternal = 4,

    /// <summary>Explicitly marked trusted by the hosting application (e.g. curated/imported content).</summary>
    ApplicationTrusted = 5
}
