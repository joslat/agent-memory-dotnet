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
    public void AQuestionAboutAMonthStillAheadIsNoLongerReadAsLastYear()
    {
        // This test recorded the limit that "what will change in December", asked in January, resolved to LAST
        // December, and called the cost tolerable ("a narrower answer to a question about the future"). It was not:
        // once extraction dated future facts, the narrower answer HID them ("What do I have coming up in October?"
        // answered "nothing", simulated conversations, run 5). A month still ahead this year is last year's only in a
        // past-tense question now.
        TemporalQueryParser.Resolve(
                "what will change in December", new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero))
            .Should().BeNull();
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
        Resolve("what was I doing in December")
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

    /// <summary>
    /// 36.1 (run 5): a month still ahead this year is last year's only in a past-tense question. Now is 12 August 2026.
    /// </summary>
    [Theory]
    [InlineData("What do I have coming up in October?")]
    [InlineData("What am I doing in December?")]
    [InlineData("Is anything planned in November?")]
    [InlineData("Where did I say I'd be in October?")]
    [InlineData("Had I booked anything in November?")]
    [InlineData("What was planned in October?")]
    [InlineData("What was the plan in December?")]
    public void A_question_about_a_month_still_ahead_recalls_against_now(string query)
    {
        Resolve(query).Should().BeNull();
    }

    [Fact]
    public void A_past_tense_question_about_that_month_is_about_last_year()
    {
        Resolve("What did I do in October?").Should().Be(new DateTimeOffset(2025, 10, 31, 23, 59, 59, TimeSpan.Zero));
        Resolve("Where was I back in December?").Should().Be(new DateTimeOffset(2025, 12, 31, 23, 59, 59, TimeSpan.Zero));
        Resolve("What did I think in March?").Should().Be(new DateTimeOffset(2026, 3, 31, 23, 59, 59, TimeSpan.Zero),
            "a month already past this year is unchanged");
    }

    /// <summary>Review round 6: a word that only looks ahead ("book" the noun, "next", "be") keeps a past question past.</summary>
    [Theory]
    [InlineData("What book was I reading in December?")]
    [InlineData("What did I do next in November?")]
    [InlineData("What was it like to be in Paris in December?")]
    [InlineData("Where was my booking in October last time?")]
    public void A_past_question_with_a_word_that_only_looks_ahead_is_still_about_last_year(string query) =>
        Resolve(query).Should().NotBeNull();

    [Fact]
    public void A_typographic_apostrophe_is_an_apostrophe() =>
        Resolve("Where did I say I’d go in October?").Should().BeNull();
}
