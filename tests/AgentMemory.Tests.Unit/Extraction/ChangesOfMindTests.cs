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
    [InlineData("lived in", true)]
    [InlineData("age", true)]
    [InlineData("favourite band", true)]
    [InlineData("favorite food", true)]
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
        MemoryRelationCardinality.ReplacedKeys("works for").Should().Contain(["works at", "works for", "worked at"]);
        MemoryRelationCardinality.ReplacedKeys("favourite band").Should().Equal("favourite band");
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
        var shaped = ReplacementShapes.Prepare([F("Nadia", "moved to", "Copenhagen")], Places);
        shaped.Select(f => $"{f.Predicate} {f.Object}").Should().Equal("moved to Copenhagen", "lives in Copenhagen");

        var alreadySaid = ReplacementShapes.Prepare([F("Nadia", "moved to", "Copenhagen"), F("Nadia", "lives in", "Copenhagen")], Places);
        alreadySaid.Should().HaveCount(2, "a state the turn already stated is not written twice");
    }

    [Theory]
    [InlineData("the analytics team")]
    [InlineData("somewhere untyped")]
    public void Moving_to_something_that_is_not_a_place_states_no_home(string @object)
    {
        ReplacementShapes.Prepare([F("Tomás", "moved to", @object)], Places).Should().ContainSingle();
    }

    // ── What a marked correction closes ──────────────────────────────────────────────────────────

    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private static Fact Stored(string id, string predicate, string @object) => new()
    {
        FactId = id, Subject = "user", Predicate = predicate, Object = @object, Confidence = 0.9, CreatedAtUtc = T0,
    };

    [Fact]
    public void A_correction_closes_the_fact_that_stated_the_replaced_value_and_nothing_else()
    {
        var winner = Stored("new", "plans to run", "the full marathon in May");

        Corrections.Closes(Stored("a", "plans to run", "the half marathon in April"), winner, "the half marathon").Should().BeTrue();
        Corrections.Closes(Stored("b", "weighs", "half marathon medal"), winner, "the half marathon").Should().BeFalse(
            "another relation that merely mentions the value is left alone");
        Corrections.Closes(Stored("c", "owns", "half marathon"), winner, "half marathon").Should().BeTrue(
            "an object that IS the replaced value is closed whatever the relation");
        Corrections.Closes(Stored("d", "plans to run", "the half marathon in April") with { InvalidatedAtUtc = T0 }, winner, "half marathon")
            .Should().BeFalse("an already-closed fact is not closed again");
        Corrections.Closes(winner, winner, "the full marathon").Should().BeFalse();
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
                Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
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

    private PersistenceStage Sut(bool supersede)
    {
        var embeddings = Substitute.For<IEmbeddingOrchestrator>();
        embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[8]);
        var ids = Substitute.For<IIdGenerator>();
        ids.GenerateId().Returns(_ => Guid.NewGuid().ToString("N"));
        return new PersistenceStage(embeddings, Substitute.For<IEntityRepository>(), _facts, _preferences,
            Substitute.For<IRelationshipRepository>(), _clock, ids, NullLogger<PersistenceStage>.Instance,
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
            Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
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
}
