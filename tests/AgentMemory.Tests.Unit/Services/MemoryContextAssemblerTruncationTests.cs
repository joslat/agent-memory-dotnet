using FluentAssertions;
using AgentMemory.Core.Services;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// cycle-5 #5 — proportional GraphRAG truncation must not split a UTF-16 surrogate pair (e.g. an emoji),
/// which would emit an orphaned surrogate.
/// </summary>
public sealed class MemoryContextAssemblerTruncationTests
{
    [Theory]
    [InlineData("hello", 3, "hel")]
    [InlineData("hello", 10, "hello")] // budget >= length ⇒ unchanged
    [InlineData("hello", 0, "")]
    [InlineData("hello", -5, "")]
    public void TruncateToCharBudget_Ascii(string input, int budget, string expected)
    {
        MemoryContextAssembler.TruncateToCharBudget(input, budget).Should().Be(expected);
    }

    [Fact]
    public void TruncateToCharBudget_DoesNotSplitSurrogatePair()
    {
        // "😀" (U+1F600) is a surrogate PAIR (2 UTF-16 char units), followed by "world".
        const string text = "😀world";

        // Cutting at 2 keeps the whole emoji.
        MemoryContextAssembler.TruncateToCharBudget(text, 2).Should().Be("😀");
        // Cutting at 1 would split the pair — must back off to 0 (drop the half-emoji) rather than orphan it.
        MemoryContextAssembler.TruncateToCharBudget(text, 1).Should().Be("");
    }

    [Fact]
    public void TruncateToCharBudget_NeverEmitsLoneSurrogate_AcrossAllBudgets()
    {
        const string text = "a😀b😀c"; // mix of BMP + non-BMP

        for (int budget = 0; budget <= text.Length + 1; budget++)
        {
            var result = MemoryContextAssembler.TruncateToCharBudget(text, budget);
            // A well-formed UTF-16 string has no high surrogate that isn't immediately followed by a low one,
            // and no low surrogate that isn't immediately preceded by a high one.
            for (int i = 0; i < result.Length; i++)
            {
                if (char.IsHighSurrogate(result[i]))
                    (i + 1 < result.Length && char.IsLowSurrogate(result[i + 1])).Should().BeTrue(
                        $"high surrogate at {i} (budget {budget}) must be paired");
                if (char.IsLowSurrogate(result[i]))
                    (i > 0 && char.IsHighSurrogate(result[i - 1])).Should().BeTrue(
                        $"low surrogate at {i} (budget {budget}) must be paired");
            }
        }
    }
}
