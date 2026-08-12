using AgentMemory.Abstractions.Domain;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// The token half of B3: what answering from memory costs against answering from the whole
/// conversation.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the most persuasive number in the category precisely because it is a claim about
/// architecture.</b> "1.6k context tokens instead of 115k" cannot be inflated by a better answer
/// model, cannot be tuned with prompt engineering, and does not move when the judge changes its mind.
/// It is either true of the assembled context or it is not.
/// </para>
/// <para>
/// Which is why it is counted rather than estimated, and why the counting method travels with the
/// number: a ratio derived from a chars/4 heuristic would be a claim about arithmetic wearing the
/// clothes of a claim about design.
/// </para>
/// </remarks>
public sealed class ContextTokenBreakdownTests
{
    private static readonly LongMemEvalTokenCounter Counter = new("gpt-4o");

    private static Message M(string content) => new()
    {
        MessageId = $"m-{content.GetHashCode():X}",
        ConversationId = "c-1",
        SessionId = "s-1",
        Role = "user",
        Content = content,
        TimestampUtc = DateTimeOffset.UnixEpoch,
    };

    private static MemoryContext Context(params Fact[] facts) => new()
    {
        SessionId = "s-1",
        AssembledAtUtc = DateTimeOffset.UnixEpoch,
        RelevantFacts = new MemoryContextSection<Fact> { Items = facts },
    };

    private static Fact F(string subject, string predicate, string @object) => new()
    {
        FactId = $"f-{@object}",
        Subject = subject,
        Predicate = predicate,
        Object = @object,
        Confidence = 0.9,
        CreatedAtUtc = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void MemoryCostsFarLessThanTheConversationItWasBuiltFrom()
    {
        // THE claim. A long conversation distils to a handful of triples, and the ratio is what a
        // reader is actually being asked to believe.
        var history = Enumerable.Range(0, 200)
            .Select(i => M($"turn {i}: a fairly ordinary sentence about the day and the weather"))
            .ToList();

        var breakdown = ContextTokenBreakdown.Measure(
            Context(F("user", "lives in", "Zurich"), F("user", "works at", "Acme")),
            history, Counter);

        breakdown.ContextTokens.Should().BeLessThan(breakdown.FullHistoryTokens);
        breakdown.CompressionRatio.Should().BeGreaterThan(10);
    }

    [Fact]
    public void TheCountIsRealRatherThanEstimated()
    {
        // The whole reason C4 had to land before this could be published.
        var breakdown = ContextTokenBreakdown.Measure(
            Context(F("user", "lives in", "Zurich")), [M("hello")], Counter);

        breakdown.CountMethod.Should().NotBe(TokenCountMethodNameForEstimate);
        breakdown.Encoding.Should().NotBeNullOrWhiteSpace();
    }

    private const string TokenCountMethodNameForEstimate = "Estimated";

    [Fact]
    public void SectionsAreOrderedByCost()
    {
        // A breakdown exists to show where the budget goes; a fixed section order buries that.
        var breakdown = ContextTokenBreakdown.Measure(
            Context(F("user", "lives in", "a rather long place name indeed for testing purposes")),
            [M("hello")], Counter);

        breakdown.Sections.Should().BeInDescendingOrder(s => s.Tokens);
        breakdown.Sections.First().Section.Should().Be("RelevantFacts");
    }

    [Fact]
    public void EverySectionIsAccountedForEvenWhenEmpty()
    {
        // An omitted empty section reads as "this kind of memory does not exist" rather than "it
        // contributed nothing here", and the two invite different conclusions about the design.
        var breakdown = ContextTokenBreakdown.Measure(Context(), [M("hello")], Counter);

        breakdown.Sections.Should().HaveCount(6);
        breakdown.ContextTokens.Should().Be(0);
    }

    [Fact]
    public void NoHistoryMeansNoRatioRatherThanAFlatteringOne()
    {
        // An empty denominator is not a ratio of infinity, it is an absent measurement -- and it would
        // be the single most flattering way to be wrong.
        ContextTokenBreakdown.Measure(Context(F("user", "lives in", "Zurich")), [], Counter)
            .CompressionRatio.Should().BeNull();
    }

    [Fact]
    public void AnEmptyContextAgainstRealHistoryDoesNotDivideByZero()
    {
        var breakdown = ContextTokenBreakdown.Measure(Context(), [M("a real turn of conversation")], Counter);

        breakdown.CompressionRatio.Should().NotBeNull();
        breakdown.CompressionRatio.Should().BePositive();
    }
}
