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
    [InlineData("2026-12-12", "2026-12-13")]
    [InlineData("12 diciembre 2026", "3 diciembre 2026")]
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
    [InlineData("2026-12", "2026-12-12")]
    [InlineData("December 2026", "2026-12-12")]
    [InlineData("December 12", "December 12, 2027")]
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

    [Theory]
    [InlineData("2026-12-12", "12 December 2026")]
    [InlineData("December 12, 2026", "2026-12-12")]
    [InlineData("12 diciembre 2026", "2026-12-12")]
    [InlineData("2028-02-29", "29 February 2028")]
    public async Task Equivalent_complete_dates_are_compared_as_values(string mention, string stored)
    {
        var (resolver, embeddings) = Resolver(Existing(stored));
        var result = await resolver.ResolveEntityAsync(new ExtractedEntity { Name = mention, Type = "OBJECT" }, ["wedding-message"]);
        result.EntityId.Should().Be("training-date");
        await embeddings.DidNotReceive().EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("December 12", "December 3")]
    [InlineData("2026-12-12", "2026-12-03")]
    [InlineData("2026-12-12", "12 December 2026")]
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

    [Theory]
    [InlineData("exact")]
    [InlineData("fuzzy")]
    [InlineData("partial")]
    [InlineData("semantic")]
    public async Task Name_matchers_cannot_authorize_temporal_identity(string strategy)
    {
        var options = new EntityResolutionOptions { PartialNameMatchTypes = ["OBJECT"] };
        var embeddings = Substitute.For<IEmbeddingOrchestrator>();
        embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new float[] { 1, 0, 0, 0 }));
        IEntityMatcher matcher = strategy switch
        {
            "exact" => new ExactMatchEntityMatcher(),
            "fuzzy" => new FuzzyMatchEntityMatcher(options),
            "partial" => new PartialNameEntityMatcher(options, NullLogger.Instance),
            _ => new SemanticMatchEntityMatcher(embeddings, options)
        };
        var stored = strategy == "partial" ? Existing("December") : Existing("December 3", "December 12");
        var result = await matcher.TryMatchAsync(new ExtractedEntity { Name = "December 12", Type = "OBJECT" }, [stored]);
        result.Should().BeNull();
        await embeddings.DidNotReceive().EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    public static IEnumerable<object[]> ResolverModes()
    {
        foreach (var indexed in new[] { false, true })
        foreach (var autoMerge in new[] { false, true })
        foreach (var batched in new[] { false, true })
            yield return [indexed, autoMerge, batched];
    }

    [Theory]
    [MemberData(nameof(ResolverModes))]
    public async Task Date_protection_is_independent_of_index_batch_and_merge_modes(bool indexed, bool autoMerge, bool batched)
    {
        var options = new ExtractionOptions { EnableAutoMerge = autoMerge, UseBatchEntityResolutionSnapshots = batched };
        options.EntityResolution.IndexedCandidates = indexed;
        var (resolver, embeddings) = Resolver(Existing("December 3"), options);
        using var batch = resolver.BeginBatch();
        var result = await resolver.ResolveEntityAsync(new ExtractedEntity { Name = "December 12", Type = "OBJECT" }, ["source"]);
        result.Name.Should().Be("December 12");
        result.EntityId.Should().Be("new-date");
        await embeddings.DidNotReceive().EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("February 30, 2026", "February 3, 2026")]
    [InlineData("2026-02-29", "2026-02-28")]
    [InlineData("December 32, 2026", "December 3, 2026")]
    [InlineData("01/02/2026", "2026-01-02")]
    public async Task Invalid_or_ambiguous_calendar_values_are_preserved_without_guessing(string mention, string stored)
    {
        var (resolver, embeddings) = Resolver(Existing(stored));
        var result = await resolver.ResolveEntityAsync(new ExtractedEntity { Name = mention, Type = "OBJECT" }, ["source"]);
        result.Name.Should().Be(mention);
        result.EntityId.Should().Be("new-date");
        await embeddings.DidNotReceive().EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_temporal_candidate_is_not_a_semantic_target_for_an_ordinary_object()
    {
        var embeddings = Substitute.For<IEmbeddingOrchestrator>();
        embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new float[] { 1, 0, 0, 0 }));
        var matcher = new SemanticMatchEntityMatcher(embeddings, new EntityResolutionOptions());
        var result = await matcher.TryMatchAsync(new ExtractedEntity { Name = "Release workshop", Type = "OBJECT" }, [Existing("2026-12-12")]);
        result.Should().BeNull("a coincident embedding cannot turn an object into a date");
    }

    [Fact]
    public async Task Batch_preparation_does_not_embed_calendar_literals()
    {
        var (resolver, embeddings) = Resolver(Existing("December 3"));
        using var batch = resolver.BeginBatch();
        await resolver.PrepareCandidatesAsync(["OBJECT"]);
        await resolver.PrepareNameEmbeddingsAsync([
            new ExtractedEntity { Name = "2026-12-12", Type = "OBJECT" },
            new ExtractedEntity { Name = "2026-12-13", Type = "OBJECT" }
        ]);
        await embeddings.DidNotReceive().EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancelled_date_resolution_cannot_persist()
    {
        var (resolver, _) = Resolver(Existing("December 3"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var resolve = () => resolver.ResolveEntityAsync(
            new ExtractedEntity { Name = "2026-12-12", Type = "OBJECT" }, ["source"], cancellationToken: cancellation.Token);
        await resolve.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("May", "May Parker")]
    [InlineData("December 12 Edition", "December 12 Edition Pro")]
    public void A_name_is_not_a_calendar_literal_because_it_contains_a_month(string mention, string canonical)
    {
        var entity = Existing(canonical) with { Type = mention == "May" ? "PERSON" : "OBJECT" };
        var names = new FactNames(new ExtractionOptions { CanonicalFactSubjects = true },
            new Dictionary<string, Entity> { [mention] = entity }, null);
        names.CanonicalName(mention).Should().Be(canonical);
    }

    private static (CompositeEntityResolver Resolver, IEmbeddingOrchestrator Embeddings) Resolver(Entity entity, ExtractionOptions? options = null)
    {
        var repository = Substitute.For<IEntityRepository>();
        repository.GetByTypeAsync("OBJECT", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>([entity]));
        repository.GetByTypeWithoutEmbeddingAsync("OBJECT", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>([entity with { Embedding = null }]));
        repository.GetByIdAsync(entity.EntityId, Arg.Any<CancellationToken>()).Returns(Task.FromResult<Entity?>(entity));
        repository.SearchByVectorAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<(Entity Entity, double Score)>>([(entity, 1)]));
        repository.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>()).Returns(call => Task.FromResult(call.Arg<Entity>()));
        var embeddings = Substitute.For<IEmbeddingOrchestrator>();
        embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(new float[] { 1, 0, 0, 0 }));
        var time = Substitute.For<TimeProvider>();
        time.GetUtcNow().Returns(Now);
        var ids = Substitute.For<IIdGenerator>();
        ids.GenerateId().Returns("new-date");
        return (new CompositeEntityResolver(repository, embeddings, Options.Create(options ?? new ExtractionOptions()), time, ids,
            NullLogger<CompositeEntityResolver>.Instance), embeddings);
    }
}
