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
    /// <summary>
    /// A declared view of the provider's own module whose current records the provider sees as
    /// <see cref="IngestionRequest.Existing"/>, so it can update instead of duplicating (Extensibility 0.12, W3). Null: none.
    /// </summary>
    public string? ExistingView { get; init; }
    /// <summary>At most this many existing records are shown.</summary>
    public int MaxExisting { get; init; } = 20;
    /// <summary>At most this many UTF-8 bytes of existing record text are shown; a record that does not fit ends the list.</summary>
    public int MaxExistingUtf8Bytes { get; init; } = 16_384;
}
