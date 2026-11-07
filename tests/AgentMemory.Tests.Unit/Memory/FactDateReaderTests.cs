using AgentMemory.Abstractions.Domain;
using AgentMemory.Core.Memory;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Memory;

/// <summary>
/// The dates a fact's words state, read into its date fields (<c>LlmExtractionOptions.DatesFromText</c>). The facts are
/// the ones the store-aware writer wrote for the Bruna scenario, word for word, which kept every date in the text.
/// </summary>
public sealed class FactDateReaderTests
{
    private static DateTimeOffset On(int year, int month, int day) => new(year, month, day, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset EndOf(int year, int month, int day) => new(year, month, day, 23, 59, 59, TimeSpan.Zero);

    private static ExtractedFact Fact(string subject, string predicate, string @object) =>
        new() { Subject = subject, Predicate = predicate, Object = @object };

    [Fact]
    public void AnEventsDayIsItsDate()
    {
        var fact = FactDateReader.Apply(
            Fact("Bruna", "is leading the go-live of", "Acme Logistics' new billing system on Friday, December 11, on-site at Acme's office in Córdoba, in person"),
            said: On(2026, 9, 7));

        fact.OccurredOn.Should().Be(On(2026, 12, 11));
        fact.OccurredOnPrecision.Should().Be(DatePrecision.Day);
        fact.ValidFrom.Should().BeNull();
        fact.Object.Should().Contain("on Friday, December 11", "the words stay as written");
    }

    [Fact]
    public void ARangeIsAStay()
    {
        var fact = FactDateReader.Apply(Fact("Bruna", "will be in Bariloche", "from December 9 to December 13"), said: On(2026, 11, 12));

        fact.ValidFrom.Should().Be(On(2026, 12, 9));
        fact.ValidUntil.Should().Be(EndOf(2026, 12, 13));
        fact.OccurredOn.Should().BeNull();
    }

    [Fact]
    public void AnUntilTheWriterSetIsKept()
    {
        var written = Fact("Bruna", "will be in Bariloche", "from December 9 to December 13") with
        {
            ValidUntil = new DateTimeOffset(2026, 12, 13, 23, 59, 59, TimeSpan.Zero),
            ValidUntilPrecision = DatePrecision.Day,
        };

        var fact = FactDateReader.Apply(written, said: On(2026, 11, 12));

        fact.ValidFrom.Should().Be(On(2026, 12, 9));
        fact.ValidUntil.Should().Be(written.ValidUntil);
    }

    [Theory]
    [InlineData("Lucía", "is getting married in", "Bariloche on Saturday, December 12", 2026, 12, 12)]
    [InlineData("Bruna", "scheduled the training session for", "December 3, remote, two hours", 2026, 12, 3)]
    [InlineData("The load test for the billing system", "is scheduled for", "November 18", 2026, 11, 18)]
    [InlineData("The go-live", "is on", "2026-12-11", 2026, 12, 11)]
    [InlineData("The demo", "is on", "the 3rd of December", 2026, 12, 3)]
    public void OneStatedDateIsTheDayItHappens(string subject, string predicate, string @object, int year, int month, int day) =>
        FactDateReader.Apply(Fact(subject, predicate, @object), said: On(2026, 11, 9)).OccurredOn.Should().Be(On(year, month, day));

    [Fact]
    public void StartingIsAStart()
    {
        var fact = FactDateReader.Apply(Fact("The billing system", "has a code freeze starting", "December 4"), said: On(2026, 11, 9));

        fact.ValidFrom.Should().Be(On(2026, 12, 4));
        fact.OccurredOn.Should().BeNull();
    }

    [Fact]
    public void UntilIsAnEnd() =>
        FactDateReader.Apply(Fact("Valentina", "is on leave", "until December 13"), said: On(2026, 11, 9))
            .ValidUntil.Should().Be(EndOf(2026, 12, 13));

    [Fact]
    public void BetweenAndIsARange() =>
        FactDateReader.Apply(Fact("Bruna", "is away", "between December 7 and 13"), said: On(2026, 11, 9))
            .ValidFrom.Should().Be(On(2026, 12, 7));

    [Fact]
    public void AWeekdayNamedWithTheDateDecidesTheYear() =>
        // Said on 2 January 2027: the nearest December 11 that is a Friday is 2026's (2027's is a Saturday).
        FactDateReader.Apply(Fact("The go-live", "was on", "Friday, December 11"), said: On(2027, 1, 2))
            .OccurredOn.Should().Be(On(2026, 12, 11));

    // ── what must stay undated ───────────────────────────────────────

    [Theory]
    [InlineData("The load test", "passed with", "1,200 invoices per minute, above the 1,000 target")]
    [InlineData("Bruna", "wants", "the go-live day to start at 7 am with a short kickoff call")]
    [InlineData("Priya", "may run", "3 more tests")]
    [InlineData("Bruna", "has", "the training on December 3 and the freeze on December 4")]
    [InlineData("Bruna", "meets Diego", "tomorrow")]
    [InlineData("Our sprint reviews", "are", "every other Thursday at 4 pm")]
    [InlineData("The deadline", "is", "December 32")]
    public void WhatStatesNoSingleDateStaysUndated(string subject, string predicate, string @object)
    {
        var fact = FactDateReader.Apply(Fact(subject, predicate, @object), said: On(2026, 11, 9));

        fact.OccurredOn.Should().BeNull();
        fact.ValidFrom.Should().BeNull();
        fact.ValidUntil.Should().BeNull();
    }

    [Fact]
    public void AFactAlreadyDatedIsLeftAlone()
    {
        var dated = Fact("The go-live", "is on", "December 11") with { OccurredOn = On(2026, 12, 10) };

        FactDateReader.Apply(dated, said: On(2026, 11, 9)).Should().BeSameAs(dated);
    }

    // ── review round 1 ───────────────────────────────────────────────

    [Fact]
    public void ARangeNextToAnotherDateIsTwoThingsAndStaysUndated()
    {
        var fact = FactDateReader.Apply(
            Fact("Bruna", "has", "the wedding on December 12, and the trip from December 9 to December 13"), said: On(2026, 11, 12));

        fact.ValidFrom.Should().BeNull();
        fact.ValidUntil.Should().BeNull();
        fact.OccurredOn.Should().BeNull();
    }

    [Fact]
    public void AYearAfterARangeIsItsEndsYear()
    {
        var fact = FactDateReader.Apply(Fact("Bruna", "is away", "from December 28 to January 3, 2027"), said: On(2026, 11, 9));

        fact.ValidFrom.Should().Be(On(2026, 12, 28));
        fact.ValidUntil.Should().Be(EndOf(2027, 1, 3));
    }

    [Fact]
    public void AnIsoRangeIsAStay()
    {
        var fact = FactDateReader.Apply(Fact("Bruna", "will be in Bariloche", "from 2026-12-09 to 2026-12-13"), said: On(2026, 11, 12));

        fact.ValidFrom.Should().Be(On(2026, 12, 9));
        fact.ValidUntil.Should().Be(EndOf(2026, 12, 13));
    }
}
