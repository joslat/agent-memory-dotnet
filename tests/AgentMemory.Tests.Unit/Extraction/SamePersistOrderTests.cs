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

    private static PersistenceStage Stage(Store store, IEntityRepository? entities = null, IEmbeddingOrchestrator? embeddings = null)
    {
        var facts = Substitute.For<IFactRepository, IBatchMemoryRepository<Fact>>();
        ((IBatchMemoryRepository<Fact>)facts).UpsertBatchAsync(Arg.Any<IReadOnlyList<Fact>>(), Arg.Any<CancellationToken>())
            .Returns(ci => { store.Facts.AddRange(ci.Arg<IReadOnlyList<Fact>>()); return Task.FromResult(ci.Arg<IReadOnlyList<Fact>>()); });
        facts.FindSupersededCandidatesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<IReadOnlyList<Fact>>(store.Facts
                .Where(f => f.InvalidatedAtUtc is null && f.Subject == ci.ArgAt<string>(1) && f.Predicate == ci.ArgAt<string>(2) &&
                            f.Object != ci.ArgAt<string>(3) && f.FactId != ci.ArgAt<string>(0))
                .ToList()));
        facts.GetBySubjectAsync(Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>([]));
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
        return new PersistenceStage(embeddings, entities, facts, preferences, Substitute.For<IRelationshipRepository>(), clock, ids,
            NullLogger<PersistenceStage>.Instance, new PassThroughMemoryPersistenceTransaction(),
            Options.Create(new ExtractionOptions { SupersedeReplacedFacts = true, EnableBatchMemoryUpserts = true }));
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
}
