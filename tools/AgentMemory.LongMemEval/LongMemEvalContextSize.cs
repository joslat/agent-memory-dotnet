namespace AgentMemory.LongMemEval;

/// <summary>
/// J5.1. Approximate size, in tokens, of the memory context handed to the reader.
/// </summary>
/// <remarks>
/// <para>
/// Every quality number here is half a result without its cost, and the cost half had no measurement
/// at all: the prepared-pair report records how many items each category contributed but never how
/// large the resulting context was. The band's Structured-673-versus-Hybrid-2,143 figures predate
/// predicate expansion, which adds up to 100 further facts, so the "structured is the cheap rung"
/// premise underneath the tier ladder cannot currently be checked.
/// </para>
/// <para>
/// <b>An estimate, and named one.</b> Four characters per token is the usual English approximation
/// and is deliberately not a real tokenizer: the question this has to answer is whether one arm costs
/// several times another, and a ratio survives a consistent approximation. Calling it
/// <c>Estimate</c> keeps that visible instead of implying a exact count that a downstream reader might
/// compare against a provider's billing.
/// </para>
/// </remarks>
internal static class LongMemEvalContextSize
{
    private const double CharactersPerToken = 4d;

    internal static int Estimate(string? content) =>
        string.IsNullOrWhiteSpace(content)
            ? 0
            : (int)Math.Round(content.Length / CharactersPerToken, MidpointRounding.AwayFromZero);

    /// <summary>Estimated size of a whole assembled context, section by section.</summary>
    internal static int EstimateAll(params string?[] sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        return sections.Sum(Estimate);
    }
}
