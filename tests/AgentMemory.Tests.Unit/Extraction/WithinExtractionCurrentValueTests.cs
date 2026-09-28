using AgentMemory.Abstractions.Domain;
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

/// <summary>Review round 7: what feeds the current-value decision (ended values, dates, failures) and what surrounds it.</summary>
public sealed class WithinExtractionCurrentValueTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);

    private sealed class Store
    {
        public List<Fact> Facts { get; } = [];
        public HashSet<string> FailOn { get; } = [];
    }

    private static PersistenceStage Stage(Store store, bool batch = true, Action<ExtractionOptions>? configure = null,
        IEmbeddingOrchestrator? embeddings = null)
    {
        var facts = StoreFakes.Facts(store.Facts, T0, store.FailOn);

        var entities = Substitute.For<IEntityRepository>();
        entities.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>()).Returns(ci => Task.FromResult(ci.Arg<Entity>()));
        if (embeddings is null)
        {
            embeddings = Substitute.For<IEmbeddingOrchestrator>();
            embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[4]);
        }
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(T0);
        var ids = Substitute.For<IIdGenerator>();
        ids.GenerateId().Returns(_ => Guid.NewGuid().ToString("N"));
        var options = new ExtractionOptions { SupersedeReplacedFacts = true, EnableBatchMemoryUpserts = batch };
        configure?.Invoke(options);
        return new PersistenceStage(embeddings, entities, facts, Substitute.For<IPreferenceRepository>(),
            Substitute.For<IRelationshipRepository>(), clock, ids,
            NullLogger<PersistenceStage>.Instance, new PassThroughMemoryPersistenceTransaction(), Options.Create(options));
    }

    private static ExtractedFact F(string o, string s = "Oskar", string p = "lives in") =>
        new() { Subject = s, Predicate = p, Object = o, Confidence = 0.9 };

    private static Fact Stored(string id, string o, string s = "Oskar", string p = "lives in") =>
        new() { FactId = id, Subject = s, Predicate = p, Object = o, Confidence = 1, CreatedAtUtc = T0.AddDays(-9), OwnerId = "u1" };

    private static IEnumerable<string> Live(Store store, string predicate = "lives in") =>
        store.Facts.Where(f => f.InvalidatedAtUtc is null && f.Predicate == predicate && f.ValidUntil is null).Select(f => f.Object);

    // An ended value states history: said last, it is still not the current home, and replaces nothing.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_history_value_said_last_does_not_close_the_current_one(bool batch)
    {
        var store = new Store();
        store.Facts.Add(Stored("lis", "Lisbon"));

        await Stage(store, batch).PersistAsync(new ExtractionStageResult
        {
            FilteredFacts = [F("Oslo"), F("Berlin") with { ValidUntil = new DateTimeOffset(2019, 12, 31, 0, 0, 0, TimeSpan.Zero) }],
        }, ownerId: "u1");

        Live(store).Should().Equal("Oslo");
    }

    // Of two dated values the later-dated one is current, whatever order they were said in.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Dated_moves_said_out_of_order_leave_the_latest_dated_home(bool batch)
    {
        var store = new Store();
        Entity Place(string n) => new() { EntityId = n, Name = n, Type = "LOCATION", Confidence = 1, CreatedAtUtc = T0 };
        await Stage(store, batch).PersistAsync(new ExtractionStageResult
        {
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase) { ["Oslo"] = Place("Oslo"), ["Copenhagen"] = Place("Copenhagen") },
            FilteredFacts =
            [
                F("Oslo", p: "moved to") with { OccurredOn = new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero), OccurredOnPrecision = DatePrecision.Month },
                F("Copenhagen", p: "moved to") with { OccurredOn = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), OccurredOnPrecision = DatePrecision.Year },
            ],
        }, ownerId: "u1");

        Live(store).Should().Equal("Oslo");
    }

    // A current value that fails to write is stood in for by the last replaced one, which still replaces the stored home.
    [Fact]
    public async Task A_current_value_that_fails_to_write_is_stood_in_for()
    {
        var store = new Store();
        store.Facts.Add(Stored("lis", "Lisbon"));
        store.FailOn.Add("Oslo");

        await Stage(store, batch: false).PersistAsync(
            new ExtractionStageResult { FilteredFacts = [F("Copenhagen"), F("Oslo")] }, ownerId: "u1");

        Live(store).Should().ContainSingle();
    }

    // What a replaced value would have superseded, the current one supersedes, under whichever self word it was stored.
    [Fact]
    public async Task A_stored_home_under_another_self_word_is_closed()
    {
        var store = new Store();
        store.Facts.Add(Stored("lis", "Lisbon", s: "user"));

        await Stage(store).PersistAsync(new ExtractionStageResult
        {
            FilteredFacts = [F("Copenhagen", s: "user"), F("Oslo", s: "I")],
        }, ownerId: "u1");

        Live(store).Should().Equal("Oslo");
    }

    // A stored fact restated after a correction, under another relation, is what the person holds now: the correction's fallback spares it.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_stored_value_restated_after_a_correction_under_another_relation_stays_live(bool batch)
    {
        var store = new Store();
        store.Facts.Add(Stored("fav", "Copenhagen", p: "favourite city"));

        await Stage(store, batch).PersistAsync(new ExtractionStageResult
        {
            FilteredFacts = [F("Oslo") with { Replaces = "Copenhagen" }, F("Copenhagen", p: "favourite city")],
        }, ownerId: "u1");

        store.Facts.Single(f => f.FactId == "fav").InvalidatedAtUtc.Should().BeNull("the user just said it is still true");
    }

    // A statement merged within the extraction takes the place of its latest telling: "Copenhagen, Oslo, back in Copenhagen" leaves Copenhagen.
    [Fact]
    public async Task A_value_said_again_wins_with_within_extraction_dedup_on()
    {
        var store = new Store();
        var embeddings = Substitute.For<IEmbeddingOrchestrator>();
        embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<string>(0).Contains("Copenhagen") ? new[] { 1f, 0f, 0f, 0f } : new[] { 0f, 1f, 0f, 0f });

        await Stage(store, batch: false, o => o.DeduplicateWithinExtraction = true, embeddings).PersistAsync(
            new ExtractionStageResult { FilteredFacts = [F("Copenhagen"), F("Oslo"), F("Copenhagen") with { Confidence = 0.8 }] }, ownerId: "u1");

        Live(store).Should().Equal("Copenhagen");
    }

    // Two events on different days are two statements, as two validity windows are.
    [Fact]
    public void Two_events_on_different_days_are_not_one_statement()
    {
        var hike = F("Sintra", p: "went hiking in");
        PersistenceStage.MayBeOneStatement(
                hike with { OccurredOn = new DateTimeOffset(2026, 5, 3, 0, 0, 0, TimeSpan.Zero) },
                hike with { OccurredOn = new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero) })
            .Should().BeFalse();
    }

    // Valid-time as-of recall and derivation read an event by the day it happened, as they read a state by its start.
    [Fact]
    public void As_of_recall_bounds_an_event_by_the_day_it_happened()
    {
        AgentMemory.Neo4j.Queries.TemporalQueries.SearchFactsAsOf(true, false, 10).Should().Contain("occurred_on");
        AgentMemory.Neo4j.Queries.DerivedFactQueries.GetGroupFacts(true, false).Should().Contain("occurred_on");
    }

    // A home stated under the speaker's name, given in the same extraction, stops the entailed one as a self word does.
    [Theory]
    [InlineData("I")]
    [InlineData("Dana")]
    public async Task A_stated_home_wins_over_an_entailed_one_whatever_the_speaker_is_called(string statedSubject)
    {
        var store = new Store();
        Entity Place(string n) => new() { EntityId = n, Name = n, Type = "LOCATION", Confidence = 1, CreatedAtUtc = T0 };
        await Stage(store).PersistAsync(new ExtractionStageResult
        {
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase) { ["Paris"] = Place("Paris"), ["London"] = Place("London") },
            FilteredFacts =
            [
                new ExtractedFact { Subject = "user", Predicate = "is named", Object = "Dana", Confidence = 1 },
                F("Paris", s: statedSubject),
                F("London", s: "I", p: "moved to"),
            ],
        }, ownerId: "u1");

        Live(store).Should().Equal("Paris");
    }

    // A change of mind changed back in one extraction leaves the last value: a correction never closes the current one.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_correction_changed_back_leaves_the_last_value_live(bool batch)
    {
        var store = new Store();
        store.Facts.Add(Stored("osl", "Oslo"));

        await Stage(store, batch).PersistAsync(new ExtractionStageResult
        {
            FilteredFacts = [F("Copenhagen") with { Replaces = "Oslo" }, F("Oslo") with { Replaces = "Copenhagen" }],
        }, ownerId: "u1");

        Live(store).Should().Equal("Oslo");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_correction_changed_back_leaves_the_last_value_live_nothing_stored(bool batch)
    {
        var store = new Store();

        await Stage(store, batch).PersistAsync(new ExtractionStageResult
        {
            FilteredFacts = [F("Copenhagen") with { Replaces = "Oslo" }, F("Oslo") with { Replaces = "Copenhagen" }],
        }, ownerId: "u1");

        Live(store).Should().Equal("Oslo");
    }

    // The same under a relation with many values, where no current value protects it: what was said after the
    // correction is what the person holds now.
    [Fact]
    public async Task A_stored_value_restated_after_a_correction_under_a_many_valued_relation_stays_live()
    {
        var store = new Store();
        store.Facts.Add(Stored("trip", "Copenhagen", p: "visited"));

        await Stage(store).PersistAsync(new ExtractionStageResult
        {
            FilteredFacts = [F("Oslo") with { Replaces = "Copenhagen" }, F("Copenhagen", p: "visited")],
        }, ownerId: "u1");

        store.Facts.Single(f => f.FactId == "trip").InvalidatedAtUtc.Should().BeNull();
    }

    // ── Review round 8: every closing after every write, against the store's real semantics ─────────────────

    private static DateTimeOffset Y(int y) => new(y, 6, 1, 0, 0, 0, TimeSpan.Zero);

    // Current: not invalidated and no end reached (what live recall shows).
    private static List<string> Current(Store store, string predicate = "lives in") =>
        store.Facts.Where(f => f.InvalidatedAtUtc is null && f.Predicate == predicate && (f.ValidUntil is null || f.ValidUntil > T0)).Select(f => f.Object).ToList();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Stored_value_restated_after_a_correction_is_still_current(bool batch)
    {
        var store = new Store();
        store.Facts.Add(Stored("fav", "Copenhagen", p: "favourite city"));
        await Stage(store, batch).PersistAsync(new ExtractionStageResult
        {
            FilteredFacts = [F("Oslo") with { Replaces = "Copenhagen" }, F("Copenhagen", p: "favourite city")],
        }, ownerId: "u1");
        Current(store, "favourite city").Should().Equal("Copenhagen");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Chained_corrections_onto_a_stored_value_leave_it_current(bool batch)
    {
        var store = new Store();
        store.Facts.Add(Stored("osl", "Oslo"));
        await Stage(store, batch).PersistAsync(new ExtractionStageResult
        {
            FilteredFacts = [F("Copenhagen") with { Replaces = "Oslo" }, F("Oslo") with { Replaces = "Copenhagen" }],
        }, ownerId: "u1");
        Current(store).Should().Equal("Oslo");
    }

    [Fact]
    public async Task Stand_in_is_the_latest_dated_surviving_value()
    {
        var store = new Store();
        store.Facts.Add(Stored("lis", "Lisbon"));
        store.FailOn.Add("Oslo");
        await Stage(store, batch: false).PersistAsync(new ExtractionStageResult
        {
            FilteredFacts = [F("Oslo") with { ValidFrom = Y(2025) }, F("Copenhagen") with { ValidFrom = Y(2020) }, F("Berlin") with { ValidFrom = Y(2018) }],
        }, ownerId: "u1");
        Current(store).Should().Equal("Copenhagen");
    }

    [Fact]
    public async Task Stand_in_is_never_a_value_the_person_corrected_away()
    {
        var store = new Store();
        store.Facts.Add(Stored("ber", "Bergen"));
        store.FailOn.Add("Oslo");
        await Stage(store, batch: false).PersistAsync(new ExtractionStageResult
        {
            FilteredFacts = [F("Copenhagen"), F("Oslo") with { Replaces = "Copenhagen" }],
        }, ownerId: "u1");
        Current(store).Should().Equal(["Bergen"], "the retracted value is withdrawn, the stored one stays");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_history_value_of_the_same_extraction_is_not_closed(bool batch)
    {
        var store = new Store();
        await Stage(store, batch).PersistAsync(new ExtractionStageResult
        {
            FilteredFacts = [F("Copenhagen"), F("Oslo"), F("Berlin") with { ValidUntil = Y(2019) }],
        }, ownerId: "u1");
        store.Facts.Single(f => f.Object == "Berlin").InvalidatedAtUtc.Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_history_value_said_after_the_current_one_is_not_closed(bool batch)
    {
        var store = new Store();
        await Stage(store, batch).PersistAsync(new ExtractionStageResult
        {
            FilteredFacts = [F("Oslo"), F("Berlin") with { ValidUntil = Y(2019) }],
        }, ownerId: "u1");
        store.Facts.Single(f => f.Object == "Berlin").InvalidatedAtUtc.Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_planned_value_is_not_closed_by_the_undated_current(bool batch)
    {
        var store = new Store();
        await Stage(store, batch).PersistAsync(new ExtractionStageResult
        {
            FilteredFacts = [F("Oslo"), F("Berlin") with { ValidFrom = T0.AddDays(60) }],
        }, ownerId: "u1");
        store.Facts.Single(f => f.Object == "Berlin").InvalidatedAtUtc.Should().BeNull();
        store.Facts.Single(f => f.Object == "Oslo").InvalidatedAtUtc.Should().BeNull("a plan is not the current home");
    }

    // A correction between two history values ("I lived in Hamburg until 2019, not Berlin") leaves the current home alone.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_corrected_history_value_does_not_close_the_current_home(bool batch)
    {
        var store = new Store();
        store.Facts.Add(Stored("osl", "Oslo"));
        await Stage(store, batch).PersistAsync(new ExtractionStageResult
        {
            FilteredFacts = [F("Berlin") with { ValidUntil = Y(2019) }, F("Hamburg") with { ValidUntil = Y(2019), Replaces = "Berlin" }],
        }, ownerId: "u1");
        Current(store).Should().Equal("Oslo");
    }

    // An undated value began when it was said: now, later than any value dated in the past, whatever the order said.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_undated_value_is_later_than_a_past_dated_one(bool batch)
    {
        var store = new Store();
        await Stage(store, batch).PersistAsync(new ExtractionStageResult
        {
            FilteredFacts = [F("Oslo"), F("Copenhagen") with { ValidFrom = Y(2020) }],
        }, ownerId: "u1");
        Current(store).Should().Equal("Oslo");
    }

    // A marked correction withdraws what it replaces when it is said, even when the new value is dated ahead ("the full
    // marathon in May instead of the half in April").
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_correction_withdraws_what_it_replaces_even_when_the_new_value_is_dated_ahead(bool batch)
    {
        var store = new Store();
        store.Facts.Add(Stored("half", "half marathon in April 2027", p: "is training for"));
        await Stage(store, batch).PersistAsync(new ExtractionStageResult
        {
            FilteredFacts = [F("full marathon in May 2027", p: "is running") with { ValidFrom = T0.AddDays(200), Replaces = "half marathon in April 2027" }],
        }, ownerId: "u1");
        store.Facts.Single(f => f.FactId == "half").InvalidatedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public void The_profile_rebuild_boundary_counts_the_day_an_event_happens() =>
        AgentMemory.Neo4j.Queries.WorkingMemoryQueries.NextValidityBoundary.Should().Contain("coalesce(f.valid_from, f.occurred_on)");
}
