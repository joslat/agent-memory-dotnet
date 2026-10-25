namespace AgentMemory.AgentFramework;

/// <summary>Immutable source projection captured only after every message in a turn was persisted.</summary>
/// <remarks>Owner is the host claim, not an authorization grant. Consumers must apply their isolation policy.
/// Message IDs/content come from storage results, not predictions from provider input. This is not a
/// transaction receipt for the entire application: earlier message writes may survive a later failure.</remarks>
public sealed record PersistedMemoryTurn
{
    /// <summary>Session used for persistence.</summary>
    public required string SessionId { get; init; }
    /// <summary>Conversation used for persistence.</summary>
    public required string ConversationId { get; init; }
    /// <summary>Host-supplied owner claim.</summary>
    public string? Owner { get; init; }
    /// <summary>Effective application/store selection captured while its scope is active.</summary>
    public string? ApplicationId { get; init; }
    /// <summary>Host clock at completion of message storage.</summary>
    public required DateTimeOffset AtUtc { get; init; }
    /// <summary>Persisted messages in request/response order, before core extraction role filtering.</summary>
    public IReadOnlyList<PersistedTurnMessage> Messages { get; init; } = [];
    /// <summary>
    /// This turn's core extraction: completes with its result when it finishes (inline, or later on the background queue),
    /// or with null when there was none or it failed. Work queued after core's on the same owner key sees it completed.
    /// </summary>
    public Task<Abstractions.Domain.ExtractionResult?> CoreWrite { get; init; } = NoCoreWrite;

    // One shared completed task, so two turns that differ in nothing else stay equal.
    private static readonly Task<Abstractions.Domain.ExtractionResult?> NoCoreWrite = Task.FromResult<Abstractions.Domain.ExtractionResult?>(null);
}

/// <summary>Stored source identity and text without mutable embeddings, metadata or provider message objects.</summary>
public sealed record PersistedTurnMessage
{
    /// <summary>Identity actually returned by storage.</summary>
    public required string Id { get; init; }
    /// <summary>Stored role; consumers choose which roles their own capabilities accept.</summary>
    public required string Role { get; init; }
    /// <summary>Stored, untrusted text.</summary>
    public required string Text { get; init; }
}
