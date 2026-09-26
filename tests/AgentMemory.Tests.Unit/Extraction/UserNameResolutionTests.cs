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
        _facts.GetBySubjectAsync(Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>([]));
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

    private void OwnerIsAlreadyNamed(string name, DateTimeOffset? invalidatedAt = null) =>
        _facts.GetBySubjectAsync("user", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>(
            [
                new Fact
                {
                    FactId = "naming", Subject = "user", Predicate = "is named", Object = name, Confidence = 1,
                    CreatedAtUtc = DateTimeOffset.UnixEpoch, InvalidatedAtUtc = invalidatedAt,
                },
            ]));

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
        await _facts.DidNotReceiveWithAnyArgs().GetBySubjectAsync(default!, default, default);
    }

    [Fact]
    public async Task A_name_stated_in_an_earlier_session_is_read_from_the_store()
    {
        var sut = Sut(resolve: true);
        OwnerIsAlreadyNamed("Dana");

        await sut.PersistAsync(Extraction(F("I", "is learning", "cello")), ownerId: "owner-1", cancellationToken: CancellationToken.None);

        Stored("is learning").Subject.Should().Be("Dana");
    }

    [Fact]
    public async Task An_invalidated_name_is_not_used()
    {
        var sut = Sut(resolve: true);
        OwnerIsAlreadyNamed("Dana", invalidatedAt: DateTimeOffset.UnixEpoch);

        await sut.PersistAsync(Extraction(F("user", "is learning", "cello")), ownerId: "owner-1", cancellationToken: CancellationToken.None);

        Stored("is learning").Subject.Should().Be("user");
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
        await _facts.DidNotReceiveWithAnyArgs().GetBySubjectAsync(default!, default, default);
    }

    [Fact]
    public async Task A_turn_that_says_nothing_about_the_user_reads_nothing()
    {
        await Sut(resolve: true).PersistAsync(Extraction(F("Lena", "lives in", "Berlin")),
            ownerId: "owner-1", cancellationToken: CancellationToken.None);

        await _facts.DidNotReceiveWithAnyArgs().GetBySubjectAsync(default!, default, default);
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
    public void No_extractor_rung_changes_its_prompt_when_off()
    {
        foreach (var (rung, prompt) in AllRungPrompts(capture: false))
            prompt.Should().NotContain("is named", $"the {rung} rung must add nothing when off");
    }
}
