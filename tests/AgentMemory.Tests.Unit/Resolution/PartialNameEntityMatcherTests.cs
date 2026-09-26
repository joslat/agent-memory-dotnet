using System.Diagnostics;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core;
using AgentMemory.Core.Resolution;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Resolution;

/// <summary>
/// "Priya" said after "Priya Nair" was introduced must be Priya Nair, not a second person.
/// </summary>
/// <remarks>
/// Observed live: the extractor emitted "Priya" for a person the store knew as "Priya Nair". Exact
/// match failed (different strings), fuzzy failed (token-sort ratio 67 &lt; 85), semantic failed (a
/// one-word embedding), so a duplicate PERSON was created and the new facts attached to it.
/// </remarks>
public sealed class PartialNameEntityMatcherTests
{
    private static readonly DateTimeOffset FixedTime = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private static PartialNameEntityMatcher Sut(Action<EntityResolutionOptions>? configure = null)
    {
        var options = new EntityResolutionOptions { EnablePartialNameMatch = true };
        configure?.Invoke(options);
        return new PartialNameEntityMatcher(options, NullLogger.Instance);
    }

    private static Entity Person(string id, string name, string type = "PERSON", params string[] aliases) => new()
    {
        EntityId = id,
        Name = name,
        Type = type,
        Confidence = 1.0,
        Aliases = aliases,
        CreatedAtUtc = FixedTime,
    };

    private static ExtractedEntity Mention(string name, string type = "PERSON") => new() { Name = name, Type = type };

    [Fact]
    public async Task A_first_name_resolves_to_the_only_person_whose_name_contains_it()
    {
        var result = await Sut().TryMatchAsync(Mention("Priya"), [Person("p1", "Priya Nair"), Person("p2", "Jose Garcia")]);

        result.Should().NotBeNull();
        result!.ResolvedEntity.EntityId.Should().Be("p1");
        result.MatchType.Should().Be(EntityMatchType.PartialName);
        result.Confidence.Should().Be(0.9);
    }

    [Fact]
    public async Task A_full_name_resolves_to_a_person_known_only_by_part_of_it()
    {
        var result = await Sut().TryMatchAsync(Mention("Priya Nair"), [Person("p1", "Priya")]);

        result!.ResolvedEntity.EntityId.Should().Be("p1");
    }

    [Fact]
    public async Task Two_qualifying_people_are_never_guessed_between_and_the_ambiguity_is_recorded()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == nameof(PartialNameEntityMatcherTests),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        using var source = new ActivitySource(nameof(PartialNameEntityMatcherTests));
        using var activity = source.StartActivity("resolve");

        var result = await Sut().TryMatchAsync(Mention("Priya"), [Person("p1", "Priya Nair"), Person("p2", "Priya Shah")]);

        result.Should().BeNull();
        activity!.Events.Should().ContainSingle(e => e.Name == "memory.resolve.partial_name_ambiguous")
            .Which.Tags.Should().Contain(new KeyValuePair<string, object?>("memory.resolve.match_count", 2));
    }

    [Fact]
    public async Task Accents_case_punctuation_and_initials_do_not_block_a_match()
    {
        (await Sut().TryMatchAsync(Mention("nunez"), [Person("p1", "Lucia Núñez")]))!
            .ResolvedEntity.EntityId.Should().Be("p1");
        (await Sut().TryMatchAsync(Mention("J. Smith"), [Person("p2", "John Smith")]))!
            .ResolvedEntity.EntityId.Should().Be("p2");
    }

    [Fact]
    public async Task Aliases_count_as_names()
    {
        var result = await Sut().TryMatchAsync(Mention("Bob"), [Person("p1", "Robert Smith", "PERSON", "Bob Smith")]);

        result!.ResolvedEntity.EntityId.Should().Be("p1");
    }

    [Fact]
    public async Task Types_outside_the_configured_list_are_left_to_the_other_matchers()
    {
        // "Microsoft" and "Microsoft Research" are different organizations; a word-subset rule cannot tell.
        var result = await Sut().TryMatchAsync(
            Mention("Microsoft", "ORGANIZATION"), [Person("o1", "Microsoft Research", "ORGANIZATION")]);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Only_same_type_entities_qualify()
    {
        var result = await Sut().TryMatchAsync(Mention("Paris"), [Person("l1", "Paris France", "LOCATION")]);

        result.Should().BeNull();
    }

    [Fact]
    public async Task The_type_list_is_case_insensitive()
    {
        var result = await Sut().TryMatchAsync(Mention("Priya", "Person"), [Person("p1", "Priya Nair", "Person")]);

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task Identical_word_sets_are_not_partial()
    {
        // Word order / spelling variants of the SAME name are the exact and fuzzy matchers' business.
        var result = await Sut().TryMatchAsync(Mention("Ruiz Priya"), [Person("p1", "Priya Nair")]);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Names_of_only_initials_or_punctuation_match_nothing()
    {
        (await Sut().TryMatchAsync(Mention("J."), [Person("p1", "J. Smith")])).Should().BeNull();
        (await Sut().TryMatchAsync(Mention("--"), [Person("p1", "Priya Nair")])).Should().BeNull();
    }
}

/// <summary>
/// The resolver's side: partial matching is opt-in, runs before the embedding-based matcher, and a
/// new entity keeps the vector resolution already computed for its name.
/// </summary>
public sealed class CompositeEntityResolverPartialNameAndReuseTests
{
    private static readonly DateTimeOffset FixedTime = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly IEntityRepository _entities = Substitute.For<IEntityRepository>();
    private readonly IEmbeddingOrchestrator _embeddings = Substitute.For<IEmbeddingOrchestrator>();

    public CompositeEntityResolverPartialNameAndReuseTests()
    {
        _entities.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<Entity>()));
        _entities.GetByTypeAsync("PERSON", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>(
            [
                new Entity
                {
                    EntityId = "priya-nair", Name = "Priya Nair", Type = "PERSON", Confidence = 1.0,
                    Embedding = [1f, 0f, 0f, 0f], CreatedAtUtc = FixedTime,
                },
            ]));
        // Orthogonal to the stored vector: the semantic matcher never matches.
        _embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new[] { 0f, 1f, 0f, 0f }));
    }

    private CompositeEntityResolver Sut(bool partial)
    {
        var options = new ExtractionOptions();
        options.EntityResolution.EnablePartialNameMatch = partial;
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(FixedTime);
        var ids = Substitute.For<IIdGenerator>();
        ids.GenerateId().Returns("new-id");
        return new CompositeEntityResolver(
            _entities, _embeddings, Options.Create(options), clock, ids,
            NullLogger<CompositeEntityResolver>.Instance);
    }

    [Fact]
    public async Task Off_by_default_so_existing_behaviour_is_unchanged()
    {
        new EntityResolutionOptions().EnablePartialNameMatch.Should().BeFalse();

        var result = await Sut(partial: false).ResolveForPersistenceAsync(
            new ExtractedEntity { Name = "Priya", Type = "PERSON" }, []);

        result.EntityId.Should().Be("new-id");
    }

    [Fact]
    public async Task When_on_the_mention_resolves_to_the_known_person_without_an_embedding_round_trip()
    {
        var result = await Sut(partial: true).ResolveForPersistenceAsync(
            new ExtractedEntity { Name = "Priya", Type = "PERSON" }, []);

        result.EntityId.Should().Be("priya-nair");
        await _embeddings.DidNotReceive().EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_new_entity_keeps_the_vector_the_semantic_matcher_computed_for_its_name()
    {
        // Before: the vector was discarded and persistence embedded the same name again.
        var result = await Sut(partial: false).ResolveForPersistenceAsync(
            new ExtractedEntity { Name = "Contoso", Type = "PERSON" }, []);

        result.EntityId.Should().Be("new-id");
        result.Embedding.Should().Equal(0f, 1f, 0f, 0f);
        await _embeddings.Received(1).EmbedAsync("Contoso", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_embedding_is_not_kept_so_persistence_still_retries_it()
    {
        _embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Array.Empty<float>()));

        var result = await Sut(partial: false).ResolveForPersistenceAsync(
            new ExtractedEntity { Name = "Contoso", Type = "PERSON" }, []);

        result.Embedding.Should().BeNull();
    }

    [Fact]
    public void An_out_of_range_confidence_fails_validation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentMemoryCore(o => o.Extraction.EntityResolution.PartialNameMatchConfidence = 1.5);
        using var provider = services.BuildServiceProvider();

        var act = () => _ = provider.GetRequiredService<IOptions<MemoryOptions>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }
}
