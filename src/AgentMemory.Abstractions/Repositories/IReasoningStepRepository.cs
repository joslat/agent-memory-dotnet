using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Abstractions.Repositories;

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

    /// <summary>
    /// Links a reasoning step to existing entities (by id) via <c>:TOUCHED</c> audit edges. Entity ids
    /// that do not resolve to a node — and a non-existent step — are silently skipped. Idempotent.
    /// Returns the number of entities linked.
    /// </summary>
    Task<int> LinkTouchedEntitiesAsync(
        string stepId,
        IReadOnlyList<string> entityIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the ids of all entities a reasoning step touched (via <c>:TOUCHED</c> edges), ordered by id.
    /// </summary>
    Task<IReadOnlyList<string>> GetTouchedEntityIdsAsync(
        string stepId,
        CancellationToken cancellationToken = default);
}
