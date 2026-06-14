using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Core.Services.Budgeting;

/// <summary>
/// <see cref="TruncationStrategy.Fail"/> — refuse to truncate. Throws a <see cref="MemoryError"/> carrying
/// the over-budget totals so the caller can surface a hard limit rather than silently dropping context.
/// </summary>
internal sealed class FailTruncationStrategy : ITruncationStrategy
{
    public TruncationStrategy Strategy => TruncationStrategy.Fail;

    public TruncationResult Truncate(TruncationInput input) =>
        throw MemoryError.Create(
                $"Context budget exceeded: {input.TotalCharacters} chars (limit {input.MaxCharacters}).")
            .WithCode(MemoryErrorCodes.ContextBudgetExceeded)
            .WithMetadata("totalChars", input.TotalCharacters)
            .WithMetadata("maxChars", input.MaxCharacters)
            .Build();
}
