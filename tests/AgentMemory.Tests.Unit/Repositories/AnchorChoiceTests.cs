using AgentMemory.Abstractions.Domain;
using AgentMemory.Neo4j.Repositories;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Repositories;

/// <summary>Which stated date an anchored window ("go-live week", "launch week") is centred on.</summary>
public sealed class AnchorChoiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);

    private static Fact Launch(string @object, DateTimeOffset? on = null, DatePrecision precision = DatePrecision.Unspecified) => new()
    {
        FactId = Guid.NewGuid().ToString("N"), Subject = "the launch", Predicate = "is", Object = @object, Confidence = 0.9,
        CreatedAtUtc = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero), OccurredOn = on, OccurredOnPrecision = precision,
    };

    [Fact]
    public void AMonthIsNoDayToCentreAWeekOn()
    {
        // Review round 2: "the launch is in December" stored at December 1 with month precision gave a launch week.
        var december = Launch("in December", new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), DatePrecision.Month);

        Neo4jFactRepository.ChooseAnchor([december], Now).Should().BeNull();
    }

    [Fact]
    public void AMonthPrecisionFactWhoseWordsStateTheDayIsAnchoredOnThatDay()
    {
        var stated = Launch("on December 11", new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), DatePrecision.Month);

        Neo4jFactRepository.ChooseAnchor([stated], Now)!.OccurredOn.Should().Be(new DateTimeOffset(2026, 12, 11, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void ADayIsAnAnchor()
    {
        var day = Launch("on Friday", new DateTimeOffset(2026, 12, 11, 0, 0, 0, TimeSpan.Zero), DatePrecision.Day);

        Neo4jFactRepository.ChooseAnchor([day], Now)!.FactId.Should().Be(day.FactId);
    }
}
