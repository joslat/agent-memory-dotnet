using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Extensibility.Ingestion;

/// <summary>Host-bound provider identity and permitted invocation mode; never supplied by a proposal.</summary>
[Experimental("AMEXT001")]
public sealed record IngestionProviderDescriptor
{
    /// <summary>Module-owned provider identity in moduleId:local form.</summary>
    public required string Id { get; init; }
    /// <summary>Automatic by default. Only OnRequest providers may propose shared types.</summary>
    public IngestionTrigger Trigger { get; init; }
}
