using FluentAssertions;
using AgentMemory.Abstractions.Options;
using AgentMemory.Core.Services;
using AgentMemory.Core.Services.Budgeting;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// cycle-5 #5 — proportional GraphRAG truncation must not split a UTF-16 surrogate pair (e.g. an emoji),
/// which would emit an orphaned surrogate. The logic now lives in <see cref="ContextBudgetEstimator"/>
/// (S9 extraction).
/// </summary>
public sealed class MemoryContextAssemblerTruncationTests
{
    // R5: token→char budget must never wrap negative on a very large MaxTokens (which would invert the
    // "totalChars <= maxChars" check and silently truncate the whole context to empty).
    [Theory]
    [InlineData(600_000_000, int.MaxValue)]   // the reported trigger: 600M*4 = 2.4e9 -> clamp, NOT negative
    [InlineData(int.MaxValue, int.MaxValue)]
    [InlineData(4096, 16384)]                 // normal: 4096*4
    [InlineData(536_870_911, 2_147_483_644)]  // largest MaxTokens whose *4 still fits int
    public void ResolveMaxChars_TokenBudget_NeverWrapsNegative(int maxTokens, int expected)
    {
        int maxChars = MemoryContextAssembler.ResolveMaxChars(new ContextBudget { MaxTokens = maxTokens });
        maxChars.Should().BePositive();
        maxChars.Should().Be(expected);
    }

    [Fact]
    public void ResolveMaxChars_MaxCharactersWins_OverTokens()
        => MemoryContextAssembler.ResolveMaxChars(new ContextBudget { MaxCharacters = 100, MaxTokens = 600_000_000 }).Should().Be(100);

    [Fact]
    public void ResolveMaxChars_NoLimits_IsIntMaxValue()
        => MemoryContextAssembler.ResolveMaxChars(new ContextBudget()).Should().Be(int.MaxValue);

    [Theory]
    [InlineData("hello", 3, "hel")]
    [InlineData("hello", 10, "hello")] // budget >= length ⇒ unchanged
    [InlineData("hello", 0, "")]
    [InlineData("hello", -5, "")]
    public void TruncateToCharBudget_Ascii(string input, int budget, string expected)
    {
        ContextBudgetEstimator.TruncateToCharBudget(input, budget).Should().Be(expected);
    }

    [Fact]
    public void TruncateToCharBudget_DoesNotSplitSurrogatePair()
    {
        // "😀" (U+1F600) is a surrogate PAIR (2 UTF-16 char units), followed by "world".
        const string text = "😀world";

        // Cutting at 2 keeps the whole emoji.
        ContextBudgetEstimator.TruncateToCharBudget(text, 2).Should().Be("😀");
        // Cutting at 1 would split the pair — must back off to 0 (drop the half-emoji) rather than orphan it.
        ContextBudgetEstimator.TruncateToCharBudget(text, 1).Should().Be("");
    }

    [Fact]
    public void TruncateToCharBudget_NeverEmitsLoneSurrogate_AcrossAllBudgets()
    {
        const string text = "a😀b😀c"; // mix of BMP + non-BMP

        for (int budget = 0; budget <= text.Length + 1; budget++)
        {
            var result = ContextBudgetEstimator.TruncateToCharBudget(text, budget);
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
