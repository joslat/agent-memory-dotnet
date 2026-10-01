using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;
using AgentMemory.Core.Memory;
using AgentMemory.Core.Services;
using AgentMemory.Extraction.Llm;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// 36.4: a change of mind replaces what it changes. Found in simulated conversations, every one with both values
/// left live: "works for" a new firm beside "works at" the old one, "moved to Copenhagen" beside "lives in
/// Hamburg", "Bruno is 7 years old" beside "6", "Arcade Fire, not Radiohead" as two preferences.
/// </summary>
public sealed class ChangesOfMindTests
{
    // ── The vocabulary: stored forms are the relation ────────────────────────────────────────────

    [Theory]
    [InlineData("works for", true)]
    [InlineData("work for", true)]
    [InlineData("lived in", false)]
    [InlineData("live", false)]
    [InlineData("worked at", false)]
    [InlineData("used to work in", false)]
    [InlineData("employed by", true)]
    [InlineData("favourite bands", false)]
    [InlineData("favourite bands are", false)]
    [InlineData("favourite children are", false)]
    [InlineData("favourite glass", true)]
    [InlineData("age", true)]
    [InlineData("favourite band", true)]
    [InlineData("favorite food", true)]
    [InlineData("has favourite band", true)]
    [InlineData("favourite band is", true)]
    [InlineData("favourite", false)]
    [InlineData("likes", false)]
    [InlineData("moved to", false)]
    public void Cardinality_reads_stored_forms_and_declared_prefixes(string predicate, bool single)
    {
        MemoryRelationCardinality.IsSingleValued(predicate).Should().Be(single);
    }

    [Fact]
    public void A_new_value_replaces_every_stored_form_of_its_relation()
    {
        MemoryRelationCardinality.ReplacedKeys("works for").Should().Contain(["works at", "works for", "employed by"])
            .And.NotContain(["worked at", "used to work in", "was employed in"], "history is not the current employer");
        MemoryRelationCardinality.ReplacedKeys("member of").Should().Equal(["member of"], "a relation without present forms replaces its own key only");
        MemoryRelationCardinality.ReplacedKeys("has favourite band").Should().Contain(["favourite band", "has favourite band", "favorite band"])
            .And.NotContain("favourite food");
        MemoryRelationCardinality.ReplacedKeys("likes").Should().Equal("likes");
    }

    // ── Shapes: an age, an event's entailed state ───────────────────────────────────────────────

    private static ExtractedFact F(string s, string p, string o) => new() { Subject = s, Predicate = p, Object = o, Confidence = 0.9 };

    [Theory]
    [InlineData("is", "6 years old", "age", "6")]
    [InlineData("is", "7-year-old", "age", "7")]
    [InlineData("turned", "7", "age", "7")]
    [InlineData("is", "7", "is", "7")]
    [InlineData("weighs", "6 years old", "weighs", "6 years old")]
    public void An_age_is_written_as_the_single_valued_age(string predicate, string @object, string expectedPredicate, string expectedObject)
    {
        var shaped = ReplacementShapes.AsAge(F("Bruno", predicate, @object));

        shaped.Predicate.Should().Be(expectedPredicate);
        shaped.Object.Should().Be(expectedObject);
    }

    private static string? Places(string name) => name == "Copenhagen" ? "LOCATION" : name == "the analytics team" ? "ORGANIZATION" : null;

    [Fact]
    public void Moving_somewhere_also_states_living_there_once()
    {
        var shaped = ReplacementShapes.Prepare([F("Nadia", "moved to", "Copenhagen")], Places, T0);
        shaped.Select(f => $"{f.Predicate} {f.Object}").Should().Equal("moved to Copenhagen", "lives in Copenhagen");

        var alreadySaid = ReplacementShapes.Prepare([F("Nadia", "moved to", "Copenhagen"), F("Nadia", "lives in", "Copenhagen")], Places, T0);
        alreadySaid.Should().HaveCount(2, "a state the turn already stated is not written twice");
    }

    [Fact]
    public void A_move_on_a_day_is_a_home_since_that_day_and_no_event_itself()
    {
        var march = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var shaped = ReplacementShapes.Prepare(
            [F("Nadia", "moved to", "Copenhagen") with { OccurredOn = march, OccurredOnPrecision = DatePrecision.Month }], Places, T0);

        var home = shaped.Should().HaveCount(2).And.Subject.Last();
        home.Predicate.Should().Be("lives in");
        home.ValidFrom.Should().Be(march);
        home.ValidFromPrecision.Should().Be(DatePrecision.Month);
        home.OccurredOn.Should().BeNull("living somewhere is a state, not an event");

        ReplacementShapes.Prepare(
                [F("Nadia", "moved to", "Copenhagen") with { OccurredOn = T0.AddMonths(2), OccurredOnPrecision = DatePrecision.Month }], Places, T0)
            .Should().ContainSingle("a move that has not happened yet is no home");
    }

    [Theory]
    [InlineData("the analytics team")]
    [InlineData("somewhere untyped")]
    public void Moving_to_something_that_is_not_a_place_states_no_home(string @object)
    {
        ReplacementShapes.Prepare([F("Tomás", "moved to", @object)], Places, T0).Should().ContainSingle();
    }

    // ── What a marked correction closes ──────────────────────────────────────────────────────────

    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private static Fact Stored(string id, string predicate, string @object) => new()
    {
        FactId = id, Subject = "user", Predicate = predicate, Object = @object, Confidence = 0.9, CreatedAtUtc = T0,
    };

    [Fact]
    public void A_correction_closes_the_same_relation_first_and_leaves_other_mentions_alone()
    {
        // Run 3, show 04: "works for a wind energy firm, replaces the shipping company".
        var winner = Stored("new", "works for", "a wind energy firm");
        Fact[] stored = [Stored("job", "works at", "a shipping company"), Stored("left", "left", "shipping company")];

        Corrections.Closed(stored, winner, "the shipping company").Select(f => f.FactId).Should().Equal(
            ["job"], "\"left the shipping company\" is a true event, not the replaced value");
    }

    [Fact]
    public void A_correction_closes_the_one_fact_that_mentions_the_value_when_no_relation_matches()
    {
        // Run 3, show 04: "is running the full marathon in May, replaces half marathon in April", stored as training.
        var winner = Stored("new", "is running", "full marathon in May");

        Corrections.Closed([Stored("half", "is training for", "half marathon")], winner, "half marathon in April")
            .Select(f => f.FactId).Should().Equal("half");
        // 38.6: a changed plan closes the plan it names and nothing else; the purchase is a true event and stays.
        Corrections.Closed([Stored("a", "is training for", "half marathon"), Stored("b", "bought shoes for", "half marathon")],
            winner, "half marathon in April").Select(f => f.FactId).Should().Equal("a");
    }

    /// <summary>
    /// 38.6 (K-19). Show 04, run 14: the old plan was stored under two phrasings, and the correction named it once
    /// ("replaces": "half marathon in April 2027"). With two mentions nothing was closed and both plans stayed live.
    /// </summary>
    [Fact]
    public void A_changed_plan_closes_every_phrasing_of_the_old_plan()
    {
        var winner = Stored("new", "plans to run", "full marathon in May 2027");
        Fact[] stored = [Stored("a", "is training for", "half marathon"), Stored("b", "is running", "half marathon in April")];

        Corrections.Closed(stored, winner, "half marathon in April 2027").Select(f => f.FactId).Should().BeEquivalentTo(["a", "b"]);
    }

    /// <summary>
    /// 38.6 review. The date a correction names picks the plan: "the April trip" is not the Rome trip in August, and
    /// "the half in April" is not the half in October. With no date named, two plans of two dates are two plans.
    /// </summary>
    [Fact]
    public void The_date_a_correction_names_picks_the_plan_it_closes()
    {
        Corrections.Closed(
                [Stored("lisbon", "is planning", "trip to Lisbon in April"), Stored("rome", "is planning", "trip to Rome in August")],
                Stored("new", "is planning", "trip to Porto"), "the April trip")
            .Select(f => f.FactId).Should().Equal("lisbon");

        Fact[] halves = [Stored("apr", "is training for", "half marathon in April"), Stored("oct", "is training for", "half marathon in October")];
        var full = Stored("full", "is doing", "full marathon in May");
        Corrections.Closed(halves, full, "the half in April").Select(f => f.FactId).Should().Equal("apr");
        Corrections.Closed(halves, full, "the half").Should().BeEmpty("two halves on two dates are two plans, and which one is meant is not said");
    }

    /// <summary>
    /// 38.6 review. The role at the old employer and the other phrasing of a changed plan close WITH the same-relation
    /// match: before, a same-relation match returned alone and they stayed live beside the new value.
    /// </summary>
    [Fact]
    public void A_correction_closes_every_form_of_what_it_names_not_only_the_same_relation()
    {
        Corrections.Closed(
                [Stored("job", "works at", "Contoso"), Stored("role", "works as", "designer at Contoso")],
                Stored("new", "works at", "Fabrikam"), "Contoso")
            .Select(f => f.FactId).Should().BeEquivalentTo(["job", "role"]);

        Corrections.Closed(
                [Stored("a", "is training for", "half marathon"), Stored("b", "is running", "half marathon in April")],
                Stored("new", "is training for", "full marathon"), "half marathon")
            .Select(f => f.FactId).Should().BeEquivalentTo(["a", "b"]);
    }

    /// <summary>
    /// 38.6, held-out show 11 as recorded: "I'm a designer at Contoso" was stored as "works as | designer at Contoso";
    /// "I don't work at Contoso any more, I've joined Fabrikam" came back as "works at | Fabrikam", replaces "Contoso".
    /// </summary>
    [Fact]
    public void A_new_employer_closes_the_role_held_at_the_old_one()
    {
        var role = Stored("role", "works as", "designer at Contoso");

        Corrections.Closed([role], Stored("new", "works at", "Fabrikam"), "Contoso").Select(f => f.FactId).Should().Equal("role");
        Corrections.Closed([role], Stored("new", "joined", "Fabrikam"), "Contoso").Select(f => f.FactId).Should().Equal("role");
        Corrections.Closed([Stored("job", "works at", "Google in London")], Stored("home", "lives in", "Paris"), "London")
            .Should().BeEmpty("a new home is not a new employer");
        Corrections.Closed([Stored("club", "is a member of", "the chess club in Contoso")], Stored("new", "works at", "Fabrikam"), "Contoso")
            .Should().BeEmpty("\"in\" is not a role held at the employer");
        Corrections.Closed([role], Stored("new", "works at", "Fabrikam"), "Contoso", statedNow: new HashSet<string> { "role" })
            .Should().BeEmpty("said in the same breath, it stays");
    }

    /// <summary>
    /// 38.6. The Conversational sample, as recorded: "the full marathon in May instead of the half in April" marked
    /// <c>"replaces": "the half in April"</c>. The stored plan never holds that phrase in one piece: it names the old plan
    /// by ellipsis.
    /// </summary>
    [Fact]
    public void A_correction_named_elliptically_still_closes_the_plan_it_names()
    {
        var winner = Stored("new", "is doing", "full marathon");

        Corrections.Closed([Stored("half", "is training for", "half marathon in April 2027")], winner, "the half in April")
            .Select(f => f.FactId).Should().Equal("half");
        Corrections.Closed([Stored("trip", "is planning", "a trip to Lisbon in April")], winner, "the half in April")
            .Should().BeEmpty("\"april\" alone is a coincidence: \"half\" is not in it");
    }

    /// <summary>
    /// 38.6, the same sample on a rerun: the plan was stored without its month ("half marathon"), so not even every word
    /// of "the half in April" is in it. The correction's own word (half) is, and the old plan shares "marathon" with the new.
    /// </summary>
    [Fact]
    public void A_plan_named_by_ellipsis_closes_when_both_sides_agree()
    {
        var winner = Stored("new", "is doing", "full marathon");

        Corrections.Closed([Stored("half", "is training for", "half marathon")], winner, "the half in April")
            .Select(f => f.FactId).Should().Equal("half");
        Corrections.Closed([Stored("half", "bought shoes for", "half marathon")], winner, "the half in April")
            .Should().BeEmpty("not a plan");
        Corrections.Closed([Stored("half", "is training for", "half marathon")], Stored("job", "works at", "Contoso"), "the half")
            .Should().BeEmpty("the correction is not a plan");
        Corrections.Closed([Stored("swim", "is training for", "half-hour swims")], winner, "the half in April")
            .Should().BeEmpty("it shares nothing with the new plan");
    }

    [Fact]
    public void A_changed_plan_closes_the_plan_and_leaves_a_mention_that_is_not_a_plan()
    {
        var winner = Stored("new", "plans to run", "full marathon in May 2027");
        Fact[] stored = [Stored("a", "is training for", "half marathon"), Stored("b", "bought shoes for", "half marathon")];

        Corrections.Closed(stored, winner, "half marathon in April").Select(f => f.FactId)
            .Should().Equal(["a"], "the plan closes; the purchase, which is not a plan, stays");
    }

    [Theory]
    [InlineData("plans to run", true)]
    [InlineData("is training for", true)]
    [InlineData("is going to", true)]
    [InlineData("will run", true)]
    [InlineData("is running", true)]
    [InlineData("bought shoes for", false)]
    [InlineData("left", false)]
    [InlineData("works at", false)]
    public void A_plan_is_named_by_its_predicate(string predicate, bool plan)
    {
        Corrections.IsPlan(predicate).Should().Be(plan);
    }

    [Fact]
    public void A_correction_never_closes_what_is_already_closed_itself_or_an_unrelated_value()
    {
        var winner = Stored("new", "plans to run", "the full marathon in May");

        Corrections.Closed([Stored("d", "plans to run", "the half marathon in April") with { InvalidatedAtUtc = T0 }], winner, "half marathon")
            .Should().BeEmpty();
        Corrections.Closed([winner], winner, "the full marathon").Should().BeEmpty();
        Corrections.Closed([Stored("x", "plans to run", "a triathlon")], winner, "half marathon").Should().BeEmpty();
        Corrections.Closed([Stored("y", "plans to run", "halfway house")], winner, "half").Should().BeEmpty("whole words only");
    }

    [Fact]
    public void A_correction_closes_the_preference_that_names_the_replaced_value_as_whole_words()
    {
        Preference P(string id, string text) => new() { PreferenceId = id, Category = "music", PreferenceText = text, Confidence = 1, CreatedAtUtc = T0 };
        var winner = P("new", "likes Arcade Fire");

        Corrections.Closes(P("a", "likes Radiohead"), winner, "Radiohead").Should().BeTrue();
        Corrections.Closes(P("b", "likes Radioheadish noise"), winner, "Radiohead").Should().BeFalse();
        Corrections.Closes(P("c", "likes jazz"), winner, "Radiohead").Should().BeFalse();
    }

    // ── The write ─────────────────────────────────────────────────────────────────────────────────

    private readonly IFactRepository _facts = Substitute.For<IFactRepository>();
    private readonly IPreferenceRepository _preferences = Substitute.For<IPreferenceRepository>();
    private readonly List<(string Loser, string Winner)> _closedFacts = [];
    private readonly List<(string Loser, string Winner)> _closedPreferences = [];
    private readonly IClock _clock = Substitute.For<IClock>();

    public ChangesOfMindTests()
    {
        _clock.UtcNow.Returns(new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero));
        _facts.UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>()).Returns(ci => Task.FromResult(ci.Arg<Fact>()));
        _facts.FindSupersededCandidatesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<DateTimeOffset>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>([]));
        _facts.GetBySubjectAsync(Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>([Stored("half", "plans to run", "the half marathon in April")]));
        _facts.SupersedeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(ci => { _closedFacts.Add((ci.ArgAt<string>(0), ci.ArgAt<string>(1))); return Task.FromResult(true); });
        _preferences.UpsertAsync(Arg.Any<Preference>(), Arg.Any<CancellationToken>()).Returns(ci => Task.FromResult(ci.Arg<Preference>()));
        _preferences.GetByCategoryAsync("music", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Preference>>(
            [
                new Preference { PreferenceId = "radiohead", Category = "music", PreferenceText = "likes Radiohead", Confidence = 1, CreatedAtUtc = T0 },
            ]));
        _preferences.SupersedeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(ci => { _closedPreferences.Add((ci.ArgAt<string>(0), ci.ArgAt<string>(1))); return Task.FromResult(true); });
    }

    private PersistenceStage Sut(bool supersede, IEntityRepository? entities = null, IRelationshipRepository? relationships = null)
    {
        var embeddings = Substitute.For<IEmbeddingOrchestrator>();
        embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[8]);
        var ids = Substitute.For<IIdGenerator>();
        ids.GenerateId().Returns(_ => Guid.NewGuid().ToString("N"));
        return new PersistenceStage(embeddings, entities ?? Substitute.For<IEntityRepository>(), _facts, _preferences,
            relationships ?? Substitute.For<IRelationshipRepository>(), _clock, ids, NullLogger<PersistenceStage>.Instance,
            new PassThroughMemoryPersistenceTransaction(),
            Options.Create(new ExtractionOptions { SupersedeReplacedFacts = supersede, EnableBatchMemoryUpserts = false }));
    }

    [Fact]
    public async Task A_marked_correction_closes_the_fact_and_the_preference_it_replaces()
    {
        var extraction = new ExtractionStageResult
        {
            FilteredFacts = [F("user", "plans to run", "the full marathon in May") with { Replaces = "the half marathon" }],
            FilteredPreferences = [new ExtractedPreference { Category = "music", PreferenceText = "likes Arcade Fire", Replaces = "Radiohead" }],
        };

        await Sut(supersede: true).PersistAsync(extraction, ownerId: "owner-1");

        _closedFacts.Should().ContainSingle().Which.Loser.Should().Be("half");
        _closedPreferences.Should().ContainSingle().Which.Loser.Should().Be("radiohead");
    }

    /// <summary>
    /// 38.6, the Conversational sample as recorded: the plan fact closed, its edge "Lena —is training for→ half marathon"
    /// stayed live, and the agent answered from the edge. The edge the closed fact mirrors ends with it; an edge of another
    /// relation, one already ended, and one a still-live fact keeps saying all stay.
    /// </summary>
    [Fact]
    public async Task A_correction_ends_the_edge_its_closed_fact_mirrors()
    {
        var entities = Substitute.For<IEntityRepository>();
        var relationships = Substitute.For<IRelationshipRepository>();
        Entity E(string id, string name) => new() { EntityId = id, Name = name, Type = "EVENT", Confidence = 1, CreatedAtUtc = T0 };
        Relationship R(string id, string type, string target) => new()
        {
            RelationshipId = id, SourceEntityId = "lena", TargetEntityId = target, RelationshipType = type, Confidence = 1, CreatedAtUtc = T0,
        };
        entities.FindLiveByNameAsync("user", null, Arg.Any<MemoryScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Entity?>(E("lena", "Lena")));
        entities.GetByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ci => Task.FromResult<Entity?>(ci.Arg<string>() switch
        {
            "half" => E("half", "half marathon"),
            "relay" => E("relay", "relay"),
            "lyon" => E("lyon", "Lyon"),
            _ => null,
        }));
        relationships.GetBySourceEntityAsync("lena", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Relationship>>(
            [
                R("e-half", "PLANS_TO_RUN", "half"),
                R("e-half-bare", "planning to run", "half"),
                R("e-ended", "plans to run", "half") with { ValidUntil = T0.AddDays(-30) },
                R("e-relay", "plans to run", "relay"),
                R("e-lyon", "lives in", "lyon"),
            ]));
        relationships.EndAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        _facts.GetBySubjectAsync(Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>(
                [Stored("half", "plans to run", "the half marathon in April"), Stored("relay", "plans to run", "the relay in June")]));

        await Sut(supersede: true, entities, relationships).PersistAsync(
            new ExtractionStageResult { FilteredFacts = [F("user", "plans to run", "the full marathon in May") with { Replaces = "the half marathon" }] },
            ownerId: "owner-1");

        _closedFacts.Should().ContainSingle().Which.Loser.Should().Be("half");
        await relationships.Received(1).EndAsync("e-half", Arg.Any<DateTimeOffset>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
        await relationships.Received(1).EndAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>38.6, the sample's next run: the fact said "is training for", its edge TRAINING_FOR.</summary>
    [Fact]
    public async Task The_edge_ends_when_its_type_drops_the_facts_auxiliary()
    {
        var entities = Substitute.For<IEntityRepository>();
        var relationships = Substitute.For<IRelationshipRepository>();
        entities.FindLiveByNameAsync("user", null, Arg.Any<MemoryScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Entity?>(new Entity { EntityId = "lena", Name = "Lena", Type = "PERSON", Confidence = 1, CreatedAtUtc = T0 }));
        entities.GetByIdAsync("half", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Entity?>(new Entity { EntityId = "half", Name = "half marathon", Type = "EVENT", Confidence = 1, CreatedAtUtc = T0 }));
        relationships.GetBySourceEntityAsync("lena", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Relationship>>(
            [
                new Relationship
                {
                    RelationshipId = "e-half", SourceEntityId = "lena", TargetEntityId = "half", RelationshipType = "TRAINING_FOR",
                    Confidence = 1, CreatedAtUtc = T0,
                },
            ]));
        relationships.EndAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        _facts.GetBySubjectAsync(Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>([Stored("half", "is training for", "half marathon in April 2027")]));

        await Sut(supersede: true, entities, relationships).PersistAsync(
            new ExtractionStageResult { FilteredFacts = [F("user", "is doing", "full marathon") with { Replaces = "the half in April" }] },
            ownerId: "owner-1");

        _closedFacts.Should().ContainSingle().Which.Loser.Should().Be("half");
        await relationships.Received(1).EndAsync("e-half", Arg.Any<DateTimeOffset>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 38.6, the same sample: "lives in | Lyon" was superseded by the new home, and "Lena —lives in→ Lyon" stayed live (the
    /// new home's edge was "moved to", so the edge-level replacement of 36.4 never saw a new "lives in").
    /// </summary>
    [Fact]
    public async Task Supersession_ends_the_edge_its_closed_fact_mirrors()
    {
        var entities = Substitute.For<IEntityRepository>();
        var relationships = Substitute.For<IRelationshipRepository>();
        entities.FindLiveByNameAsync("Lena", null, Arg.Any<MemoryScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Entity?>(new Entity { EntityId = "lena", Name = "Lena", Type = "PERSON", Confidence = 1, CreatedAtUtc = T0 }));
        entities.GetByIdAsync("lyon", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Entity?>(new Entity { EntityId = "lyon", Name = "Lyon", Type = "LOCATION", Confidence = 1, CreatedAtUtc = T0 }));
        relationships.GetBySourceEntityAsync("lena", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Relationship>>(
            [
                new Relationship
                {
                    RelationshipId = "e-lyon", SourceEntityId = "lena", TargetEntityId = "lyon", RelationshipType = "LIVES_IN",
                    Confidence = 1, CreatedAtUtc = T0,
                },
            ]));
        relationships.EndAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        _facts.FindSupersededCandidatesAsync(Arg.Any<string>(), "Lena", Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<DateTimeOffset>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>([Stored("lyon-fact", "lives in", "Lyon") with { Subject = "Lena" }]));

        await Sut(supersede: true, entities, relationships).PersistAsync(
            new ExtractionStageResult { FilteredFacts = [F("Lena", "lives in", "Copenhagen")] }, ownerId: "owner-1");

        _closedFacts.Should().ContainSingle().Which.Loser.Should().Be("lyon-fact");
        await relationships.Received(1).EndAsync("e-lyon", Arg.Any<DateTimeOffset>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 38.6, the sample's third run: "half marathon | takes place in | 2027-04", stored from the plan's own words, outlived
    /// the plan, and the agent called the old race "a milestone along the way".
    /// </summary>
    [Fact]
    public async Task A_withdrawn_plan_takes_its_own_date_with_it()
    {
        Fact From(Fact fact, string message) => fact with { SourceMessageIds = [message] };
        _facts.GetBySubjectAsync("user", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>([From(Stored("half", "is training for", "Half marathon in April 2027"), "m1")]));
        _facts.GetBySubjectAsync("half marathon", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>(
            [
                From(Stored("date", "takes place in", "2027-04") with { Subject = "half marathon" }, "m1"),
                From(Stored("other-day", "takes place in", "2026-04") with { Subject = "half marathon" }, "m9"),
                From(Stored("route", "starts at", "the old harbour") with { Subject = "half marathon" }, "m1"),
                From(Stored("history", "first held in", "1998") with { Subject = "half marathon" }, "m1"),
                From(Stored("cap", "is on", "3000") with { Subject = "half marathon" }, "m1"),
            ]));

        await Sut(supersede: true).PersistAsync(
            new ExtractionStageResult { FilteredFacts = [F("user", "is doing", "full marathon") with { Replaces = "the half in April" }] },
            ownerId: "owner-1");

        _closedFacts.Select(c => c.Loser).Should().BeEquivalentTo(["half", "date"],
            "the date said with the plan goes; a date said another time, its history, a number and a detail that is not a date stay");
    }

    /// <summary>38.6 review: the race's date stays while someone else still has a live edge to it (the brother runs it).</summary>
    [Fact]
    public async Task A_withdrawn_plan_leaves_the_date_of_a_race_someone_else_still_runs()
    {
        var entities = Substitute.For<IEntityRepository>();
        var relationships = Substitute.For<IRelationshipRepository>();
        Entity E(string id, string name) => new() { EntityId = id, Name = name, Type = "EVENT", Confidence = 1, CreatedAtUtc = T0 };
        entities.FindLiveByNameAsync("user", null, Arg.Any<MemoryScope>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<Entity?>(E("lena", "Lena")));
        entities.FindLiveByNameAsync("half marathon", null, Arg.Any<MemoryScope>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<Entity?>(E("half", "half marathon")));
        relationships.GetByTargetEntityAsync("half", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Relationship>>(
            [
                new Relationship
                {
                    RelationshipId = "brother-runs", SourceEntityId = "brother", TargetEntityId = "half", RelationshipType = "RUNNING",
                    Confidence = 1, CreatedAtUtc = T0,
                },
            ]));
        _facts.GetBySubjectAsync("user", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>([Stored("half", "is training for", "half marathon in April") with { SourceMessageIds = ["m1"] }]));
        _facts.GetBySubjectAsync("half marathon", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>([Stored("date", "takes place in", "2027-04") with { Subject = "half marathon", SourceMessageIds = ["m1"] }]));

        await Sut(supersede: true, entities, relationships).PersistAsync(
            new ExtractionStageResult { FilteredFacts = [F("user", "is doing", "full marathon") with { Replaces = "the half in April" }] },
            ownerId: "owner-1");

        _closedFacts.Select(c => c.Loser).Should().Equal("half");
    }

    /// <summary>
    /// 38.6 review: "worked at" is history (36.4). A new employer ends the WORKS_AT edge, never the WORKED_AT one, whose
    /// fact stays live.
    /// </summary>
    [Fact]
    public async Task Supersession_leaves_the_edge_of_a_history_form()
    {
        var entities = Substitute.For<IEntityRepository>();
        var relationships = Substitute.For<IRelationshipRepository>();
        entities.FindLiveByNameAsync("Lena", null, Arg.Any<MemoryScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Entity?>(new Entity { EntityId = "lena", Name = "Lena", Type = "PERSON", Confidence = 1, CreatedAtUtc = T0 }));
        entities.GetByIdAsync("google", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Entity?>(new Entity { EntityId = "google", Name = "Google", Type = "ORGANIZATION", Confidence = 1, CreatedAtUtc = T0 }));
        Relationship R(string id, string type) => new()
        {
            RelationshipId = id, SourceEntityId = "lena", TargetEntityId = "google", RelationshipType = type, Confidence = 1, CreatedAtUtc = T0,
        };
        relationships.GetBySourceEntityAsync("lena", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Relationship>>([R("e-works", "WORKS_AT"), R("e-worked", "WORKED_AT")]));
        relationships.EndAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        _facts.FindSupersededCandidatesAsync(Arg.Any<string>(), "Lena", Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<DateTimeOffset>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>([Stored("google-fact", "works at", "Google") with { Subject = "Lena" }]));

        await Sut(supersede: true, entities, relationships).PersistAsync(
            new ExtractionStageResult { FilteredFacts = [F("Lena", "works at", "Meta")] }, ownerId: "owner-1");

        await relationships.Received(1).EndAsync("e-works", Arg.Any<DateTimeOffset>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
        await relationships.DidNotReceive().EndAsync("e-worked", Arg.Any<DateTimeOffset>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("Half marathon in April 2027", "Half marathon")]
    [InlineData("the half marathon", "half marathon")]
    [InlineData("full marathon in May", "full marathon")]
    [InlineData("2027-04", "")]
    public void Undated_keeps_the_event_and_its_case(string text, string expected) =>
        Corrections.Undated(text).Should().Be(expected);

    [Theory]
    [InlineData("2027-04", true)]
    [InlineData("in April 2027", true)]
    [InlineData("on 12 May", true)]
    [InlineData("1998", true)]
    [InlineData("the old harbour", false)]
    [InlineData("France", false)]
    [InlineData("3000", false)]
    [InlineData("21", false)]
    public void A_date_only_value_is_told_apart(string text, bool expected) =>
        Corrections.IsDateOnly(text).Should().Be(expected);

    [Fact]
    public async Task A_closed_fact_keeps_its_edge_while_a_live_fact_still_says_it()
    {
        var entities = Substitute.For<IEntityRepository>();
        var relationships = Substitute.For<IRelationshipRepository>();
        entities.FindLiveByNameAsync("user", null, Arg.Any<MemoryScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Entity?>(new Entity { EntityId = "lena", Name = "Lena", Type = "PERSON", Confidence = 1, CreatedAtUtc = T0 }));
        entities.GetByIdAsync("half", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Entity?>(new Entity { EntityId = "half", Name = "half marathon", Type = "EVENT", Confidence = 1, CreatedAtUtc = T0 }));
        relationships.GetBySourceEntityAsync("lena", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Relationship>>(
            [
                new Relationship
                {
                    RelationshipId = "e-half", SourceEntityId = "lena", TargetEntityId = "half", RelationshipType = "plans to run",
                    Confidence = 1, CreatedAtUtc = T0,
                },
            ]));
        // "The half marathon in April, not March": the new plan names the same race, so its edge is still said.
        await Sut(supersede: true, entities, relationships).PersistAsync(
            new ExtractionStageResult { FilteredFacts = [F("user", "plans to run", "the half marathon in May") with { Replaces = "the half marathon in April" }] },
            ownerId: "owner-1");

        _closedFacts.Should().ContainSingle();
        await relationships.DidNotReceive().EndAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Without_supersession_a_marked_correction_closes_nothing()
    {
        var extraction = new ExtractionStageResult
        {
            FilteredFacts = [F("user", "plans to run", "the full marathon in May") with { Replaces = "the half marathon" }],
            FilteredPreferences = [new ExtractedPreference { Category = "music", PreferenceText = "likes Arcade Fire", Replaces = "Radiohead" }],
        };

        await Sut(supersede: false).PersistAsync(extraction, ownerId: "owner-1");

        _closedFacts.Should().BeEmpty();
        _closedPreferences.Should().BeEmpty();
    }

    [Fact]
    public async Task The_shapes_run_with_supersession_only()
    {
        var extraction = new ExtractionStageResult { FilteredFacts = [F("Bruno", "is", "7 years old")] };

        await Sut(supersede: true).PersistAsync(extraction, ownerId: "owner-1");
        await Sut(supersede: false).PersistAsync(extraction, ownerId: "owner-1");

        await _facts.Received(1).UpsertAsync(Arg.Is<Fact>(f => f.Predicate == "age" && f.Object == "7"), Arg.Any<CancellationToken>());
        await _facts.Received(1).UpsertAsync(Arg.Is<Fact>(f => f.Predicate == "is" && f.Object == "7 years old"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_value_that_has_already_ended_does_not_replace_the_current_one()
    {
        var extraction = new ExtractionStageResult
        {
            FilteredFacts = [F("user", "worked at", "Google") with { ValidUntil = new DateTimeOffset(2019, 12, 31, 0, 0, 0, TimeSpan.Zero) }],
        };

        await Sut(supersede: true).PersistAsync(extraction, ownerId: "owner-1");

        await _facts.DidNotReceive().FindSupersededCandidatesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    // ── The extractor is asked, and hands the value on ───────────────────────────────────────────

    private const string Marker = "add \"replaces\"";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Every_prompt_asks_for_it_exactly_when_on(bool on)
    {
        LlmUnifiedMemoryExtractor.BuildSystemPrompt(AssistantContentMode.Ignore, [], TemporalValidityMode.Ignore,
                ExtractionProvenanceMode.Batch, markCorrections: on)
            .Contains(Marker, StringComparison.Ordinal).Should().Be(on);
        LlmMultiSessionUnifiedMemoryExtractor.BuildSystemPrompt(null, markCorrections: on)
            .Contains(Marker, StringComparison.Ordinal).Should().Be(on);
        LlmFactExtractor.BuildSystemPrompt(AssistantContentMode.Ignore, TemporalValidityMode.Ignore,
                ExtractionProvenanceMode.Batch, markCorrections: on)
            .Contains(Marker, StringComparison.Ordinal).Should().Be(on);
    }

    [Fact]
    public async Task The_extractor_hands_the_replaced_value_on()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                """{"entities":[],"facts":[{"subject":"user","predicate":"plans to run","object":"full marathon","confidence":0.9,"replaces":"half marathon"}],"preferences":[{"category":"music","preference":"likes Arcade Fire","confidence":0.9,"replaces":"Radiohead"}],"relations":[]}"""))));
        var sut = new LlmUnifiedMemoryExtractor(client,
            Options.Create(new LlmExtractionOptions { UseUnifiedExtraction = true, MarkCorrections = true }),
            NullLogger<LlmUnifiedMemoryExtractor>.Instance);

        var result = await sut.ExtractAsync([new Message
        {
            MessageId = "m", ConversationId = "c", SessionId = "s", Role = "user",
            Content = "Actually the full marathon, not the half. And Arcade Fire, not Radiohead.", TimestampUtc = T0,
        }]);

        result.Facts.Should().ContainSingle().Which.Replaces.Should().Be("half marathon");
        result.Preferences.Should().ContainSingle().Which.Replaces.Should().Be("Radiohead");
    }

    // ── Review round 1 ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Only_a_completed_move_that_has_happened_states_a_home_and_never_over_a_stated_one()
    {
        ReplacementShapes.Prepare([F("Nadia", "moving to", "Copenhagen")], Places, T0).Should().ContainSingle("not yet moved");
        ReplacementShapes.Prepare([F("Nadia", "moved to", "Copenhagen") with { ValidFrom = T0.AddDays(10) }], Places, T0)
            .Should().ContainSingle("a move dated after now has not happened");
        ReplacementShapes.Prepare([F("Nadia", "moved to", "Copenhagen"), F("Nadia", "lives in", "Paris")], Places, T0)
            .Select(f => $"{f.Predicate} {f.Object}").Should().Equal("moved to Copenhagen", "lives in Paris");
        ReplacementShapes.Prepare([F("Nadia", "moved to", "Copenhagen"), F("Nadia", "likes", "jazz")], Places, T0)
            .Select(f => $"{f.Predicate} {f.Object}").Should().Equal("moved to Copenhagen", "lives in Copenhagen", "likes jazz");
    }

    [Fact]
    public void A_correction_never_closes_a_coincidental_one_word_mention()
    {
        var winner = Stored("new", "age", "7");
        Corrections.Closed([Stored("w", "weighs", "6 kg")], winner, "6").Should().BeEmpty();
        Corrections.Closed([Stored("b", "born in", "London")], Stored("n", "works for", "Meta"), "Google in London").Should().BeEmpty();
        Corrections.Closed([Stored("a", "age", "6"), Stored("w", "weighs", "6 kg")], winner, "6").Select(f => f.FactId).Should().Equal("a");
    }

    [Fact]
    public async Task A_correction_runs_before_supersession_so_it_names_what_it_replaces()
    {
        var order = new List<string>();
        _facts.GetBySubjectAsync(Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(ci => { order.Add("correction"); return Task.FromResult<IReadOnlyList<Fact>>([]); });
        _facts.FindSupersededCandidatesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<DateTimeOffset>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(ci => { order.Add("supersession"); return Task.FromResult<IReadOnlyList<Fact>>([]); });

        await Sut(supersede: true).PersistAsync(
            new ExtractionStageResult { FilteredFacts = [F("Bruno", "age", "7") with { Replaces = "6" }] }, ownerId: "owner-1");

        order.Should().Equal("correction", "supersession");
    }

    [Fact]
    public async Task A_shared_write_closes_what_its_correction_replaces()
    {
        // No owner: the reads are shared-only; the supersede statement gets no owner filter (a filter on the
        // shared-only placeholder owner matched nothing).
        await Sut(supersede: true).PersistAsync(
            new ExtractionStageResult { FilteredFacts = [F("user", "plans to run", "the full marathon in May") with { Replaces = "the half marathon" }] },
            ownerId: null);

        _closedFacts.Should().ContainSingle();
        await _facts.Received().SupersedeAsync("half", Arg.Any<string>(), null, Arg.Any<CancellationToken>());
    }

    // ── Review round 2 ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void History_does_not_block_the_new_home_and_two_moves_both_state_theirs()
    {
        ReplacementShapes.Prepare([F("user", "lived in", "Paris"), F("user", "moved to", "Copenhagen")], Places, T0)
            .Select(f => $"{f.Predicate} {f.Object}").Should().Contain("lives in Copenhagen", "\"lived in Paris\" is history");
        ReplacementShapes.Prepare([F("user", "lives in", "Paris") with { ValidUntil = T0.AddDays(-1) }, F("user", "moved to", "Copenhagen")], Places, T0)
            .Select(f => $"{f.Predicate} {f.Object}").Should().Contain("lives in Copenhagen", "a residence that ended is not current");

        string? Two(string name) => name is "Copenhagen" or "Oslo" ? "LOCATION" : null;
        ReplacementShapes.Prepare([F("user", "moved to", "Copenhagen"), F("user", "moved to", "Oslo")], Two, T0)
            .Select(f => $"{f.Predicate} {f.Object}")
            .Should().Equal("moved to Copenhagen", "lives in Copenhagen", "moved to Oslo", "lives in Oslo");
    }

    [Fact]
    public void The_self_words_are_one_speaker()
    {
        ReplacementShapes.Prepare([F("user", "lives in", "Paris"), F("I", "moved to", "Copenhagen")], Places, T0)
            .Select(f => $"{f.Predicate} {f.Object}").Should().NotContain("lives in Copenhagen", "the speaker stated where they live");
    }

    /// <summary>Run 4: "Arcade Fire, not Radiohead" came without a marked correction, and both favourites stayed live.</summary>
    [Fact]
    public async Task A_new_favourite_preference_replaces_the_old_one_marked_or_not()
    {
        _preferences.GetByCategoryAsync("music", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Preference>>(
            [
                new Preference { PreferenceId = "old", Category = "music", PreferenceText = "Favourite band is Radiohead", Confidence = 1, CreatedAtUtc = T0 },
                new Preference { PreferenceId = "jazz", Category = "music", PreferenceText = "likes jazz", Confidence = 1, CreatedAtUtc = T0 },
            ]));

        await Sut(supersede: true).PersistAsync(new ExtractionStageResult
        {
            FilteredPreferences = [new ExtractedPreference { Category = "music", PreferenceText = "Favourite band is Arcade Fire, not Radiohead" }],
        }, ownerId: "owner-1");

        _closedPreferences.Select(c => c.Loser).Should().Equal("old");
        Corrections.SingleValuedRelation("Favourite bands are Radiohead and Blur").Should().BeNull("a plural holds several");
        Corrections.SingleValuedRelation("likes jazz").Should().BeNull();
    }
}
