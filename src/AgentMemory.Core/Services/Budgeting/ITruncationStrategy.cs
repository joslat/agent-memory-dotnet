using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Core.Services.Budgeting;

/// <summary>
/// A pluggable policy for fitting an over-budget set of assembled memory sections within a character
/// limit. Each strategy maps to one <see cref="TruncationStrategy"/> value; the assembler dispatches to
/// the matching strategy when the assembled context exceeds its <see cref="ContextBudget"/>.
/// <para>Strategies are pure (stateless) and registered in DI as an enumerable, so a consumer can replace
/// or add a strategy for a given <see cref="Strategy"/> value without subclassing the assembler.</para>
/// </summary>
internal interface ITruncationStrategy
{
    /// <summary>The budget strategy this implementation handles.</summary>
    TruncationStrategy Strategy { get; }

    /// <summary>Reduce the sections in <paramref name="input"/> until they fit the character budget.</summary>
    TruncationResult Truncate(TruncationInput input);
}

/// <summary>
/// The over-budget assembled sections plus the budget context a strategy needs. <see cref="TotalCharacters"/>
/// is the estimated cost of all sections (recomputed by victim strategies from the lists; used directly by
/// the proportional strategy for its shrink ratio).
/// </summary>
internal sealed record TruncationInput(
    int MaxCharacters,
    int TotalCharacters,
    IReadOnlyList<Message> Recent,
    IReadOnlyList<Message> Relevant,
    IReadOnlyList<Entity> Entities,
    IReadOnlyList<Preference> Preferences,
    IReadOnlyList<Fact> Facts,
    IReadOnlyList<ReasoningTrace> Traces,
    string? GraphRag);

/// <summary>The sections after truncation. Always considered truncated (the assembler only invokes a
/// strategy once the within-budget early-return has been ruled out).</summary>
internal sealed record TruncationResult(
    IReadOnlyList<Message> Recent,
    IReadOnlyList<Message> Relevant,
    IReadOnlyList<Entity> Entities,
    IReadOnlyList<Preference> Preferences,
    IReadOnlyList<Fact> Facts,
    IReadOnlyList<ReasoningTrace> Traces,
    string? GraphRag);
