using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;
using AgentMemory.Core.Resolution;
using AgentMemory.Core.Services;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Resolution;

/// <summary>
/// ILC-08: evidence-based identity re-checks a store-aware writer's stored-entity reference. Reproduces the full
/// Bariloche run: the writer named "the cutover" after the stored go-live it was shown, identity re-decided with the
/// current sentence only and kept a separate Pending event, so the go-live's place and presence were unreachable.
/// </summary>
public sealed class IdentityWriterReferenceTests
{
    private const string EventName = "Acme Logistics billing system go-live";
    private const string CurrentText = "The cutover happens on-site at Acme's office in Cordoba, and I have to be there in person to run it.";
    private const string Quote = "The cutover happens on-site";

    private readonly IIdentityAdjudicator _model = Substitute.For<IIdentityAdjudicator>();
    private int _modelCalls;

    public IdentityWriterReferenceTests()
    {
        _model.GetDecisionAsync(Arg.Any<IdentityAdjudicationRequest>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _modelCalls++;
            return "{\"decision\":\"uncertain\",\"candidateId\":null,\"evidence\":[]}";
        });
    }

    private static IdentityCandidate Candidate(string id, string name = EventName, string type = "EVENT", string? description = null,
        string[]? knownAs = null) => new()
    {
        Entity = new() { EntityId = id, Name = name, Type = type, OwnerId = "bruna", Confidence = 1, Description = description,
            SourceMessageIds = ["prior-" + id], CreatedAtUtc = DateTimeOffset.UnixEpoch },
        KnownAs = knownAs ?? [],
    };

    private async Task<IdentityDecision> Plan(ExtractedEntityReference? reference, IdentityCandidate[] found,
        bool allowContextualConfirmation = true, bool withModel = true, string type = "EVENT", string text = CurrentText,
        IIdentityDecisionRepository? store = null, string name = EventName, string[]? aliases = null)
    {
        var candidates = Substitute.For<IIdentityCandidateProvider>();
        candidates.Capabilities.Returns(new IdentityCandidateCapabilities { BoundedReads = true, CalendarValueLookup = true, VerifiedKeyLookup = true });
        candidates.FindExactCandidatesAsync(Arg.Any<IdentityCandidateQuery>(), Arg.Any<CancellationToken>())
            .Returns(new IdentityCandidateSet { ExactIdentityLookupComplete = true, Candidates = found });
        candidates.FindApproximateCandidatesAsync(Arg.Any<IdentityCandidateQuery>(), Arg.Any<float[]?>(), Arg.Any<CancellationToken>())
            .Returns(new IdentityCandidateSet { ExactIdentityLookupComplete = true });
        store ??= Substitute.For<IIdentityDecisionRepository>();
        store.IsEnabled.Returns(true); store.SupportsAtomicIngestion.Returns(true);
        var embeddings = Substitute.For<IEmbeddingOrchestrator>();
        embeddings.EmbedEntityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<float>());
        var ids = Substitute.For<IIdGenerator>(); ids.GenerateId().Returns("temporary");
        var options = new IdentityResolutionOptions { Enabled = true, MaxCandidates = 4, MaxAdjudicationsPerIngestion = withModel ? 2 : 0 };
        options.Policies[type] = new() { AllowContextualConfirmation = allowContextualConfirmation };
        var ingestion = new IdentityIngestion(IdentityConfiguration.Capture(options), TimeProvider.System,
            candidates, store, embeddings, ids, adjudicator: withModel ? _model : null);
        var plan = await ingestion.PlanAsync(new() { Name = name, Type = type, Confidence = 1, Reference = reference, Aliases = aliases ?? [] },
            new() { Targets = [new() { MessageId = "current", Content = text, Role = "user", SessionId = "s",
                ConversationId = "c", TimestampUtc = DateTimeOffset.UnixEpoch }] },
            MemoryScope.For("bruna", false), default);
        return plan.Decision;
    }

    [Fact]
    public async Task A_supported_writer_reference_confirms_when_the_type_policy_allows_contextual_confirmation()
    {
        var decision = await Plan(new() { EntityId = "event-a", Quote = Quote }, [Candidate("event-a")]);
        decision.Status.Should().Be(IdentityDecisionKind.Confirmed);
        decision.ConfirmedEntityId.Should().Be("event-a");
        decision.Diagnostics.Should().Contain("writer_reference_confirmed");
        decision.ContextualEvidence!.Verdict.Should().Be(IdentityAdjudicationVerdict.Same);
        decision.ContextualEvidence.Citations.Should().ContainSingle(c => c.SourceId == "current" && c.Quote == Quote);
        decision.Observation.SourceMessageIds.Should().Equal("current");
        decision.Observation.Name.Should().Be(EventName);
        _modelCalls.Should().Be(0, "the writer's reference already carries the judgment; no second paid call");
    }

    [Fact]
    public async Task A_writer_reference_works_without_any_adjudicator()
    {
        var decision = await Plan(new() { EntityId = "event-a", Quote = Quote }, [Candidate("event-a")], withModel: false);
        decision.Status.Should().Be(IdentityDecisionKind.Confirmed);
        decision.ConfirmedEntityId.Should().Be("event-a");
    }

    [Fact]
    public async Task Without_contextual_confirmation_the_reference_is_kept_for_review_and_no_model_is_called()
    {
        var decision = await Plan(new() { EntityId = "event-a", Quote = Quote }, [Candidate("event-a")], allowContextualConfirmation: false);
        decision.Status.Should().Be(IdentityDecisionKind.Pending);
        decision.PendingEntityIds.Should().Equal("event-a");
        decision.Diagnostics.Should().Contain("writer_reference_requires_review");
        decision.ContextualEvidence!.CandidateId.Should().Be("event-a");
        _modelCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_reference_to_an_entity_that_is_not_a_candidate_falls_back_to_the_adjudicator()
    {
        var decision = await Plan(new() { EntityId = "elsewhere", Quote = Quote }, [Candidate("event-a")]);
        decision.Status.Should().Be(IdentityDecisionKind.Pending);
        decision.Diagnostics.Should().Contain("writer_reference_not_candidate").And.Contain("contextual_uncertain");
        _modelCalls.Should().Be(1);
    }

    [Fact]
    public async Task Two_identically_named_candidates_make_the_reference_ambiguous()
    {
        var decision = await Plan(new() { EntityId = "event-a", Quote = Quote }, [Candidate("event-a"), Candidate("event-b")]);
        decision.Status.Should().Be(IdentityDecisionKind.Pending);
        decision.ConfirmedEntityId.Should().BeNull();
        decision.Diagnostics.Should().Contain("writer_reference_ambiguous");
        _modelCalls.Should().Be(1);
    }

    [Theory]
    [InlineData("event-a")]
    [InlineData("event-b")]
    public async Task Reversed_identical_candidates_stay_ambiguous_for_either_reference(string referenced)
    {
        // ILCS CROSS-02 candidate-order-reversed: the order a backend returns identical candidates in cannot pick a winner.
        var decision = await Plan(new() { EntityId = referenced, Quote = Quote }, [Candidate("event-b"), Candidate("event-a")]);
        decision.Status.Should().Be(IdentityDecisionKind.Pending);
        decision.ConfirmedEntityId.Should().BeNull();
        decision.PendingEntityIds.Should().BeEquivalentTo(["event-a", "event-b"]);
        decision.Diagnostics.Should().Contain("writer_reference_ambiguous");
    }

    [Fact]
    public async Task A_quote_absent_from_the_current_message_cannot_support_the_reference()
    {
        var decision = await Plan(new() { EntityId = "event-a", Quote = "the go-live on December 11" }, [Candidate("event-a")]);
        decision.Status.Should().Be(IdentityDecisionKind.Pending);
        decision.Diagnostics.Should().Contain("writer_reference_unsupported_quote");
    }

    [Fact]
    public async Task A_vetoed_candidate_cannot_be_confirmed_by_a_reference()
    {
        var decision = await Plan(new() { EntityId = "event-a", Quote = Quote }, [Candidate("event-a", type: "PERSON")]);
        decision.Status.Should().NotBe(IdentityDecisionKind.Confirmed);
        decision.ConfirmedEntityId.Should().BeNull();
        decision.Diagnostics.Should().Contain("writer_reference_not_candidate");
    }

    [Fact]
    public async Task A_reference_to_an_observation_already_confirmed_elsewhere_resolves_to_its_identity()
    {
        // Every observation is an Entity node; the writer can be shown one that a decision already confirmed into the
        // event. It is no candidate itself, so the reference is resolved through that decision (owner-scoped).
        var store = Substitute.For<IIdentityDecisionRepository>();
        store.GetAsync("identity-decision-k1", "bruna", Arg.Any<CancellationToken>()).Returns(new IdentityDecision
        {
            DecisionId = "identity-decision-k1", ObservationKey = "k1", Status = IdentityDecisionKind.Confirmed, ConfirmedEntityId = "event-a",
            Policy = new(), Observation = new() { EntityId = "identity-observation-k1", Name = EventName, Type = "EVENT", OwnerId = "bruna",
                Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch },
            Context = new(), CreatedAtUtc = DateTimeOffset.UnixEpoch, UpdatedAtUtc = DateTimeOffset.UnixEpoch,
        });
        var decision = await Plan(new() { EntityId = "identity-observation-k1", Quote = Quote }, [Candidate("event-a")], store: store);
        decision.Status.Should().Be(IdentityDecisionKind.Confirmed);
        decision.ConfirmedEntityId.Should().Be("event-a");
        decision.Diagnostics.Should().Contain("writer_reference_resolved").And.Contain("writer_reference_confirmed");
        _modelCalls.Should().Be(0);
    }

    [Fact]
    public async Task An_unresolvable_observation_reference_still_falls_back()
    {
        var store = Substitute.For<IIdentityDecisionRepository>();
        store.GetAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns((IdentityDecision?)null);
        var decision = await Plan(new() { EntityId = "identity-observation-unknown", Quote = Quote }, [Candidate("event-a")], store: store);
        decision.Status.Should().Be(IdentityDecisionKind.Pending);
        decision.Diagnostics.Should().Contain("writer_reference_not_candidate");
        _modelCalls.Should().Be(1);
    }

    [Fact]
    public async Task No_reference_leaves_the_existing_decision_and_diagnostics_unchanged()
    {
        var decision = await Plan(null, [Candidate("event-a")]);
        decision.Status.Should().Be(IdentityDecisionKind.Pending);
        decision.Diagnostics.Should().NotContain(d => d.StartsWith("writer_reference", StringComparison.Ordinal));
        _modelCalls.Should().Be(1);
    }

    // ── IDENTITY-NAMESAKE-WRITER-MERGE (IKB-3, whos-who-v1 statements 8 and 13) ──

    [Theory]
    [InlineData("My niece Valentina started school this year, she is six.")]
    [InlineData("Valentina, my niece, started school this year.")]
    [InlineData("Valentina is my niece and she started school this year.")]
    public async Task A_reference_the_turn_ties_to_another_relation_than_the_stored_person_s_role_is_not_confirmed(string text)
    {
        // The store-aware writer referred "my niece Valentina" to the stored designer Valentina Ríos, and identity confirmed it:
        // the quote was in the turn and she was the only Valentina. The turn's own words tie her to the owner as a niece, which
        // the stored person is not known to be: that is not agreement, so the reference is kept for review instead.
        var decision = await Plan(new() { EntityId = "designer", Quote = "Valentina" },
            [Candidate("designer", "Valentina Ríos", "PERSON", "The designer on Atlas")], withModel: false, type: "PERSON",
            text: text, name: "Valentina Ríos");

        decision.Status.Should().NotBe(IdentityDecisionKind.Confirmed);
        decision.ConfirmedEntityId.Should().BeNull();
        decision.Diagnostics.Should().Contain("writer_reference_relation_conflict").And.NotContain("writer_reference_confirmed");
    }

    [Fact]
    public async Task A_reference_the_turn_ties_to_the_stored_person_s_own_relation_still_confirms()
    {
        var decision = await Plan(new() { EntityId = "sister", Quote = "Marta" },
            [Candidate("sister", "Marta", "PERSON", "The user's sister, a nurse in Rosario")], withModel: false, type: "PERSON",
            text: "My sister Marta called me today.", name: "Marta");

        decision.Status.Should().Be(IdentityDecisionKind.Confirmed);
        decision.ConfirmedEntityId.Should().Be("sister");
        decision.Diagnostics.Should().Contain("writer_reference_confirmed");
    }

    /// <summary>
    /// IDENTITY-HARD-CASES A2: in whos-who-v2 every run merged two people of one first name, one met as a partner's parent
    /// ("X's parents, A and B"), the other as the owner's boss: the writer referred the boss to the stored parent and the
    /// guard read the parent as known as nothing (no "parent" in its words, no reading of the list form). A person met as
    /// someone's parent, then called "my boss", is two descriptions; the reference stays for review.
    /// </summary>
    [Fact]
    public async Task A_person_met_as_someone_s_parent_is_not_the_owner_s_boss_of_that_name()
    {
        var parent = Candidate("parent", "Marta", "PERSON",
            knownAs: [.. IdentityRelationCue.FromFactsAndSources(new Entity { EntityId = "parent", Name = "Marta", Type = "PERSON",
                Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch }, [], ["Tomás's parents, Iker and Marta, live in Bilbao."])]);

        var decision = await Plan(new() { EntityId = "parent", Quote = "Marta" }, [parent], withModel: false, type: "PERSON",
            text: "My boss Marta put my name forward for the project.", name: "Marta");

        decision.Status.Should().NotBe(IdentityDecisionKind.Confirmed);
        decision.Diagnostics.Should().Contain("writer_reference_relation_conflict");
    }

    [Theory]
    [InlineData("My mum Marta called me today.")]
    [InlineData("My mother, Marta, called me today.")]
    public async Task A_person_met_as_a_parent_may_still_be_the_owner_s_mother(string text)
    {
        var parent = Candidate("parent", "Marta", "PERSON", knownAs: ["parent"]);

        var decision = await Plan(new() { EntityId = "parent", Quote = "Marta" }, [parent], withModel: false, type: "PERSON",
            text: text, name: "Marta");

        decision.Status.Should().Be(IdentityDecisionKind.Confirmed, "a mother is a parent: no disagreement");
    }

    [Theory]
    [InlineData("Tomás's parents, Iker and Marta, live in Bilbao.", "Marta", "parent")]
    [InlineData("Tomás's parents, Iker and Marta, live in Bilbao.", "Iker", "parent")]
    [InlineData("Her sister, Marta, lives in Bilbao.", "Marta", "sister")]
    public void A_relation_given_to_a_list_or_an_apposition_is_read(string text, string name, string relation) =>
        IdentityRelationCue.InText(text, [name]).Should().Contain(relation);

    [Theory]
    [InlineData("My parents, Iker and Marta, came over.", "Marta", "parent")]
    [InlineData("My mum, Marta, came over.", "Marta", "mother")]
    public void The_owner_s_relation_given_as_an_apposition_is_read(string text, string name, string relation) =>
        IdentityRelationCue.InTurn(text, [name]).Should().Contain(relation);

    [Theory]
    [InlineData(null)]
    [InlineData("Lives in Rosario and likes tango")]
    public async Task A_reference_to_a_person_with_no_known_role_still_confirms(string? description)
    {
        // Nothing stored contradicts the turn, so the writer's judgment stands as before.
        var decision = await Plan(new() { EntityId = "marta", Quote = "Marta" },
            [Candidate("marta", "Marta", "PERSON", description)], withModel: false, type: "PERSON",
            text: "My sister Marta called me today.", name: "Marta");

        decision.Status.Should().Be(IdentityDecisionKind.Confirmed);
        decision.ConfirmedEntityId.Should().Be("marta");
    }

    [Fact]
    public async Task A_turn_with_no_relation_still_confirms_a_person_with_a_role()
    {
        var decision = await Plan(new() { EntityId = "designer", Quote = "Valentina" },
            [Candidate("designer", "Valentina Ríos", "PERSON", "The designer on Atlas")], withModel: false, type: "PERSON",
            text: "Valentina finished the new checkout mock-ups today.", name: "Valentina Ríos");

        decision.Status.Should().Be(IdentityDecisionKind.Confirmed);
        decision.ConfirmedEntityId.Should().Be("designer");
    }

    [Fact]
    public async Task A_pet_the_turn_calls_another_animal_than_the_stored_one_is_not_confirmed()
    {
        var decision = await Plan(new() { EntityId = "cat", Quote = "Luna" },
            [Candidate("cat", "Luna", "ANIMAL", "A Persian cat who sleeps on the sofa")], withModel: false, type: "ANIMAL",
            text: "My dog Luna turned three last week.", name: "Luna");

        decision.Status.Should().NotBe(IdentityDecisionKind.Confirmed);
        decision.Diagnostics.Should().Contain("writer_reference_relation_conflict");
    }

    [Fact]
    public async Task A_stored_person_known_only_by_a_fact_is_held_against_the_turn_too()
    {
        // The store-aware writer gives entities no description: the designer is known as one from her fact, which the backend
        // reads into KnownAs. That is the run IKB-3 found.
        var decision = await Plan(new() { EntityId = "designer", Quote = "Valentina" },
            [Candidate("designer", "Valentina Ríos", "PERSON", knownAs: ["is the designer on Atlas"])], withModel: false, type: "PERSON",
            text: "My niece Valentina started school this year, she is six.", name: "Valentina Ríos");

        decision.Status.Should().NotBe(IdentityDecisionKind.Confirmed);
        decision.Diagnostics.Should().Contain("writer_reference_relation_conflict");
    }

    // ── IDENTITY-NICKNAME-SPLIT: the writer refers a short form ("Bea") to the stored person, under the stored name ──

    /// <summary>
    /// The writer now sends a person called by a short form under the stored name, with the short form as an alias. The turn
    /// names the short form, not the stored name, so the relation guard must read the alias too: otherwise "my niece Bea"
    /// referred to a stored architect would be confirmed, the relation unseen.
    /// </summary>
    [Fact]
    public async Task A_reference_under_a_short_form_the_turn_ties_to_another_relation_is_not_confirmed()
    {
        var decision = await Plan(new() { EntityId = "architect", Quote = "My niece Bea" },
            [Candidate("architect", "Beatriz Molina", "PERSON", "The architect on the new library")], withModel: false, type: "PERSON",
            text: "My niece Bea lost her first tooth yesterday.", name: "Beatriz Molina", aliases: ["Bea"]);

        decision.Status.Should().NotBe(IdentityDecisionKind.Confirmed);
        decision.Diagnostics.Should().Contain("writer_reference_relation_conflict").And.NotContain("writer_reference_confirmed");
    }

    [Fact]
    public async Task A_reference_under_a_short_form_with_no_relation_in_the_turn_confirms()
    {
        var decision = await Plan(new() { EntityId = "architect", Quote = "Bea finished the floor plans" },
            [Candidate("architect", "Beatriz Molina", "PERSON", "The architect on the new library")], withModel: false, type: "PERSON",
            text: "Bea finished the floor plans today.", name: "Beatriz Molina", aliases: ["Bea"]);

        decision.Status.Should().Be(IdentityDecisionKind.Confirmed);
        decision.ConfirmedEntityId.Should().Be("architect");
        decision.Observation.Aliases.Should().Equal("Bea");
    }

    [Fact]
    public void What_a_candidate_is_known_as_comes_from_the_facts_that_say_what_it_is()
    {
        var entity = new Entity { EntityId = "v", Name = "Valentina Ríos", Aliases = ["Vale"], Type = "PERSON", Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch };
        var known = IdentityRelationCue.FromFacts(entity, [
            ("Valentina Ríos", "is", "the designer on Atlas"),
            ("Vale", "works as", "a UX lead"),
            ("user", "has niece", "Valentina Ríos"),
            ("Valentina Ríos", "moved to", "the Madrid office"),
            ("Valentina Ríos", "works with", "my sister"),
            ("Marco", "is", "a designer"),
        ]);

        known.Should().Equal("is the designer on Atlas", "works as a UX lead", "has niece");
        IdentityRelationCue.Known(new IdentityCandidate { Entity = entity, KnownAs = known }).Should().BeEquivalentTo(["designer", "niece"]);
    }

    [Fact]
    public void With_no_fact_saying_what_it_is_the_candidate_s_own_sentence_does()
    {
        // IKB-TIER2-RERUN, re-run 1: no fact was stored from the designer's sentence.
        var entity = new Entity { EntityId = "v", Name = "Valentina Ríos", Type = "PERSON", Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch };

        var known = IdentityRelationCue.FromFactsAndSources(entity, [("Valentina Ríos", "moved to", "the Madrid office")],
            ["Valentina Ríos is the designer on Atlas.", "Marco is my brother and Valentina Ríos sat next to him.", ""]);

        known.Should().Equal("designer");
        IdentityRelationCue.Known(new IdentityCandidate { Entity = entity, KnownAs = known }).Should().BeEquivalentTo(["designer"]);
        IdentityRelationCue.Disagree(IdentityRelationCue.InTurn("My niece Valentina started school this year.", ["Valentina Ríos"]),
            IdentityRelationCue.Known(new IdentityCandidate { Entity = entity, KnownAs = known })).Should().BeTrue();
        IdentityRelationCue.FromFactsAndSources(entity, [], []).Should().BeEmpty("nothing known is no disagreement");
    }

    [Theory]
    [InlineData("My niece Valentina started school.", "niece")]
    [InlineData("My little niece Valentina started school.", "niece")]
    [InlineData("Marco is my brother and Valentina is his friend.", null)]
    [InlineData("My sister's cat Valentina sleeps on the sofa.", "cat")]
    [InlineData("Clara's cat and Valentina get along.", null)]
    public void Only_the_word_next_to_the_name_is_its_relation(string text, string? relation)
    {
        var found = IdentityRelationCue.InText(text, ["Valentina"]);
        if (relation is null) found.Should().BeEmpty(); else found.Should().Equal(relation);
    }

    [Theory]
    [InlineData("My little niece Valentina lost a tooth.", "niece")]
    [InlineData("Valentina (my niece) lost a tooth.", "niece")]
    [InlineData("Valentina, our designer, sent the mock-ups.", "designer")]
    [InlineData("Valentina sent the mock-ups.", null)]
    [InlineData("My friend's sister Valentina called.", null)]
    public void The_turn_s_relation_for_a_name_is_read_from_its_own_words(string text, string? expected)
    {
        var found = IdentityRelationCue.InTurn(text, ["Valentina Ríos"]);
        if (expected is null) found.Should().BeEmpty();
        else found.Should().Contain(expected);
    }
}
