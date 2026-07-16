using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Abstractions.Exceptions;

/// <summary>
/// Thrown by the extraction/persistence pipeline when <c>IngestionFailureMode.FailFast</c> is
/// configured and an item fails (#101). Carries every <see cref="IngestionItemOutcome"/> completed
/// before the failure, so a caller can inspect exactly how far ingestion got.
/// </summary>
public sealed class MemoryIngestionException : MemoryException
{
    /// <summary>Every item outcome recorded before the failure that triggered this exception.</summary>
    public IReadOnlyList<IngestionItemOutcome> CompletedOutcomes { get; }

    /// <summary>
    /// Initializes a new instance carrying the outcomes completed before the failure. Use this overload
    /// when fail-fast was triggered by a <see cref="IngestionItemStatus.Skipped"/> outcome (e.g. an
    /// unresolved relationship endpoint) rather than a thrown exception.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="completedOutcomes">Every item outcome recorded before the failure.</param>
    public MemoryIngestionException(string message, IReadOnlyList<IngestionItemOutcome> completedOutcomes)
        : base(message)
    {
        CompletedOutcomes = completedOutcomes;
    }

    /// <summary>Initializes a new instance carrying the outcomes completed before the failure.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="completedOutcomes">Every item outcome recorded before the failure.</param>
    /// <param name="innerException">The exception that triggered fail-fast.</param>
    public MemoryIngestionException(
        string message,
        IReadOnlyList<IngestionItemOutcome> completedOutcomes,
        Exception innerException)
        : base(message, innerException)
    {
        CompletedOutcomes = completedOutcomes;
    }
}
