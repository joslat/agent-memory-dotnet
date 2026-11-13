using System.Globalization;
using AgentMemory.Core.Resolution;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Resolution;

public sealed class CalendarValuesTests
{
    [Theory]
    [InlineData("2026-12-12", 2026, 12, 12)]
    [InlineData("12 December 2026", 2026, 12, 12)]
    [InlineData("December 12, 2026", 2026, 12, 12)]
    [InlineData("12 diciembre 2026", 2026, 12, 12)]
    [InlineData("Saturday, December 12, 2026", 2026, 12, 12)]
    [InlineData("  2028-02-29  ", 2028, 2, 29)]
    [InlineData("December 12", null, 12, 12)]
    [InlineData("12 December", null, 12, 12)]
    [InlineData("2026-12", 2026, 12, null)]
    [InlineData("December 2026", 2026, 12, null)]
    public void Reads_calendar_components_without_supplying_missing_precision(string text, int? year, int month, int? day)
    {
        CalendarValues.TryRead(text, "OBJECT", out var value).Should().BeTrue();
        value.Should().NotBeNull();
        value!.Year.Should().Be(year);
        value.Month.Should().Be(month);
        value.Day.Should().Be(day);
        value.IsComplete.Should().Be(year.HasValue && day.HasValue);
    }

    [Theory]
    [InlineData("2026-02-29")]
    [InlineData("2026-13-01")]
    [InlineData("2026-00-12")]
    [InlineData("2026-12-00")]
    [InlineData("0000-12-12")]
    [InlineData("February 30, 2026")]
    [InlineData("December 32, 2026")]
    [InlineData("Monday, December 12, 2026")]
    [InlineData("01/02/2026")]
    [InlineData("2026-12-12T10:00:00Z")]
    public void Invalid_ambiguous_or_unsupported_temporal_shapes_are_not_name_candidates(string text)
    {
        CalendarValues.TryRead(text, "OBJECT", out var value).Should().BeTrue();
        value.Should().BeNull("recognition does not authorise guessing a corrected date or an instant");
    }

    [Theory]
    [InlineData("May", "PERSON")]
    [InlineData("December 12 Edition", "OBJECT")]
    [InlineData("Release on December 12", "EVENT")]
    [InlineData("Room 101", "OBJECT")]
    [InlineData("2026", "OBJECT")]
    [InlineData("", "OBJECT")]
    public void Does_not_find_dates_inside_names_or_invent_a_type(string text, string type)
    {
        CalendarValues.TryRead(text, type, out var value).Should().BeFalse();
        value.Should().BeNull();
    }

    [Fact]
    public void An_explicit_date_type_cannot_fall_through_to_name_similarity()
    {
        CalendarValues.TryRead("next holiday", "DATE", out var value).Should().BeTrue();
        value.Should().BeNull();
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("es-AR")]
    [InlineData("de-CH")]
    public void Calendar_equality_does_not_depend_on_process_culture(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            CalendarValues.TryRead("2026-12-12", "OBJECT", out var left).Should().BeTrue();
            CalendarValues.TryRead("12 diciembre 2026", "OBJECT", out var right).Should().BeTrue();
            left.Should().Be(right);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Partial_values_remain_partial_independently_of_the_current_year()
    {
        CalendarValues.TryRead("December 12", "OBJECT", out var value).Should().BeTrue();
        value!.Year.Should().BeNull();
        value.IsComplete.Should().BeFalse();
    }
}
