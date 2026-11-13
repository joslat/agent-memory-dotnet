using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Core.Extraction;
using AgentMemory.Core.Resolution;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Resolution;

public sealed class CalendarWhitespaceRegressionTests
{
    private static string Padded(string text) => new string(' ', 128) + text + new string('\u00a0', 128);

    [Theory]
    [InlineData("2026-12-12", "gregorian:2026-12-12")]
    [InlineData("December 12, 2026", "gregorian:2026-12-12")]
    [InlineData("12 diciembre 2026", "gregorian:2026-12-12")]
    [InlineData("December 9-13, 2026", "gregorian-range:2026-12-09/2026-12-13")]
    [InlineData("December 12", null)]
    public void Surrounding_whitespace_cannot_turn_a_calendar_value_into_a_reference(string text, string? key)
    {
        CalendarValues.TryRead(Padded(text), "OBJECT", out var value).Should().BeTrue();
        value.Should().NotBeNull();
        value!.IsComplete.Should().Be(key is not null);
        if (key is not null) value.CanonicalKey.Should().Be(key);
        else value.Year.Should().BeNull();
    }

    [Fact]
    public async Task Two_padded_dates_cannot_acquire_identity_from_token_sort_similarity()
    {
        var matcher = new FuzzyMatchEntityMatcher(new EntityResolutionOptions());
        var entity = DateEntity(Padded("December 3"));
        var result = await matcher.TryMatchAsync(new ExtractedEntity { Name = Padded("December 12"), Type = "OBJECT" }, [entity]);
        result.Should().BeNull("padding cannot make December 12 the same identity as December 3");
    }

    [Fact]
    public void A_padded_fact_literal_keeps_its_exact_words_despite_an_incompatible_resolver()
    {
        var surface = Padded("December 12");
        var names = new FactNames(new ExtractionOptions { CanonicalFactSubjects = true },
            new Dictionary<string, Entity> { [surface] = DateEntity("December 3") }, null);
        var fact = new ExtractedFact { Subject = "wedding", Predicate = "is on", Object = surface };
        names.StoredName(fact, subject: false).Should().Be(surface);
    }

    [Fact]
    public void Padding_does_not_turn_a_product_name_into_a_date()
    {
        CalendarValues.TryRead(Padded("December 12 Edition"), "OBJECT", out var value).Should().BeFalse();
        value.Should().BeNull();
    }

    [Fact]
    public void Rejecting_an_oversize_reference_does_not_allocate_its_trimmed_copy()
    {
        var text = " " + new string('x', 1_000_000) + " ";
        _ = CalendarValues.TryRead("warm", "OBJECT", out _);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var recognized = CalendarValues.TryRead(text, "OBJECT", out var value);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        recognized.Should().BeFalse();
        value.Should().BeNull();
        allocated.Should().BeLessThan(65_536, "normalization must not materialize an unbounded rejected value");
    }

    private static Entity DateEntity(string name) => new()
    {
        EntityId = "training", Name = name, Type = "OBJECT", Confidence = 1,
        CreatedAtUtc = DateTimeOffset.UnixEpoch
    };
}
