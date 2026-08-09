using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// J5.1. Every quality number this track produces is half a result without its cost.
/// </summary>
/// <remarks>
/// The recorded band gives Structured 673 tokens against Hybrid's 2,143, and that ratio is the
/// load-bearing premise of the whole tier ladder — <c>light</c> exists because structured-only was
/// cheap. Those figures predate predicate expansion, which now adds up to 100 facts, so the premise
/// is very likely false and nothing in the report can settle it: item counts are recorded, context
/// size is not.
/// </remarks>
public sealed class LongMemEvalContextSizeTests
{
    [Fact]
    public void AnEmptyContextCostsNothing() =>
        LongMemEvalContextSize.Estimate(null).Should().Be(0);

    [Fact]
    public void SizeGrowsWithContent()
    {
        var small = LongMemEvalContextSize.Estimate("a short line");
        var large = LongMemEvalContextSize.Estimate(string.Concat(Enumerable.Repeat("a short line ", 50)));

        large.Should().BeGreaterThan(small);
    }

    [Fact]
    public void TheEstimateIsAboutFourCharactersPerToken()
    {
        // Deliberately an estimate, not a tokenizer: the answer needed is "is Structured still three
        // times cheaper than Hybrid", and a ratio survives a consistent approximation. Naming it
        // Estimate keeps that visible rather than implying a real token count.
        LongMemEvalContextSize.Estimate(new string('x', 400)).Should().BeInRange(90, 110);
    }

    [Fact]
    public void TheEstimateIsStable()
    {
        // It becomes recorded run metadata, so it must not drift between calls.
        const string Text = "the blue sofa was bought in March";
        LongMemEvalContextSize.Estimate(Text).Should().Be(LongMemEvalContextSize.Estimate(Text));
    }

    [Fact]
    public void WhitespaceOnlyContentCostsNothing() =>
        LongMemEvalContextSize.Estimate("   \n\t ").Should().Be(0);
}
