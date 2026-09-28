using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;
using AgentMemory.Core.Services;
using AgentMemory.Extraction.Llm;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// I-5: "user" is the owner. Recorded live: "Dana | lives_in | Porto" next to "user | is learning | cello"
/// and "Lena | is sister of | user", one person under two names, both listed in the profile.
/// </summary>
public sealed class UserNameResolutionTests
{
    private readonly IFactRepository _facts = Substitute.For<IFactRepository>();
    private readonly List<Fact> _upserted = [];

    private PersistenceStage Sut(bool resolve, bool canonical = false)
    {
        var entities = Substitute.For<IEntityRepository>();
        entities.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<Entity>());
        _facts.UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>())
            .Returns(call => { _upserted.Add(call.Arg<Fact>()); return call.Arg<Fact>(); });
        _facts.FindLatestObjectAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>(),
                Arg.Any<MemoryScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        var embeddings = Substitute.For<IEmbeddingOrchestrator>();
        embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[4]);
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.Parse("2026-09-27T00:00:00Z"));
        var ids = Substitute.For<IIdGenerator>();
        ids.GenerateId().Returns(_ => Guid.NewGuid().ToString("N"));

        return new PersistenceStage(embeddings, entities, _facts, Substitute.For<IPreferenceRepository>(),
            Substitute.For<IRelationshipRepository>(), clock, ids, NullLogger<PersistenceStage>.Instance,
            new PassThroughMemoryPersistenceTransaction(),
            Options.Create(new ExtractionOptions
            {
                ResolveUserToName = resolve,
                CanonicalFactSubjects = canonical,
                LinkFactsToEntities = true,
                EnableBatchMemoryUpserts = false,
            }));
    }

    private static ExtractedFact F(string subject, string predicate, string @object) =>
        new() { Subject = subject, Predicate = predicate, Object = @object, Confidence = 0.9 };

    private static ExtractionStageResult Extraction(params ExtractedFact[] facts) => new()
    {
        SourceMessageIds = ["message-1"],
        ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase),
        FilteredFacts = facts,
    };

    private Fact Stored(string predicate) => _upserted.Single(f => f.Predicate == predicate);

    private void OwnerIsAlreadyNamed(string name) =>
        _facts.FindLatestObjectAsync(
                Arg.Is<IReadOnlyCollection<string>>(s => s.Contains("user")),
                Arg.Is<IReadOnlyCollection<string>>(p => p.Contains("is named")),
                Arg.Is<MemoryScope>(scope => scope.OwnerId == "owner-1"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(name));

    private Task NoLookup() => _facts.DidNotReceiveWithAnyArgs()
        .FindLatestObjectAsync(default!, default!, default!, default);

    [Fact]
    public async Task A_name_stated_in_the_turn_names_the_users_facts()
    {
        await Sut(resolve: true).PersistAsync(Extraction(
            F("user", "is named", "Dana"),
            F("user", "works at", "Northwind"),
            F("Lena", "is sister of", "user")), ownerId: "owner-1", cancellationToken: CancellationToken.None);

        Stored("works at").Subject.Should().Be("Dana");
        Stored("works at").Metadata["subject_surface"].Should().Be("user");
        Stored("is sister of").Object.Should().Be("Dana");
        Stored("is named").Subject.Should().Be("user", "the naming fact is how the name is found again");
        await NoLookup();
    }

    [Fact]
    public async Task A_name_stated_in_an_earlier_session_is_read_from_the_store()
    {
        var sut = Sut(resolve: true);
        OwnerIsAlreadyNamed("Dana");

        await sut.PersistAsync(Extraction(F("I", "is learning", "cello")), ownerId: "owner-1", cancellationToken: CancellationToken.None);

        Stored("is learning").Subject.Should().Be("Dana");
    }

    // ---- review round 3 ----

    [Fact]
    public async Task A_failed_name_lookup_leaves_the_words_used_and_the_persist_succeeds()
    {
        var sut = Sut(resolve: true);
        _facts.FindLatestObjectAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>(),
                Arg.Any<MemoryScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string?>(new TimeoutException("read timed out")));

        await sut.PersistAsync(Extraction(F("user", "is learning", "cello")), ownerId: "owner-1", cancellationToken: CancellationToken.None);

        Stored("is learning").Subject.Should().Be("user");
    }

    [Fact]
    public async Task I_and_me_as_objects_and_the_assistants_own_I_are_not_the_user()
    {
        var sut = Sut(resolve: true);
        OwnerIsAlreadyNamed("Dana");

        await sut.PersistAsync(Extraction(
            F("user", "lives in", "ME"),
            F("I", "recommend", "Hotel Lisboa") with { SourceRole = "assistant" },
            F("account", "has role", "user")),
            ownerId: "owner-1", cancellationToken: CancellationToken.None);

        Stored("lives in").Should().Match<Fact>(f => f.Subject == "Dana" && f.Object == "ME", "Maine is not the user");
        Stored("recommend").Subject.Should().Be("I", "the assistant's first person is not the user's");
        Stored("has role").Object.Should().Be("Dana", "\"user\" as an object is the user");
    }

    [Fact]
    public async Task A_fact_now_under_the_name_also_replaces_what_was_stored_as_user()
    {
        var sut = Sut(resolve: true);
        OwnerIsAlreadyNamed("Dana");
        _facts.FindSupersededCandidatesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<DateTimeOffset>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>([]));

        await new PersistenceStage(Substitute.For<IEmbeddingOrchestrator>(), Substitute.For<IEntityRepository>(), _facts,
                Substitute.For<IPreferenceRepository>(), Substitute.For<IRelationshipRepository>(), Substitute.For<IClock>(),
                new SequentialIds(), NullLogger<PersistenceStage>.Instance, new PassThroughMemoryPersistenceTransaction(),
                Options.Create(new ExtractionOptions { ResolveUserToName = true, SupersedeReplacedFacts = true, EnableBatchMemoryUpserts = false }))
            .PersistAsync(Extraction(F("user", "lives_in", "Porto")), ownerId: "owner-1", cancellationToken: CancellationToken.None);

        await _facts.Received(1).FindSupersededCandidatesAsync(Arg.Any<string>(), "user", "lives_in", "Porto",
            Arg.Any<DateTimeOffset>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    private sealed class SequentialIds : IIdGenerator
    {
        private int _next;
        public string GenerateId() => $"id-{Interlocked.Increment(ref _next)}";
    }

    [Fact]
    public void The_fact_that_names_the_user_is_never_merged_away()
    {
        PersistenceStage.MayBeOneStatement(F("user", "is named", "Dana"), F("user", "is", "Dana")).Should().BeFalse();
    }

    [Fact]
    public void The_instruction_describes_the_fact_rather_than_giving_a_literal_one()
    {
        // The multi-session rung requires source_session on every fact; a literal object without it
        // teaches the model a fact that fails that rung's validation.
        ExtractionPromptSemantics.UserNameInstruction(true).Should().NotContain("{\"subject\"");
    }

    [Fact]
    public async Task With_canonical_subjects_the_name_resolves_to_the_known_person()
    {
        var sut = Sut(resolve: true, canonical: true);
        var dana = new Entity { EntityId = "entity-dana", Name = "Dana Reyes", Type = "PERSON", Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch };
        var extraction = Extraction(F("user", "is named", "Dana"), F("user", "works at", "Northwind")) with
        {
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase) { ["Dana"] = dana },
        };

        await sut.PersistAsync(extraction, ownerId: "owner-1", cancellationToken: CancellationToken.None);

        Stored("works at").Subject.Should().Be("Dana Reyes");
        // Both facts are about Dana: the one that names her, and the one now stored under her name.
        await _facts.Received(2).CreateAboutRelationshipAsync(Arg.Any<string>(), "entity-dana", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_naming_fact_keeps_user_even_when_user_resolved_to_the_named_person()
    {
        // Found live: "Hi! I'm Dana" resolved the entity "user" into the person "Dana" (alias "user"), and canonical
        // subjects then stored the naming fact as "Dana | is named | Dana": the name could never be found again, and
        // the dossier showed Dana beside "you" as someone else.
        var sut = Sut(resolve: true, canonical: true);
        var dana = new Entity { EntityId = "entity-dana", Name = "Dana", Type = "PERSON", Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch };
        var extraction = Extraction(F("user", "is named", "Dana"), F("user", "works at", "Northwind")) with
        {
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase) { ["user"] = dana, ["Dana"] = dana },
        };

        await sut.PersistAsync(extraction, ownerId: "owner-1", cancellationToken: CancellationToken.None);

        Stored("is named").Subject.Should().Be("user", "the naming fact is how the name is found again");
        Stored("works at").Subject.Should().Be("Dana");
    }

    [Fact]
    public async Task Until_a_name_is_known_nothing_changes()
    {
        await Sut(resolve: true).PersistAsync(Extraction(F("user", "works at", "Northwind")),
            ownerId: "owner-1", cancellationToken: CancellationToken.None);

        Stored("works at").Subject.Should().Be("user");
    }

    [Fact]
    public async Task Off_nothing_is_rewritten_or_read()
    {
        await Sut(resolve: false).PersistAsync(Extraction(F("user", "is named", "Dana"), F("user", "works at", "Northwind")),
            ownerId: "owner-1", cancellationToken: CancellationToken.None);

        Stored("works at").Subject.Should().Be("user");
        await NoLookup();
    }

    [Fact]
    public async Task A_turn_that_says_nothing_about_the_user_reads_nothing()
    {
        await Sut(resolve: true).PersistAsync(Extraction(F("Lena", "lives in", "Berlin")),
            ownerId: "owner-1", cancellationToken: CancellationToken.None);

        await NoLookup();
    }

    // ---- I-7: relationships from "user" ----

    private static Entity Person(string id, string name) => new()
    {
        EntityId = id, Name = name, Type = "PERSON", Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch,
    };

    private (PersistenceStage Sut, IRelationshipRepository Relationships, IEntityRepository Entities) WithRelationships(bool resolve)
    {
        var relationships = Substitute.For<IRelationshipRepository>();
        relationships.UpsertAsync(Arg.Any<Relationship>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<Relationship>());
        var entities = Substitute.For<IEntityRepository>();
        entities.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<Entity>());
        entities.GetByNameAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>([]));
        _facts.UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<Fact>());
        _facts.FindLatestObjectAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>(),
                Arg.Any<MemoryScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        var embeddings = Substitute.For<IEmbeddingOrchestrator>();
        embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[4]);
        var ids = Substitute.For<IIdGenerator>();
        ids.GenerateId().Returns(_ => Guid.NewGuid().ToString("N"));
        var sut = new PersistenceStage(embeddings, entities, _facts, Substitute.For<IPreferenceRepository>(), relationships,
            Substitute.For<IClock>(), ids, NullLogger<PersistenceStage>.Instance, new PassThroughMemoryPersistenceTransaction(),
            Options.Create(new ExtractionOptions { ResolveUserToName = resolve, EnableBatchMemoryUpserts = false }));
        return (sut, relationships, entities);
    }

    private static ExtractionStageResult WorksAt(params (string Name, Entity Entity)[] resolved)
    {
        var map = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase) { ["Northwind"] = new()
        {
            EntityId = "entity-northwind", Name = "Northwind", Type = "ORGANIZATION", Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch,
        } };
        foreach (var (name, entity) in resolved) map[name] = entity;
        return new ExtractionStageResult
        {
            SourceMessageIds = ["message-1"],
            ResolvedEntityMap = map,
            FilteredRelationships = [new ExtractedRelationship { SourceEntity = "user", TargetEntity = "Northwind", RelationshipType = "WORKS_AT", Confidence = 0.9 }],
        };
    }

    [Fact]
    public async Task A_relationship_from_user_starts_at_the_person_named_in_the_same_turn()
    {
        var (sut, relationships, _) = WithRelationships(resolve: true);
        var extraction = WorksAt(("Dana", Person("entity-dana", "Dana"))) with { FilteredFacts = [F("user", "is named", "Dana")] };

        await sut.PersistAsync(extraction, ownerId: "owner-1", cancellationToken: CancellationToken.None);

        await relationships.Received(1).UpsertAsync(
            Arg.Is<Relationship>(r => r.SourceEntityId == "entity-dana" && r.TargetEntityId == "entity-northwind"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_relationship_from_user_in_a_later_session_starts_at_the_stored_person()
    {
        var (sut, relationships, entities) = WithRelationships(resolve: true);
        _facts.FindLatestObjectAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>(),
                Arg.Any<MemoryScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("Dana"));
        entities.FindLiveByNameAsync("Dana", "PERSON", Arg.Is<MemoryScope>(s => s.OwnerId == "owner-1"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Entity?>(Person("entity-dana", "Dana")));

        await sut.PersistAsync(WorksAt(), ownerId: "owner-1", cancellationToken: CancellationToken.None);

        await relationships.Received(1).UpsertAsync(
            Arg.Is<Relationship>(r => r.SourceEntityId == "entity-dana"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Without_a_known_person_a_relationship_from_user_is_skipped_as_before()
    {
        var (sut, relationships, _) = WithRelationships(resolve: true);

        var result = await sut.PersistAsync(WorksAt(), ownerId: "owner-1", cancellationToken: CancellationToken.None);

        await relationships.DidNotReceiveWithAnyArgs().UpsertAsync(default!, default);
        result.Outcomes.Should().Contain(o => o.Kind == MemoryItemKind.Relationship && o.Status == IngestionItemStatus.Skipped);
    }

    // ---- the prompt side: every extractor rung asks for the name, or none changes ----

    private static IEnumerable<(string Rung, string Prompt)> AllRungPrompts(bool capture)
    {
        var options = new LlmExtractionOptions();
        yield return ("per-kind fact",
            LlmFactExtractor.BuildSystemPrompt(AssistantContentMode.Ignore, TemporalValidityMode.Ignore, ExtractionProvenanceMode.Batch, capture));
        yield return ("unified",
            LlmUnifiedMemoryExtractor.BuildSystemPrompt(AssistantContentMode.Ignore, options.EntityTypes,
                TemporalValidityMode.Ignore, ExtractionProvenanceMode.Batch, captureUserName: capture));
        yield return ("multi-session batch",
            LlmMultiSessionUnifiedMemoryExtractor.BuildSystemPrompt(vocabulary: null, captureUserName: capture));
    }

    [Fact]
    public void Every_extractor_rung_asks_for_the_users_name_when_on()
    {
        var expected = ExtractionPromptSemantics.UserNameInstruction(true);
        expected.Should().Contain("\"is named\"");

        foreach (var (rung, prompt) in AllRungPrompts(capture: true))
            prompt.Should().Contain(expected, $"the {rung} rung must honour CaptureUserName");
    }

    [Fact]
    public void By_default_every_rung_says_a_question_states_nothing()
    {
        // 2026-09-27: "What do you remember about my brother?" became an entity "user's brother".
        var options = new LlmExtractionOptions();
        var expected = ExtractionPromptSemantics.QuestionsInstruction(options.IgnoreQuestions);
        expected.Should().NotBeEmpty();
        LlmFactExtractor.BuildSystemPrompt(options.AssistantContent, options.TemporalValidity, options.Provenance,
            options.CaptureUserName, options.IgnoreQuestions).Should().Contain(expected);
        LlmUnifiedMemoryExtractor.BuildSystemPrompt(options.AssistantContent, options.EntityTypes, options.TemporalValidity,
            options.Provenance, options.CaptureIdentityAliases, options.CaptureUserName, options.IgnoreQuestions).Should().Contain(expected);
        LlmMultiSessionUnifiedMemoryExtractor.BuildSystemPrompt(null, options.AssistantContent, options.TemporalValidity,
            options.Provenance, options.CaptureIdentityAliases, options.CaptureUserName, options.IgnoreQuestions).Should().Contain(expected);
    }

    [Fact]
    public void No_extractor_rung_changes_its_prompt_when_off()
    {
        foreach (var (rung, prompt) in AllRungPrompts(capture: false))
            prompt.Should().NotContain("is named", $"the {rung} rung must add nothing when off");
    }
}
