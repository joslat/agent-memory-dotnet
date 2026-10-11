using System.Diagnostics.CodeAnalysis;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Gate;

/// <summary>Detects a compound memory need and returns two or three constrained retrieval focuses.</summary>
/// <remarks>
/// Return an empty list for a question that should not be split. Preserve shared people, dates,
/// comparisons and negation; do not answer the question or invent missing context. The caller bounds
/// and validates the result and preserves baseline retrieval if planning fails.
/// </remarks>
[Experimental("AMGATE001")]
public interface ICompoundQuestionPlanner
{
    /// <summary>Plans optional retrieval focuses without access to a store or candidate memories.</summary>
    Task<IReadOnlyList<string>> PlanAsync(CompoundQuestionRequest request, CancellationToken cancellationToken = default);
}

/// <summary>The question and conversation supplied to a compound-question planner.</summary>
[Experimental("AMGATE001")]
public sealed record CompoundQuestionRequest
{
    /// <summary>The complete original question.</summary>
    public required string Question { get; init; }

    /// <summary>Recent conversation, oldest first.</summary>
    public IReadOnlyList<MemoryGateTurn> Conversation { get; init; } = [];

    /// <summary>The recall's clock, retained for relative dates.</summary>
    public required DateTimeOffset NowUtc { get; init; }
}
