using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Extensibility.Ingestion;

/// <summary>How the host selected ingestion. Automatic turns never authorize shared writes.</summary>
[Experimental("AMEXT001")]
public enum IngestionTrigger
{
    /// <summary>Automatic processing after a successful persisted turn.</summary>
    EveryTurn,
    /// <summary>Explicit host selection of providers in the request.</summary>
    OnRequest
}

/// <summary>Host input for module ingestion; owner claims require isolation-policy resolution before use.</summary>
[Experimental("AMEXT001")]
public sealed record IngestionRequest
{
    /// <summary>Session containing the persisted source messages.</summary>
    public required string SessionId { get; init; }
    /// <summary>Claimed owner on input; providers and admission receive the resolved owner.</summary>
    public string? Owner { get; init; }
    /// <summary>Candidate source messages. Storage must verify actual owner/session membership.</summary>
    public IReadOnlyList<IngestedMessage> Messages { get; init; } = [];
    /// <summary>Turn time for proposal generation, not authority to set a record's creation time.</summary>
    public DateTimeOffset AtUtc { get; init; }
    /// <summary>Defaults to an automatic turn; shared types require explicit OnRequest selection.</summary>
    public IngestionTrigger Trigger { get; init; }
    /// <summary>Exact moduleId:local provider identities explicitly selected by an OnRequest host.</summary>
    public IReadOnlyList<string> ProviderIds { get; init; } = [];
    /// <summary>
    /// The provider's own module records its <see cref="IngestionProviderDescriptor.ExistingView"/> returns for the resolved
    /// owner, bounded by its limits. Filled by the ingestion engine for each provider; a caller's value is ignored, and it is
    /// not part of the request's fingerprint.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<Reading.ModuleViewItem> Existing { get; init; } = [];
}

/// <summary>A persisted user or assistant message, identified using the host's storage identity.</summary>
[Experimental("AMEXT001")]
public sealed record IngestedMessage
{
    /// <summary>Actual stored message identity; never a predicted substitute for persistence.</summary>
    public required string Id { get; init; }
    /// <summary>User or assistant role, represented as user or assistant.</summary>
    public required string Role { get; init; }
    /// <summary>Untrusted message content.</summary>
    public required string Text { get; init; }
}
