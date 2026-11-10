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

/// <summary>The reported Bruna failure, isolated from extraction and any model call.</summary>
public sealed class TemporalIdentityRegressionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 0, 0, 0, TimeSpan.Zero);

    private static Entity Existing(string name, params string[] aliases) => new()
    {
        EntityId = "training-date", Name = name, Type = "OBJECT", Confidence = 1,
        CreatedAtUtc = Now, Aliases = aliases, Embedding = [1, 0, 0, 0]
    };

    [Theory]
    [InlineData("12 december", "3 december")]
    [InlineData("4 december", "3 december")]
    [InlineData("12 december", "13 december")]
    public async Task A_fuzzy_score_cannot_establish_the_identity_of_two_dates(string mention, string stored)
    {
        var matcher = new FuzzyMatchEntityMatcher(new EntityResolutionOptions());
        var result = await matcher.TryMatchAsync(new ExtractedEntity { Name = mention, Type = "OBJECT" }, [Existing(stored)]);
        result.Should().BeNull("date values must not be identified by their spelling similarity");
    }

    [Theory]
    [InlineData("December 12", "December 3")]
    [InlineData("December 4", "December 3")]
    [InlineData("December 12", "December 12")]
    [InlineData("December 12", "2026-12-12")]
    public async Task An_incomplete_date_is_not_assigned_an_existing_identity_by_similarity_or_name(string mention, string stored)
    {
        var (resolver, _) = Resolver(Existing(stored));
        var result = await resolver.ResolveEntityAsync(new ExtractedEntity { Name = mention, Type = "OBJECT" }, ["wedding-message"]);
        result.EntityId.Should().Be("new-date");
        result.Name.Should().Be(mention);
    }

    [Fact]
    public async Task An_old_incorrect_alias_cannot_make_the_twelfth_become_the_third()
    {
        var (resolver, _) = Resolver(Existing("2026-12-03", "2026-12-12"));
        var result = await resolver.ResolveEntityAsync(new ExtractedEntity { Name = "2026-12-12", Type = "OBJECT" }, ["wedding-message"]);
        result.EntityId.Should().Be("new-date");
        result.Name.Should().Be("2026-12-12");
    }

    [Fact]
    public async Task Equivalent_complete_dates_are_compared_as_values()
    {
        var (resolver, embeddings) = Resolver(Existing("12 December 2026"));
        var result = await resolver.ResolveEntityAsync(new ExtractedEntity { Name = "2026-12-12", Type = "OBJECT" }, ["wedding-message"]);
        result.EntityId.Should().Be("training-date");
        await embeddings.DidNotReceive().EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("December 12", "December 3")]
    [InlineData("2026-12-12", "2026-12-03")]
    public void Canonical_names_cannot_overwrite_a_temporal_fact_value(string extracted, string canonical)
    {
        var names = new FactNames(new ExtractionOptions { CanonicalFactSubjects = true },
            new Dictionary<string, Entity> { [extracted] = Existing(canonical) }, null);
        var fact = new ExtractedFact { Subject = "Lucía's wedding", Predicate = "is on", Object = extracted };
        names.StoredName(fact, subject: false).Should().Be(extracted);
    }

    [Fact]
    public void The_calendar_fix_does_not_disable_existing_person_name_resolution()
    {
        var entity = Existing("Priya Nair") with { Type = "PERSON" };
        var names = new FactNames(new ExtractionOptions { CanonicalFactSubjects = true },
            new Dictionary<string, Entity> { ["Priya"] = entity }, null);
        names.StoredName(new ExtractedFact { Subject = "Priya", Predicate = "works at", Object = "Acme" }, true)
            .Should().Be("Priya Nair");
    }

    private static (CompositeEntityResolver Resolver, IEmbeddingOrchestrator Embeddings) Resolver(Entity entity)
    {
        var repository = Substitute.For<IEntityRepository>();
        repository.GetByTypeAsync("OBJECT", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>([entity]));
        repository.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>()).Returns(call => Task.FromResult(call.Arg<Entity>()));
        var embeddings = Substitute.For<IEmbeddingOrchestrator>();
        embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(new float[] { 1, 0, 0, 0 }));
        var time = Substitute.For<TimeProvider>();
        time.GetUtcNow().Returns(Now);
        var ids = Substitute.For<IIdGenerator>();
        ids.GenerateId().Returns("new-date");
        return (new CompositeEntityResolver(repository, embeddings, Options.Create(new ExtractionOptions()), time, ids,
            NullLogger<CompositeEntityResolver>.Instance), embeddings);
    }
}
