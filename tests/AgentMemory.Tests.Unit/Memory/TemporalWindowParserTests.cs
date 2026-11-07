using AgentMemory.Core.Memory;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Memory;

/// <summary>
/// Finding the period a question asks about (<c>RecallOptions.TimeWindows</c>), and whether it looks ahead
/// (<c>RecallOptions.Upcoming</c>). The questions are Bruna's, from the go-live scenario, asked on Friday 4 December.
/// </summary>
/// <remarks>
/// The same asymmetry as <see cref="TemporalQueryParserTests"/>: a missed window costs nothing, the question recalls as
/// it always did; a wrong one adds dated facts the person did not ask about. So the phrases that must NOT parse get
/// as many tests as the ones that must.
/// </remarks>
public sealed class TemporalWindowParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 12, 4, 9, 0, 0, TimeSpan.Zero);

    private static TemporalWindowQuery? Resolve(string query) => TemporalWindowParser.Resolve(query, Now);

    private static DateTimeOffset Day(int month, int day, int year = 2026) => new(year, month, day, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset EndOf(int month, int day, int year = 2026) => Day(month, day, year).AddDays(1).AddTicks(-1);

    // ── windows the question names ────────────────────────────────────

    [Theory]
    [InlineData("What do I have on my calendar between December 7 and December 13?")]
    [InlineData("What do I have between December 7 and 13?")]
    [InlineData("anything from Dec 7 to Dec 13")]
    [InlineData("between 7 and 13 December")]
    [InlineData("from the 7th to the 13th of December")]
    public void ARangeIsItsTwoDaysWhole(string query)
    {
        var window = Resolve(query);

        window.Should().NotBeNull();
        window!.Start.Should().Be(Day(12, 7));
        window.End.Should().Be(EndOf(12, 13));
        window.Anchor.Should().BeNull();
    }

    [Fact]
    public void ARangeAcrossNewYearEndsInTheNextYear()
    {
        var window = Resolve("what do I have from December 28 to January 3?");

        window!.Start.Should().Be(Day(12, 28));
        window.End.Should().Be(EndOf(1, 3, 2027));
    }

    [Fact]
    public void AYearlessDateIsTheOccurrenceNearestToNow()
    {
        // Asked on 4 December 2026: January is next month, not last January.
        Resolve("what is on January 5?")!.Start.Should().Be(Day(1, 5, 2027));
        Resolve("what was on November 30?")!.Start.Should().Be(Day(11, 30));
    }

    [Theory]
    [InlineData("What happens on December 11?")]
    [InlineData("what happens on Friday, December 11, 2026")]
    [InlineData("anything on the 11th of December?")]
    [InlineData("anything on 11 December?")]
    public void OnADayIsThatDay(string query)
    {
        var window = Resolve(query);

        window!.Start.Should().Be(Day(12, 11));
        window.End.Should().Be(EndOf(12, 11));
    }

    [Fact]
    public void ThisWeekIsWhatIsLeftOfIt()
    {
        // Friday 4 December: the rest of this Monday-to-Sunday week, from today.
        var window = Resolve("what do I have this week?");

        window!.Start.Should().Be(Day(12, 4));
        window.End.Should().Be(EndOf(12, 6));
    }

    [Fact]
    public void NextWeekIsTheWholeNextWeek()
    {
        var window = Resolve("what is planned next week?");

        window!.Start.Should().Be(Day(12, 7));
        window.End.Should().Be(EndOf(12, 13));
    }

    [Theory]
    [InlineData("what do I have in the next 10 days?", 10)]
    [InlineData("anything over the coming two weeks?", 14)]
    public void TheNextDaysStartToday(string query, int days)
    {
        var window = Resolve(query);

        window!.Start.Should().Be(Day(12, 4));
        window.End.Should().Be(Day(12, 4).AddDays(days + 1).AddTicks(-1));
    }

    [Fact]
    public void TomorrowIsTomorrow() =>
        Resolve("what do I have tomorrow?")!.Start.Should().Be(Day(12, 5));

    // ── windows anchored on an event ──────────────────────────────────

    [Theory]
    [InlineData("Plan my prep for go-live week.", "go-live")]
    [InlineData("what is happening in launch week", "launch")]
    [InlineData("help me get ready for the week of the wedding", "wedding")]
    public void AnEventsWeekIsAnchoredOnTheEvent(string query, string anchor)
    {
        var window = Resolve(query);

        window.Should().NotBeNull();
        window!.Anchor.Should().Be(anchor);
        window.Span.Should().Be(TemporalWindowSpan.Week);
        window.Start.Should().BeNull("the store gives an anchored window its date");
    }

    [Theory]
    [InlineData("what do I need on go-live day?", "go-live")]
    [InlineData("the day of the wedding", "wedding")]
    public void AnEventsDayIsAnchoredOnTheEvent(string query, string anchor)
    {
        var window = Resolve(query);

        window!.Anchor.Should().Be(anchor);
        window.Span.Should().Be(TemporalWindowSpan.Day);
    }

    [Fact]
    public void AnAnchoredWeekIsTheMondayToSundayWeekOfItsDate()
    {
        var window = Resolve("Plan my prep for go-live week.")!;

        var (start, end) = window.Around(Day(12, 11));   // the go-live, a Friday

        start.Should().Be(Day(12, 7));
        end.Should().Be(EndOf(12, 13));
    }

    // ── what must NOT parse ───────────────────────────────────────────

    [Theory]
    [InlineData("When is the Acme go-live, and where does it happen?")]
    [InlineData("what did I say about the rollback")]
    [InlineData("we work a four day week")]
    [InlineData("how was your day")]
    [InlineData("the work week is long")]
    [InlineData("two meetings per day")]
    [InlineData("every day I drink mate")]
    [InlineData("the last day of the sprint")]
    [InlineData("the first day of school")]
    [InlineData("between you and me")]
    [InlineData("from Monday on")]
    [InlineData("on the 32nd of December")]
    public void OrdinaryPhrasesAreNotWindows(string query) => Resolve(query).Should().BeNull();

    [Theory]
    [InlineData("what did I do last week")]
    [InlineData("where was I in March")]
    public void APastInstantIsLeftToTheAsOfParser(string query) =>
        // "last week" and "in March" ask about one past instant: TemporalQueryParser answers them, as before.
        Resolve(query).Should().BeNull();

    // ── whether a question looks ahead ────────────────────────────────

    [Theory]
    [InlineData("Plan my prep for go-live week.")]
    [InlineData("When is the Acme go-live, and where does it happen?")]
    [InlineData("what is coming up?")]
    [InlineData("what do I have this week?")]
    [InlineData("Remind me what I booked")]
    [InlineData("what will I need on Friday?")]
    public void AQuestionThatPlansOrAsksWhenLooksAhead(string query) =>
        TemporalWindowParser.LooksAhead(query).Should().BeTrue();

    [Theory]
    [InlineData("What did Tomás find in the tax rounding?")]
    [InlineData("when was the load test?")]
    [InlineData("who is my manager")]
    [InlineData("")]
    [InlineData(null)]
    public void AQuestionAboutThePastOrAboutSomeoneDoesNot(string? query) =>
        TemporalWindowParser.LooksAhead(query).Should().BeFalse();
}
