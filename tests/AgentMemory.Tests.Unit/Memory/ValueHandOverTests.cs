using AgentMemory.Abstractions.Domain;
using AgentMemory.Core.Memory;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Memory;

/// <summary>
/// J-6 (b, c): a plan that has begun takes over from the value it was planned to replace, and starts are compared at the
/// precision they were said. Found by review: "I'm moving to Oslo next month" left Copenhagen and Oslo both current once
/// the month came, and "in August" read as 1 August, before "on 20 August", though it may well be after.
/// </summary>
public sealed class ValueHandOverTests
{
    private static readonly DateTimeOffset SaidCopenhagen = new(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SaidOsloPlan = new(2026, 9, 10, 10, 0, 0, TimeSpan.Zero);

    private static Fact F(string id, string predicate, string @object, DateTimeOffset said,
        DateTimeOffset? from = null, DatePrecision precision = DatePrecision.Day, string owner = "u1") => new()
    {
        FactId = id, Subject = "Oskar", Predicate = predicate, Object = @object, Confidence = 0.9,
        CreatedAtUtc = said, ValidFrom = from, ValidFromPrecision = precision, OwnerId = owner,
    };

    private static readonly Fact Copenhagen = F("cph", "lives in", "Copenhagen", SaidCopenhagen);
    private static readonly Fact OsloFromOctober = F("osl", "lives in", "Oslo", SaidOsloPlan, new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), DatePrecision.Month);

    [Fact]
    public void A_plan_that_has_begun_takes_over() =>
        ValueHandOver.Current([Copenhagen, OsloFromOctober], new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero))
            .Select(f => f.FactId).Should().Equal("osl");

    [Fact]
    public void A_plan_still_ahead_replaces_nothing_yet() =>
        ValueHandOver.Current([Copenhagen, OsloFromOctober], new(2026, 9, 20, 0, 0, 0, TimeSpan.Zero))
            .Select(f => f.FactId).Should().Equal("cph", "osl");

    [Fact]
    public void As_of_before_the_plan_began_the_old_value_is_current() =>
        ValueHandOver.Current([Copenhagen, OsloFromOctober], new(2026, 9, 30, 23, 0, 0, TimeSpan.Zero))
            .Should().HaveCount(2, "on 30 September the October plan has not begun");

    [Theory]
    [InlineData(true, "lisbon")]    // "in August" said after "on 20 August": the later word stands
    [InlineData(false, "porto")]    // said before it: the dated day is the newer value
    public void Starts_that_overlap_at_their_precision_are_ordered_by_when_they_were_said(bool augustSaidLater, string current)
    {
        var lisbon = F("lisbon", "lives in", "Lisbon", augustSaidLater ? SaidOsloPlan : SaidCopenhagen,
            new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), DatePrecision.Month);
        var porto = F("porto", "lives in", "Porto", augustSaidLater ? SaidCopenhagen : SaidOsloPlan,
            new(2026, 8, 20, 0, 0, 0, TimeSpan.Zero), DatePrecision.Day);

        ValueHandOver.Current([lisbon, porto], new(2026, 9, 25, 0, 0, 0, TimeSpan.Zero))
            .Select(f => f.FactId).Should().Equal(current);
    }

    [Fact]
    public void Starts_apart_at_their_precision_are_ordered_by_date_whatever_was_said_last()
    {
        var madrid = F("madrid", "lives in", "Madrid", SaidOsloPlan, new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), DatePrecision.Year);
        var paris = F("paris", "lives in", "Paris", SaidCopenhagen, new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), DatePrecision.Month);

        ValueHandOver.Current([madrid, paris], new(2026, 9, 25, 0, 0, 0, TimeSpan.Zero))
            .Select(f => f.FactId).Should().ContainSingle().Which.Should().Be("paris", "2025 is wholly before March 2026, though Madrid was said later");
    }

    [Fact]
    public void Multi_valued_relations_and_other_owners_are_left_alone()
    {
        var likesJazz = F("jazz", "likes", "jazz", SaidCopenhagen);
        var likesHiking = F("hike", "likes", "hiking", SaidOsloPlan);
        var someoneElses = OsloFromOctober with { FactId = "osl-2", OwnerId = "u2" };

        ValueHandOver.Current([likesJazz, likesHiking, Copenhagen, someoneElses], new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero))
            .Should().HaveCount(4);
    }
}
