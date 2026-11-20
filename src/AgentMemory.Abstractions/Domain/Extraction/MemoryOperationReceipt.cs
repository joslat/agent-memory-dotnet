namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// The evidence that one applied-once write (<see cref="ExtractionRequest.OperationId"/>) committed: written in the same
/// store transaction as its effects, so it exists exactly when they do. It holds ids only, never content.
/// </summary>
public sealed record MemoryOperationReceipt
{
    /// <summary>The operation this receipt is for.</summary>
    public required string OperationId { get; init; }

    /// <summary>The stored user message the operation extracted from; the receipt lives on it.</summary>
    public required string MessageId { get; init; }

    /// <summary>When the store transaction wrote it.</summary>
    public required DateTimeOffset AppliedAtUtc { get; init; }

    /// <summary>Facts the operation wrote (new, or a stored one said again).</summary>
    public IReadOnlyList<string> FactIds { get; init; } = [];

    /// <summary>Preferences the operation wrote.</summary>
    public IReadOnlyList<string> PreferenceIds { get; init; } = [];

    /// <summary>Stored memories the operation closed (replaced or corrected).</summary>
    public IReadOnlyList<string> ClosedIds { get; init; } = [];
}
