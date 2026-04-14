using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Options;

namespace Neo4j.AgentMemory.Abstractions.Services;

/// <summary>
/// Compresses conversation context when token count exceeds budget.
/// Implements a 3-tier strategy: reflections → observations → recent messages.
/// </summary>
public interface IContextCompressor
{
    /// <summary>
    /// Compresses messages when total tokens exceed the threshold.
    /// Returns a compressed context with summary observations.
    /// </summary>
    Task<CompressedContext> CompressAsync(
        IReadOnlyList<Message> messages,
        ContextCompressionOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Estimates token count for a list of messages.
    /// </summary>
    int EstimateTokenCount(IReadOnlyList<Message> messages);
}
