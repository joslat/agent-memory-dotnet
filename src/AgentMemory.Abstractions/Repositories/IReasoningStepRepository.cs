using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Abstractions.Repositories;

/// <summary>
/// Repository for reasoning step persistence.
/// <para><b>R1 scoping note:</b> steps are children of a <c>ReasoningTrace</c> and carry no owner of their
/// own. Every read is keyed by a random parent handle (a trace id or step id) that a caller can only
/// obtain through an owner-scoped trace search, so these reads are <i>by-handle</i> — the same intentional
/// exemption applied to every <c>GetByIdAsync</c> in the codebase. Owner isolation is enforced at the
/// trace tier (see <c>IReasoningTraceRepository</c>); steps inherit it transitively.</para>
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
