using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;
using AgentMemory.Core.Resolution;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Resolution;

public sealed class TemporalIntervalRegressionTests
{
    private static Entity Stored(string text, params string[] aliases) => new()
    { EntityId = "stored", Name = text, Type = "OBJECT", Aliases = aliases,
        CreatedAtUtc = DateTimeOffset.UnixEpoch, Confidence = 1, Embedding = [1, 0] };

    private static (CompositeEntityResolver Resolver, IEmbeddingOrchestrator Embeddings) Resolver(Entity existing, bool stringsOnly = false)
    {
        var repository = Substitute.For<IEntityRepository>();
        repository.GetByTypeAsync("OBJECT", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>()).Returns(new[] { existing });
        repository.GetByIdAsync(existing.EntityId, Arg.Any<CancellationToken>()).Returns(existing);
        repository.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<Entity>());
        var embeddings = Substitute.For<IEmbeddingOrchestrator>();
        embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 1, 0 });
        var ids = Substitute.For<IIdGenerator>(); ids.GenerateId().Returns("new-interval");
        var options = new ExtractionOptions();
        if (stringsOnly) { options.EntityResolution.EnableSemanticMatch = false; options.EntityResolution.EnableFuzzyMatch = false; }
        return (new(repository, embeddings, Options.Create(options), TimeProvider.System, ids,
            NullLogger<CompositeEntityResolver>.Instance), embeddings);
    }

    [Theory]
    [InlineData("December 9–13, 2026", "December 9–12, 2026")]
    [InlineData("9–13 diciembre 2026", "9–12 diciembre 2026")]
    [InlineData("2026-12-09/2026-12-13", "2026-12-09/2026-12-12")]
    [InlineData("December 9–13", "December 9–13")]
    [InlineData("from December 9 to December 13", "from December 9 to December 13")]
    [InlineData("December 13–9, 2026", "December 9–13, 2026")]
    [InlineData("February 28–30, 2026", "February 28–29, 2026")]
    [InlineData("2026-12-12/2026-12-12", "2026-12-12")]
    [InlineData("December 9, 2026 to December 13, 2026", "December 9, 2026 to December 12, 2026")]
    [InlineData("between 9 and 13 December 2026", "between 9 and 12 December 2026")]
    public async Task Different_invalid_and_incomplete_intervals_cannot_become_an_existing_identity(string mention, string existing)
    {
        var (resolver, embeddings) = Resolver(Stored(existing));
        var result = await resolver.ResolveEntityAsync(new ExtractedEntity { Name = mention, Type = "OBJECT" }, []);
        result.EntityId.Should().Be("new-interval");
        result.Name.Should().Be(mention);
        await embeddings.DidNotReceive().EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("2026-12-09/2026-12-13", "December 9–13, 2026")]
    [InlineData("9–13 diciembre 2026", "December 9–13, 2026")]
    [InlineData("from December 9, 2026 to December 13, 2026", "2026-12-09/2026-12-13")]
    [InlineData("December 30, 2026 through January 2, 2027", "2026-12-30/2027-01-02")]
    [InlineData("2028-02-28/2028-03-01", "February 28, 2028 to March 1, 2028")]
    [InlineData("between 9 and 13 December 2026", "2026-12-09/2026-12-13")]
    public async Task Complete_equal_intervals_use_both_calendar_endpoints_without_name_matching(string mention, string existing)
    {
        var (resolver, embeddings) = Resolver(Stored(existing), stringsOnly: true);
        var result = await resolver.ResolveEntityAsync(new ExtractedEntity { Name = mention, Type = "OBJECT" }, []);
        result.EntityId.Should().Be("stored");
        await embeddings.DidNotReceive().EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("December 9–13, 2026", "December 9–12, 2026")]
    [InlineData("December 9–13", "December 9–12")]
    [InlineData("2026-12-09/2026-12-13", "2026-12-09/2026-12-12")]
    public void Canonical_names_cannot_replace_a_calendar_interval(string surface, string wrong)
    {
        var names = new FactNames(new ExtractionOptions { CanonicalFactSubjects = true },
            new Dictionary<string, Entity> { [surface] = Stored(wrong) }, null);
        names.CanonicalName(surface).Should().Be(surface);
        CalendarValues.CanLinkSurface(surface, Stored(wrong)).Should().BeFalse();
    }

    [Fact]
    public async Task A_bad_interval_alias_does_not_authorize_identity()
    {
        var (resolver, _) = Resolver(Stored("December 9–12, 2026", "December 9–13, 2026"));
        var result = await resolver.ResolveEntityAsync(new ExtractedEntity { Name = "December 9–13, 2026", Type = "OBJECT" }, []);
        result.EntityId.Should().Be("new-interval");
    }

    [Fact]
    public void A_range_word_inside_an_event_name_does_not_make_it_a_literal()
    {
        CalendarValues.TryRead("December 9–13 conference", "EVENT", out _).Should().BeFalse();
        CalendarValues.TryRead("December 9–13 Edition", "OBJECT", out _).Should().BeFalse();
    }
}
