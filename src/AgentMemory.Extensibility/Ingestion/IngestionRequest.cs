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
    /// <summary>
    /// What the core writer stored and closed for the same turn, when the host knows (Extensibility 0.12, W3): a provider
    /// links to core records rather than restating them. Null when unknown; part of the request fingerprint when present.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public CoreWriteOutcome? CoreOutcome { get; init; }
}

/// <summary>What one turn's core write stored and closed.</summary>
[Experimental("AMEXT001")]
public sealed record CoreWriteOutcome
{
    /// <summary>The memories the core writer stored for the turn.</summary>
    public IReadOnlyList<CoreWrittenMemory> Stored { get; init; } = [];
    /// <summary>Identities of the memories it closed (a change or correction it superseded).</summary>
    public IReadOnlyList<string> Closed { get; init; } = [];

    /// <summary>The outcome of a core extraction result: every persisted item, and every closing it reports.</summary>
    public static CoreWriteOutcome From(AgentMemory.Abstractions.Domain.ExtractionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var persisted = result.Outcomes.Where(o => o.Stage == AgentMemory.Abstractions.Domain.IngestionStage.Persistence
            && o.Status == AgentMemory.Abstractions.Domain.IngestionItemStatus.Succeeded && !string.IsNullOrWhiteSpace(o.PersistedId));
        return new()
        {
            Stored = persisted.DistinctBy(o => o.PersistedId)
                .Select(o => new CoreWrittenMemory { Id = o.PersistedId!, Kind = o.Kind.ToString(), Text = o.SourceKey ?? string.Empty })
                .ToList().AsReadOnly(),
            Closed = result.Outcomes.SelectMany(o => o.Closed).Distinct(StringComparer.Ordinal).ToList().AsReadOnly(),
        };
    }
}

/// <summary>One memory the core writer stored.</summary>
[Experimental("AMEXT001")]
public sealed record CoreWrittenMemory
{
    /// <summary>The stored node's identity.</summary>
    public required string Id { get; init; }
    /// <summary>Entity, Fact, Preference or Relationship.</summary>
    public required string Kind { get; init; }
    /// <summary>A readable key: the entity name, or subject, predicate and object; not a trusted instruction.</summary>
    public required string Text { get; init; }
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
