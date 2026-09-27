using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;
using AgentMemory.Core.Services;
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
        bool batch = true, IRelationshipRepository? relationships = null)
    {
        var facts = Substitute.For<IFactRepository, IBatchMemoryRepository<Fact>>();
        // The item path MERGEs on the triple, as the store does: a value said again is the stored node, live again.
        facts.UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var fact = ci.Arg<Fact>();
            var i = store.Facts.FindIndex(f => f.Subject == fact.Subject && f.Predicate == fact.Predicate && f.Object == fact.Object);
            if (i < 0) { store.Facts.Add(fact); return Task.FromResult(fact); }
            store.Facts[i] = store.Facts[i] with { InvalidatedAtUtc = null };
            return Task.FromResult(store.Facts[i]);
        });
        ((IBatchMemoryRepository<Fact>)facts).UpsertBatchAsync(Arg.Any<IReadOnlyList<Fact>>(), Arg.Any<CancellationToken>())
            .Returns(ci => { store.Facts.AddRange(ci.Arg<IReadOnlyList<Fact>>()); return Task.FromResult(ci.Arg<IReadOnlyList<Fact>>()); });
        facts.FindSupersededCandidatesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<IReadOnlyList<Fact>>(store.Facts
                .Where(f => f.InvalidatedAtUtc is null && f.Subject == ci.ArgAt<string>(1) && f.Predicate == ci.ArgAt<string>(2) &&
                            f.Object != ci.ArgAt<string>(3) && f.FactId != ci.ArgAt<string>(0))
                .ToList()));
        facts.GetBySubjectAsync(Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<IReadOnlyList<Fact>>(store.Facts.Where(f => f.Subject == ci.ArgAt<string>(0)).ToList()));
        facts.SupersedeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var i = store.Facts.FindIndex(f => f.FactId == ci.ArgAt<string>(0));
                store.Facts[i] = store.Facts[i] with { InvalidatedAtUtc = T0 };
                return Task.FromResult(true);
            });

        var preferences = Substitute.For<IPreferenceRepository, IBatchMemoryRepository<Preference>>();
        ((IBatchMemoryRepository<Preference>)preferences).UpsertBatchAsync(Arg.Any<IReadOnlyList<Preference>>(), Arg.Any<CancellationToken>())
            .Returns(ci => { store.Preferences.AddRange(ci.Arg<IReadOnlyList<Preference>>()); return Task.FromResult(ci.Arg<IReadOnlyList<Preference>>()); });
        preferences.GetByCategoryAsync(Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<IReadOnlyList<Preference>>(store.Preferences.Where(p => p.Category == ci.ArgAt<string>(0)).ToList()));
        preferences.SupersedeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var i = store.Preferences.FindIndex(p => p.PreferenceId == ci.ArgAt<string>(0));
                store.Preferences[i] = store.Preferences[i] with { InvalidatedAtUtc = T0 };
                return Task.FromResult(true);
            });

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
            Options.Create(new ExtractionOptions { SupersedeReplacedFacts = true, EnableBatchMemoryUpserts = batch }));
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
}
