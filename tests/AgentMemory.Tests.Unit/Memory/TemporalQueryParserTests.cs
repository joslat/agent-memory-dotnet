using AgentMemory.Core.Memory;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Memory;

/// <summary>
/// Finding the point in time a question asks about (R4) — and, far more importantly, not finding one.
/// </summary>
/// <remarks>
/// <para>
/// <c>RecallAsOfAsync</c> has existed since the bitemporal work and nothing in an ordinary conversation
/// could reach it: "what did I think back in March?" recalled against now, exactly like every other
/// question.
/// </para>
/// <para>
/// <b>The failure modes are wildly asymmetric, so the tests are too.</b> A missed expression costs
/// nothing — the turn recalls against now, which is today's behaviour. A false positive silently
/// narrows recall to a window the user never asked about, and the answer that comes back looks
/// completely ordinary. Most of what follows is therefore about the phrases that must NOT parse.
/// </para>
/// </remarks>
public sealed class TemporalQueryParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset? Resolve(string query) => TemporalQueryParser.Resolve(query, Now);

    // ── what must NOT parse ───────────────────────────────────────────────

    [Theory]
    [InlineData("what was the last item I bought")]
    [InlineData("give me the last one")]
    [InlineData("who spoke last")]
    [InlineData("the last thing you said")]
    public void OrdinalLastIsNotTemporal(string query)
    {
        // "last" alone is far more often ordinal than temporal. Treating these as dates would rewrite
        // the recall window of perfectly ordinary questions, and the answer would look normal.
        Resolve(query).Should().BeNull();
    }

    [Theory]
    [InlineData("what did March say about the contract")]
    [InlineData("is April coming to the meeting")]
    [InlineData("that may be the problem")]
    [InlineData("May I ask about the budget")]
    public void ABareMonthWordIsNotADate(string query)
    {
        // March and April are common names; "may" is a verb in nearly every sentence containing it.
        // The preposition is what separates a date from a word that looks like one.
        Resolve(query).Should().BeNull();
    }

    [Theory]
    [InlineData("we shipped in 2024 units of stock")]
    [InlineData("the order was for 2019 items")]
    public void ANumberThatIsAQuantityIsNotAYear(string query)
    {
        Resolve(query).Should().BeNull();
    }

    [Theory]
    [InlineData("what do I like")]
    [InlineData("who is my manager")]
    [InlineData("")]
    [InlineData("   ")]
    public void AnOrdinaryQuestionResolvesToNow(string query)
    {
        // Null means "recall as the system behaves today" -- the overwhelmingly common answer and the
        // safe one.
        Resolve(query).Should().BeNull();
    }

    [Fact]
    public void AFutureDateIsNotAnAsOfInstant()
    {
        // As-of recall reconstructs what was known at a PAST moment. Pointed at the future it returns
        // everything, which is indistinguishable from ordinary recall except that it silently ignored
        // the question.
        Resolve("what is planned in 2030").Should().BeNull();
    }

    [Fact]
    public void FutureTenseIsNotDetected_AndThatIsTheAcceptedLimit()
    {
        // "what will change in December", asked in January, resolves to LAST December. The parser
        // reads dates, not tense, and a bare month behind a preposition is genuinely ambiguous -- it
        // most often does mean the most recent one.
        //
        // Recorded rather than fixed: tense detection is a substantially harder problem, and the cost
        // of getting THIS wrong is asymmetric in the tolerable direction. The question is answered
        // against last December's memory instead of all of it, which is a narrower answer to a
        // question about the future -- not a wrong answer to a question about the past.
        TemporalQueryParser.Resolve(
                "what will change in December", new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero))
            .Should().Be(new DateTimeOffset(2025, 12, 31, 23, 59, 59, TimeSpan.Zero));
    }

    // ── what must parse ───────────────────────────────────────────────────

    [Fact]
    public void Yesterday() =>
        Resolve("what did I say yesterday").Should().Be(Now.AddDays(-1));

    [Theory]
    [InlineData("what did I think last week", -7)]
    [InlineData("what was true 3 days ago", -3)]
    [InlineData("in the past 10 days what changed", -10)]
    public void RelativeDayExpressions(string query, int days) =>
        Resolve(query).Should().Be(Now.AddDays(days));

    [Fact]
    public void LastQuarterIsThreeMonths() =>
        Resolve("what were my priorities last quarter").Should().Be(Now.AddMonths(-3));

    [Fact]
    public void LastYear() =>
        Resolve("where did I live last year").Should().Be(Now.AddYears(-1));

    [Fact]
    public void AMonthNameBehindAPrepositionResolvesToTheEndOfThatMonth()
    {
        // The END of the month, not its first instant: "back in March" means anything known by the
        // close of March, and an as-of at 1 March would exclude the entire month being asked about.
        Resolve("what did I think back in March")
            .Should().Be(new DateTimeOffset(2026, 3, 31, 23, 59, 59, TimeSpan.Zero));
    }

    [Fact]
    public void AMonthLaterInTheYearResolvesToLastYear()
    {
        // Asked in August, "in December" cannot mean this year's December -- that has not happened.
        Resolve("what was the plan in December")
            .Should().Be(new DateTimeOffset(2025, 12, 31, 23, 59, 59, TimeSpan.Zero));
    }

    [Fact]
    public void AnExplicitMonthAndYearBeatsTheInference() =>
        Resolve("what did we decide in March 2024")
            .Should().Be(new DateTimeOffset(2024, 3, 31, 23, 59, 59, TimeSpan.Zero));

    [Fact]
    public void AYearBehindAPrepositionResolvesToItsEnd() =>
        Resolve("what was true in 2023")
            .Should().Be(new DateTimeOffset(2023, 12, 31, 23, 59, 59, TimeSpan.Zero));

    // ── shape ─────────────────────────────────────────────────────────────

    [Fact]
    public void TheReferenceInstantsOffsetIsPreserved()
    {
        // A host in a non-UTC offset must not have its temporal questions silently shifted by hours.
        var offset = TimeSpan.FromHours(2);
        var now = new DateTimeOffset(2026, 8, 12, 12, 0, 0, offset);

        TemporalQueryParser.Resolve("what did we decide in March 2024", now)!
            .Value.Offset.Should().Be(offset);
    }

    [Fact]
    public void CasingDoesNotMatter() =>
        Resolve("What Did I Think Back In March").Should().NotBeNull();

    [Fact]
    public void ALongQueryDoesNotHangTheParser()
    {
        // Every pattern carries a 100 ms timeout: these run on the recall path, and a pathological
        // input must degrade to "no temporal reference" rather than stall a turn.
        var haystack = string.Join(' ', Enumerable.Repeat("last night we discussed many things", 500));

        var act = () => Resolve(haystack);

        act.Should().NotThrow();
    }
}
