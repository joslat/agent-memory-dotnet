using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;
using AgentMemory.Core.Services;
using AgentMemory.Tests.Unit.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// Review round 3: two values of one single-valued relation in the same extraction. The batch path writes both before
/// supersession runs, so each saw the other as a live older value and they closed each other. The later one said wins.
/// Also: the speaker, written under the user's name, is embedded as that name and written once.
/// </summary>
public sealed class SamePersistOrderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A store that remembers what is live, like the real one.</summary>
    private sealed class Store
    {
        public List<Fact> Facts { get; } = [];
        public List<Preference> Preferences { get; } = [];
    }

    private static PersistenceStage Stage(Store store, IEntityRepository? entities = null, IEmbeddingOrchestrator? embeddings = null,
        bool batch = true, IRelationshipRepository? relationships = null, bool failFast = false)
    {
        var facts = StoreFakes.Facts(store.Facts, T0);
        var preferences = StoreFakes.Preferences(store.Preferences, T0);

        entities ??= Substitute.For<IEntityRepository>();
        if (embeddings is null)
        {
            embeddings = Substitute.For<IEmbeddingOrchestrator>();
            embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[4]);
        }
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(T0);
        var ids = Substitute.For<IIdGenerator>();
        ids.GenerateId().Returns(_ => Guid.NewGuid().ToString("N"));
        return new PersistenceStage(embeddings, entities, facts, preferences, relationships ?? Substitute.For<IRelationshipRepository>(), clock, ids,
            NullLogger<PersistenceStage>.Instance, new PassThroughMemoryPersistenceTransaction(),
            Options.Create(new ExtractionOptions
            {
                SupersedeReplacedFacts = true, EnableBatchMemoryUpserts = batch,
                FailureMode = failFast ? IngestionFailureMode.FailFast : IngestionFailureMode.BestEffort,
            }));
    }

    private static ExtractedFact F(string o) => new() { Subject = "Oskar", Predicate = "lives in", Object = o, Confidence = 0.9 };

    [Fact]
    public async Task Two_homes_said_in_one_turn_leave_the_later_one_live()
    {
        var store = new Store();
        store.Facts.Add(new Fact { FactId = "hamburg", Subject = "Oskar", Predicate = "lives in", Object = "Hamburg", Confidence = 1, CreatedAtUtc = T0.AddDays(-9) });

        await Stage(store).PersistAsync(new ExtractionStageResult { FilteredFacts = [F("Copenhagen"), F("Oslo")] }, ownerId: "u1");

        store.Facts.Where(f => f.InvalidatedAtUtc is null).Select(f => f.Object).Should().Equal("Oslo");
    }

    [Fact]
    public async Task Two_favourites_said_in_one_turn_leave_the_later_one_live()
    {
        var store = new Store();

        await Stage(store).PersistAsync(new ExtractionStageResult
        {
            FilteredPreferences =
            [
                new ExtractedPreference { Category = "music", PreferenceText = "Favourite band is Radiohead" },
                new ExtractedPreference { Category = "music", PreferenceText = "Favourite band is Arcade Fire" },
            ],
        }, ownerId: "u1");

        store.Preferences.Where(p => p.InvalidatedAtUtc is null).Select(p => p.PreferenceText).Should().Equal("Favourite band is Arcade Fire");
    }

    // ── The speaker ───────────────────────────────────────────────────────────────────────────────────

    private static Entity E(string id) => new() { EntityId = id, Name = id, Type = "PERSON", Confidence = 1, CreatedAtUtc = T0, Embedding = [9f, 9f, 9f, 9f] };

    private static ExtractionStageResult SpeakerSays(params string[] selfWords) => new()
    {
        ResolvedEntityMap = selfWords.ToDictionary(w => w, w => E($"id-{w}"), StringComparer.OrdinalIgnoreCase),
        FilteredFacts =
        [
            new ExtractedFact { Subject = "user", Predicate = "is named", Object = "Rosa", Confidence = 1 },
            new ExtractedFact { Subject = "user", Predicate = "works as", Object = "architect", Confidence = 1 },
        ],
    };

    [Fact]
    public async Task The_speaker_is_written_once_and_embedded_as_their_name()
    {
        var entities = Substitute.For<IEntityRepository>();
        var written = new List<Entity>();
        entities.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns(ci => { written.Add(ci.Arg<Entity>()); return Task.FromResult(ci.Arg<Entity>()); });
        var embeddings = Substitute.For<IEmbeddingOrchestrator>();
        embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[4]);
        embeddings.EmbedAsync("Rosa", Arg.Any<CancellationToken>()).Returns(new[] { 1f, 2f, 3f, 4f });
        var store = new Store();

        await Stage(store, entities, embeddings).PersistAsync(SpeakerSays("user", "I"), ownerId: "u1");

        var rosa = written.Should().ContainSingle("\"user\" and \"I\" are one speaker").Subject;
        rosa.Name.Should().Be("Rosa");
        rosa.Embedding.Should().Equal(1f, 2f, 3f, 4f);
    }

    [Fact]
    public async Task The_speaker_is_not_written_when_the_named_person_is_already_stored()
    {
        var entities = Substitute.For<IEntityRepository>();
        var written = new List<Entity>();
        entities.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns(ci => { written.Add(ci.Arg<Entity>()); return Task.FromResult(ci.Arg<Entity>()); });
        entities.FindLiveByNameAsync("Rosa", Arg.Any<string?>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Entity?>(E("stored-rosa")));

        await Stage(new Store(), entities).PersistAsync(SpeakerSays("user"), ownerId: "u1");

        written.Should().BeEmpty();
    }

    // ── Review round 4 ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_value_said_again_closes_what_came_between_on_the_item_path()
    {
        var store = new Store();

        await Stage(store, batch: false).PersistAsync(
            new ExtractionStageResult { FilteredFacts = [F("Copenhagen"), F("Oslo"), F("Copenhagen")] }, ownerId: "u1");

        store.Facts.Where(f => f.InvalidatedAtUtc is null).Select(f => f.Object).Should().Equal("Copenhagen");
    }

    [Fact]
    public async Task A_correction_closes_the_value_it_names_even_when_that_value_is_said_after_it()
    {
        var store = new Store();

        await Stage(store).PersistAsync(new ExtractionStageResult
        {
            FilteredFacts = [F("Oslo") with { Replaces = "Copenhagen" }, F("Copenhagen")],
            FilteredPreferences =
            [
                new ExtractedPreference { Category = "music", PreferenceText = "likes Arcade Fire", Replaces = "Radiohead" },
                new ExtractedPreference { Category = "music", PreferenceText = "likes Radiohead" },
            ],
        }, ownerId: "u1");

        store.Facts.Where(f => f.InvalidatedAtUtc is null).Select(f => f.Object).Should().Equal("Oslo");
        store.Preferences.Where(p => p.InvalidatedAtUtc is null).Select(p => p.PreferenceText).Should().Equal("likes Arcade Fire");
    }

    [Fact]
    public async Task A_relationship_from_any_self_word_lands_on_the_speaker()
    {
        var entities = Substitute.For<IEntityRepository>();
        entities.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>()).Returns(ci => Task.FromResult(ci.Arg<Entity>()));
        var relationships = Substitute.For<IRelationshipRepository>();
        var written = new List<Relationship>();
        relationships.GetBySourceEntityAsync(Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Relationship>>([]));
        relationships.UpsertAsync(Arg.Any<Relationship>(), Arg.Any<CancellationToken>())
            .Returns(ci => { written.Add(ci.Arg<Relationship>()); return Task.FromResult(ci.Arg<Relationship>()); });
        var extraction = SpeakerSays("user", "I") with
        {
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase)
            {
                ["user"] = E("id-user"), ["I"] = E("id-I"), ["Carmen"] = E("id-Carmen"),
            },
            FilteredRelationships = [new ExtractedRelationship { SourceEntity = "I", RelationshipType = "FRIEND_OF", TargetEntity = "Carmen", Confidence = 0.9 }],
        };

        await Stage(new Store(), entities, relationships: relationships).PersistAsync(extraction, ownerId: "u1");

        written.Should().ContainSingle().Which.SourceEntityId.Should().Be("id-user", "\"I\" is the speaker, written once");
    }

    // ── Review round 5 ───────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<string> LiveHomes(Store store) =>
        store.Facts.Where(f => f.InvalidatedAtUtc is null && f.Predicate == "lives in").Select(f => f.Object);

    [Fact]
    public async Task A_correction_about_the_user_wins_over_the_old_value_restated_under_their_name()
    {
        var store = new Store();

        await Stage(store).PersistAsync(new ExtractionStageResult
        {
            FilteredFacts =
            [
                new ExtractedFact { Subject = "user", Predicate = "is named", Object = "Dana", Confidence = 1 },
                new ExtractedFact { Subject = "I", Predicate = "lives in", Object = "Oslo", Confidence = 1, Replaces = "Copenhagen" },
                new ExtractedFact { Subject = "I", Predicate = "lives in", Object = "Copenhagen", Confidence = 1 },
            ],
        }, ownerId: "u1");

        LiveHomes(store).Should().Equal("Oslo");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_old_value_restated_after_its_correction_is_closed_on_either_path(bool batch)
    {
        var store = new Store();

        await Stage(store, batch: batch).PersistAsync(
            new ExtractionStageResult { FilteredFacts = [F("Oslo") with { Replaces = "Copenhagen" }, F("Copenhagen")] }, ownerId: "u1");

        LiveHomes(store).Should().Equal("Oslo");
    }

    [Fact]
    public async Task A_favourite_correction_wins_over_the_old_favourite_restated()
    {
        var store = new Store();

        await Stage(store).PersistAsync(new ExtractionStageResult
        {
            FilteredPreferences =
            [
                new ExtractedPreference { Category = "music", PreferenceText = "Favourite band is Arcade Fire", Replaces = "Radiohead" },
                new ExtractedPreference { Category = "music", PreferenceText = "Favourite band is Radiohead" },
            ],
        }, ownerId: "u1");

        store.Preferences.Where(p => p.InvalidatedAtUtc is null).Select(p => p.PreferenceText).Should().Equal("Favourite band is Arcade Fire");
    }

    [Fact]
    public async Task A_correction_naming_its_own_value_still_replaces_the_stored_one()
    {
        var store = new Store();
        store.Facts.Add(new Fact { FactId = "cph", Subject = "Oskar", Predicate = "lives in", Object = "Copenhagen", Confidence = 1, CreatedAtUtc = T0.AddDays(-9) });
        store.Facts.Add(new Fact { FactId = "trip", Subject = "Oskar", Predicate = "visited", Object = "Oslo", Confidence = 1, CreatedAtUtc = T0.AddDays(-9) });

        await Stage(store).PersistAsync(
            new ExtractionStageResult { FilteredFacts = [F("Oslo") with { Replaces = "Oslo" }] }, ownerId: "u1");

        LiveHomes(store).Should().Equal("Oslo");
        store.Facts.Single(f => f.FactId == "trip").InvalidatedAtUtc.Should().BeNull("a mark naming the fact's own value corrects nothing");
    }

    [Fact]
    public async Task A_corrected_away_value_under_another_relation_still_replaces_its_own()
    {
        var store = new Store();
        store.Facts.Add(new Fact { FactId = "lis", Subject = "Oskar", Predicate = "favourite city", Object = "Lisbon", Confidence = 1, CreatedAtUtc = T0.AddDays(-9) });

        await Stage(store).PersistAsync(new ExtractionStageResult
        {
            FilteredFacts =
            [
                F("Oslo") with { Replaces = "Copenhagen" },
                new ExtractedFact { Subject = "Oskar", Predicate = "favourite city", Object = "Copenhagen", Confidence = 1 },
            ],
        }, ownerId: "u1");

        store.Facts.Where(f => f.InvalidatedAtUtc is null).Select(f => f.Object).Should().BeEquivalentTo(["Oslo", "Copenhagen"]);
    }

    [Fact]
    public async Task The_speakers_name_is_the_speakers_even_when_another_entity_had_it_as_an_alias()
    {
        var entities = Substitute.For<IEntityRepository>();
        entities.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>()).Returns(ci => Task.FromResult(ci.Arg<Entity>()));
        var relationships = Substitute.For<IRelationshipRepository>();
        var written = new List<Relationship>();
        relationships.GetBySourceEntityAsync(Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Relationship>>([]));
        relationships.UpsertAsync(Arg.Any<Relationship>(), Arg.Any<CancellationToken>())
            .Returns(ci => { written.Add(ci.Arg<Relationship>()); return Task.FromResult(ci.Arg<Relationship>()); });
        var extraction = SpeakerSays("user") with
        {
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase)
            {
                ["Carmen"] = E("id-Carmen") with { Aliases = ["Rosa"] }, ["user"] = E("id-user"), ["Luis"] = E("id-Luis"),
            },
            FilteredRelationships = [new ExtractedRelationship { SourceEntity = "Rosa", RelationshipType = "FRIEND_OF", TargetEntity = "Luis", Confidence = 0.9 }],
        };

        await Stage(new Store(), entities, relationships: relationships).PersistAsync(extraction, ownerId: "u1");

        written.Should().ContainSingle().Which.SourceEntityId.Should().Be("id-user", "Rosa is the speaker's name, not Carmen's alias");
    }

    [Fact]
    public async Task FailFast_holds_for_the_speakers_name_vector()
    {
        var embeddings = Substitute.For<IEmbeddingOrchestrator>();
        embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[4]);
        embeddings.EmbedAsync("Rosa", Arg.Any<CancellationToken>()).Returns<float[]>(_ => throw new InvalidOperationException("down"));
        var entities = Substitute.For<IEntityRepository>();
        entities.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>()).Returns(ci => Task.FromResult(ci.Arg<Entity>()));

        var act = () => Stage(new Store(), entities, embeddings, failFast: true).PersistAsync(SpeakerSays("user"), ownerId: "u1");

        // Reported as the embedding failure it is, like every other embedding under FailFast.
        (await act.Should().ThrowAsync<MemoryIngestionException>()).Which.CompletedOutcomes.Should().Contain(outcome =>
            outcome.Stage == IngestionStage.Embedding && outcome.ErrorCode == MemoryErrorCodes.EmbeddingGenerationFailed);
    }

    // ── Review round 6: one decision for which value is current ───────────────────────────────
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Chained_corrections_leave_the_newest_value_live(bool batch)
    {
        var store = new Store();
        store.Facts.Add(new Fact { FactId = "ber", Subject = "Oskar", Predicate = "lives in", Object = "Berlin", Confidence = 1, CreatedAtUtc = T0.AddDays(-9) });
        await Stage(store, batch: batch).PersistAsync(new ExtractionStageResult
        {
            FilteredFacts = [F("Oslo") with { Replaces = "Copenhagen" }, F("Copenhagen") with { Replaces = "Berlin" }],
        }, ownerId: "u1");
        LiveHomes(store).Should().Equal("Oslo");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Chained_favourite_corrections_leave_the_newest_live(bool batch)
    {
        var store = new Store();
        await Stage(store, batch: batch).PersistAsync(new ExtractionStageResult
        {
            FilteredPreferences =
            [
                new ExtractedPreference { Category = "music", PreferenceText = "Favourite band is Arcade Fire", Replaces = "Radiohead" },
                new ExtractedPreference { Category = "music", PreferenceText = "Favourite band is Radiohead", Replaces = "Muse" },
            ],
        }, ownerId: "u1");
        store.Preferences.Where(p => p.InvalidatedAtUtc is null).Select(p => p.PreferenceText).Should().Equal("Favourite band is Arcade Fire");
    }

    [Fact]
    public async Task A_stored_old_value_restated_is_still_closed_by_a_correction_under_another_relation()
    {
        var store = new Store();
        store.Facts.Add(new Fact { FactId = "hm", Subject = "Oskar", Predicate = "is training for", Object = "half marathon", Confidence = 1, CreatedAtUtc = T0.AddDays(-9) });
        await Stage(store, batch: false).PersistAsync(new ExtractionStageResult
        {
            FilteredFacts =
            [
                new ExtractedFact { Subject = "Oskar", Predicate = "is training for", Object = "half marathon", Confidence = 1 },
                new ExtractedFact { Subject = "Oskar", Predicate = "plans to run", Object = "the full marathon", Confidence = 1, Replaces = "the half marathon" },
            ],
        }, ownerId: "u1");
        store.Facts.Single(f => f.FactId == "hm").InvalidatedAtUtc.Should().NotBeNull("the correction replaces the stored half marathon");
    }

    [Fact]
    public async Task A_preference_correction_naming_its_own_value_marks_nothing()
    {
        var store = new Store();
        await Stage(store, batch: false).PersistAsync(new ExtractionStageResult
        {
            FilteredPreferences =
            [
                new ExtractedPreference { Category = "music", PreferenceText = "Favourite band is Radiohead", Replaces = "Radiohead" },
                new ExtractedPreference { Category = "music", PreferenceText = "Wants to see Radiohead live" },
            ],
        }, ownerId: "u1");
        store.Preferences.Where(p => p.InvalidatedAtUtc is null).Select(p => p.PreferenceText)
            .Should().BeEquivalentTo(["Favourite band is Radiohead", "Wants to see Radiohead live"]);
    }

    [Fact]
    public async Task A_best_effort_speaker_vector_failure_reports_no_failure_for_the_written_entity()
    {
        var embeddings = Substitute.For<IEmbeddingOrchestrator>();
        embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[4]);
        embeddings.EmbedAsync("Rosa", Arg.Any<CancellationToken>()).Returns<float[]>(_ => throw new InvalidOperationException("down"));
        var entities = Substitute.For<IEntityRepository>();
        entities.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>()).Returns(ci => Task.FromResult(ci.Arg<Entity>()));
        var result = await Stage(new Store(), entities, embeddings).PersistAsync(SpeakerSays("user"), ownerId: "u1");
        var forUser = result.Outcomes.Where(o => o.Kind == MemoryItemKind.Entity && o.SourceKey == "user").Select(o => o.Status).ToList();
        forUser.Should().Equal(IngestionItemStatus.Succeeded);
    }

    [Fact]
    public async Task Before_the_name_is_known_every_self_word_is_one_speaker()
    {
        var store = new Store();

        await Stage(store).PersistAsync(new ExtractionStageResult
        {
            FilteredFacts =
            [
                new ExtractedFact { Subject = "I", Predicate = "lives in", Object = "Oslo", Confidence = 1, Replaces = "Copenhagen" },
                new ExtractedFact { Subject = "user", Predicate = "lives in", Object = "Copenhagen", Confidence = 1 },
            ],
        }, ownerId: "u1");

        LiveHomes(store).Should().Equal("Oslo");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_favourite_changed_back_in_one_turn_leaves_the_last(bool batch)
    {
        var store = new Store();

        await Stage(store, batch: batch).PersistAsync(new ExtractionStageResult
        {
            FilteredPreferences =
            [
                new ExtractedPreference { Category = "music", PreferenceText = "Favourite band is Arcade Fire", Replaces = "Radiohead" },
                new ExtractedPreference { Category = "music", PreferenceText = "Favourite band is Radiohead", Replaces = "Arcade Fire" },
            ],
        }, ownerId: "u1");

        store.Preferences.Where(p => p.InvalidatedAtUtc is null).Select(p => p.PreferenceText).Should().Equal("Favourite band is Radiohead");
    }
}
