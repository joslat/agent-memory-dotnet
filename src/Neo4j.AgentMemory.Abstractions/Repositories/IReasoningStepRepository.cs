using Neo4j.AgentMemory.Abstractions.Domain;

namespace Neo4j.AgentMemory.Abstractions.Repositories;

/// <summary>
/// Repository for reasoning step persistence.
/// </summary>
public interface IReasoningStepRepository
{
    /// <summary>
    /// Adds a reasoning step.
    /// </summary>
    Task<ReasoningStep> AddAsync(
        ReasoningStep step,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets steps for a trace.
    /// </summary>
    Task<IReadOnlyList<ReasoningStep>> GetByTraceAsync(
        string traceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a step by identifier.
    /// </summary>
    Task<ReasoningStep?> GetByIdAsync(
        string stepId,
        CancellationToken cancellationToken = default);
}
