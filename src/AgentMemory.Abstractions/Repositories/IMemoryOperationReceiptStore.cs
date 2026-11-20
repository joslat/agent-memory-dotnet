using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Abstractions.Repositories;

/// <summary>
/// Receipts of applied-once writes (<see cref="ExtractionRequest.OperationId"/>), kept on the stored message each one
/// extracted from. <see cref="RecordAsync"/> must join the ambient persistence transaction, so a receipt commits exactly
/// with the effects it describes. A store without one cannot offer applied-once writes.
/// </summary>
public interface IMemoryOperationReceiptStore
{
    /// <summary>The receipt on this message, or null when no operation has been applied from it.</summary>
    Task<MemoryOperationReceipt?> GetAsync(string messageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the receipt on its message, inside the current transaction. Throws
    /// <see cref="InvalidOperationException"/> when the message is missing or already carries another operation's
    /// receipt (the transaction then rolls back).
    /// </summary>
    Task RecordAsync(MemoryOperationReceipt receipt, CancellationToken cancellationToken = default);
}
