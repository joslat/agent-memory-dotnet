using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using AgentMemory.AgentFramework.Mapping;
using AgentMemory.Core.Extraction;
using AgentMemory.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// 36.7: how the recalled people and things relate reaches the agent, a relation said twice is one edge, and a
/// single-valued relation ends its previous edge. Found in simulated conversations: "Carmen is my best friend" never
/// reached the agent ("is she a colleague or a friend?"), "lives in Lyon" was two edges, and both residences stayed live.
/// </summary>
public sealed class RelationshipsInRecallTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private static RecalledRelationship R(string source, string type, string target, string? owner = "u1") => new()
    {
        SourceName = source, TargetName = target,
        Relationship = new Relationship
        {
            RelationshipId = $"{source}-{type}-{target}", SourceEntityId = source, TargetEntityId = target,
            RelationshipType = type, Confidence = 0.9, CreatedAtUtc = T0, OwnerId = owner,
        },
    };

    private static MemoryContext Context(bool separated = false) => new()
    {
        SessionId = "s1", AssembledAtUtc = T0, SeparatesSharedKnowledge = separated,
        RelevantRelationships = new MemoryContextSection<RecalledRelationship>
        {
            Items = [R("Rosa", "BEST_FRIEND", "Carmen"), R("Alice", "follows", "White Rabbit", owner: null)],
        },
    };

    [Fact]
    public void Both_renderers_state_how_the_recalled_entities_relate()
    {
        var maf = MafTypeMapper.ToContextMessages(Context(), new ContextFormatOptions()).Select(m => m.Text).ToList();
        var core = MemoryContextFormatter.FormatRecallResult(new RecallResult { Context = Context(), TotalItemsRetrieved = 2 });

        maf.Should().Contain(t => t.Contains("Relationships: ") && t.Contains("Rosa — best friend → Carmen"));
        core.Should().Contain("### Relationships").And.Contain("- Rosa — best friend → Carmen");
    }

    [Fact]
    public void Shared_relationships_are_labelled_like_every_shared_item()
    {
        var maf = MafTypeMapper.ToContextMessages(Context(separated: true), new ContextFormatOptions()).Select(m => m.Text).ToList();

        maf.Single(t => t.Contains("Relationships: ", StringComparison.Ordinal)).Should().NotContain("White Rabbit");
        maf.Should().Contain(t => t.Contains($"Relationships ({SharedKnowledge.Label})") && t.Contains("White Rabbit"));
    }

    [Fact]
    public void No_relationships_render_nothing()
    {
        var empty = new MemoryContext { SessionId = "s1", AssembledAtUtc = T0 };

        MafTypeMapper.ToContextMessages(empty, new ContextFormatOptions()).Should().NotContain(m => m.Text.Contains("Relationships"));
    }

    // ── The assembler asks for them only when asked ─────────────────────────────────────────────────

    private static (MemoryContextAssembler Sut, ILongTermMemoryService LongTerm) Assembler()
    {
        var longTerm = Substitute.For<ILongTermMemoryService>();
        longTerm.SearchEntitiesAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>(
                [new Entity { EntityId = "carmen", Name = "Carmen", Type = "PERSON", Confidence = 1, CreatedAtUtc = T0 }]));
        longTerm.GetRelationshipsAmongAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<DateTimeOffset>(),
                Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RecalledRelationship>>([R("Rosa", "best_friend", "Carmen")]));
        var embeddings = Substitute.For<IEmbeddingOrchestrator>();
        embeddings.EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[4]);
        var options = new MemoryOptions();
        var sut = new MemoryContextAssembler(
            Substitute.For<IShortTermMemoryService>(), longTerm, Substitute.For<IReasoningMemoryService>(), graphRag: null,
            embeddings, Substitute.For<IClock>(), Options.Create(options), NullLogger<MemoryContextAssembler>.Instance,
            new DefaultMemoryIsolationPolicy(Options.Create(options.Isolation), NullLogger<DefaultMemoryIsolationPolicy>.Instance));
        return (sut, longTerm);
    }

    private static RecallRequest Ask(int maxRelationships) => new()
    {
        SessionId = "s1", Query = "what does Carmen do", QueryEmbedding = new float[4],
        Options = new RecallOptions { MaxRelationships = maxRelationships },
    };

    [Fact]
    public async Task Recall_reads_the_relationships_of_the_recalled_entities_when_asked()
    {
        var (sut, longTerm) = Assembler();

        var context = await sut.AssembleContextAsync(Ask(maxRelationships: 5));

        context.RelevantRelationships.Items.Should().ContainSingle().Which.TargetName.Should().Be("Carmen");
        await longTerm.Received(1).GetRelationshipsAmongAsync(
            Arg.Is<IReadOnlyList<string>>(ids => ids.SequenceEqual(new[] { "carmen" })), 5,
            Arg.Any<DateTimeOffset>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task By_default_recall_reads_none()
    {
        var (sut, longTerm) = Assembler();

        var context = await sut.AssembleContextAsync(Ask(maxRelationships: 0));

        context.RelevantRelationships.Items.Should().BeEmpty();
        await longTerm.DidNotReceive().GetRelationshipsAmongAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(),
            Arg.Any<DateTimeOffset>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_store_that_cannot_read_them_leaves_the_section_empty_not_the_recall()
    {
        var (sut, longTerm) = Assembler();
        longTerm.GetRelationshipsAmongAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<DateTimeOffset>(),
                Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new NotSupportedException());

        var context = await sut.AssembleContextAsync(Ask(maxRelationships: 5));

        context.RelevantRelationships.Items.Should().BeEmpty();
        context.RelevantEntities.Items.Should().ContainSingle();
    }

    // ── The write: one edge per relation, and a new value ends the old ─────────────────────────────

    private readonly IRelationshipRepository _relationships = Substitute.For<IRelationshipRepository>();
    private readonly List<Relationship> _upserted = [];
    private readonly List<string> _ended = [];

    private PersistenceStage Stage(bool supersede, params Relationship[] stored)
    {
        _relationships.GetBySourceEntityAsync(Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Relationship>>(stored));
        _relationships.UpsertAsync(Arg.Any<Relationship>(), Arg.Any<CancellationToken>())
            .Returns(ci => { _upserted.Add(ci.Arg<Relationship>()); return Task.FromResult(ci.Arg<Relationship>()); });
        _relationships.EndAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(ci => { _ended.Add(ci.ArgAt<string>(0)); return Task.FromResult(true); });
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(T0);
        var ids = Substitute.For<IIdGenerator>();
        ids.GenerateId().Returns(_ => Guid.NewGuid().ToString("N"));
        var entities = Substitute.For<IEntityRepository>();
        entities.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>()).Returns(ci => Task.FromResult(ci.Arg<Entity>()));
        return new PersistenceStage(Substitute.For<IEmbeddingOrchestrator>(), entities,
            Substitute.For<IFactRepository>(), Substitute.For<IPreferenceRepository>(), _relationships, clock, ids,
            NullLogger<PersistenceStage>.Instance, new PassThroughMemoryPersistenceTransaction(),
            Options.Create(new ExtractionOptions { SupersedeReplacedFacts = supersede, EnableBatchMemoryUpserts = false }));
    }

    private static Entity E(string id) => new() { EntityId = id, Name = id, Type = "PERSON", Confidence = 1, CreatedAtUtc = T0 };

    private static ExtractionStageResult Says(string source, string type, string target) => new()
    {
        ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase) { [source] = E(source), [target] = E(target) },
        FilteredRelationships = [new ExtractedRelationship { SourceEntity = source, RelationshipType = type, TargetEntity = target, Confidence = 0.9 }],
    };

    private static Relationship Edge(string id, string type, string target, DateTimeOffset? ended = null) => new()
    {
        RelationshipId = id, SourceEntityId = "Oskar", TargetEntityId = target, RelationshipType = type,
        Confidence = 0.9, CreatedAtUtc = T0.AddDays(-9), ValidUntil = ended,
    };

    [Fact]
    public async Task A_relation_said_again_is_the_edge_already_stored()
    {
        await Stage(supersede: false, Edge("old-edge", "LIVES_IN", "Lyon")).PersistAsync(Says("Oskar", "lives_in", "Lyon"), ownerId: "u1");

        _upserted.Should().ContainSingle().Which.RelationshipId.Should().Be("old-edge");
    }

    [Fact]
    public async Task An_ended_edge_is_not_restated_a_new_one_is_written()
    {
        await Stage(supersede: false, Edge("old-edge", "lives_in", "Lyon", ended: T0.AddDays(-1)))
            .PersistAsync(Says("Oskar", "lives_in", "Lyon"), ownerId: "u1");

        _upserted.Should().ContainSingle().Which.RelationshipId.Should().NotBe("old-edge");
    }

    [Fact]
    public async Task A_new_residence_ends_the_previous_one()
    {
        await Stage(supersede: true, Edge("hamburg", "lives_in", "Hamburg"), Edge("friend", "friend_of", "Ana"))
            .PersistAsync(Says("Oskar", "lives_in", "Copenhagen"), ownerId: "u1");

        _ended.Should().Equal("hamburg");
    }

    [Fact]
    public async Task A_new_employer_stated_in_other_words_ends_the_previous_one()
    {
        await Stage(supersede: true, Edge("shipping", "works_at", "shipping company"))
            .PersistAsync(Says("Oskar", "employed_by", "wind energy firm"), ownerId: "u1");

        _ended.Should().Equal("shipping");
    }

    [Fact]
    public async Task A_multi_valued_relation_ends_nothing_and_neither_does_supersession_off()
    {
        await Stage(supersede: true, Edge("ana", "friend_of", "Ana")).PersistAsync(Says("Oskar", "friend_of", "Bea"), ownerId: "u1");
        await Stage(supersede: false, Edge("hamburg", "lives_in", "Hamburg")).PersistAsync(Says("Oskar", "lives_in", "Copenhagen"), ownerId: "u1");

        _ended.Should().BeEmpty();
    }

    // ── Review round 1 ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Restating_an_edge_keeps_its_sources_validity_and_description()
    {
        var stored = Edge("old-edge", "lives_in", "Lyon") with
        {
            SourceMessageIds = ["first-message"], Description = "since the move", ValidFrom = T0.AddYears(-2),
            ValidUntil = T0.AddYears(1),
        };

        await Stage(supersede: false, stored).PersistAsync(Says("Oskar", "lives_in", "Lyon"), ownerId: "u1");

        var written = _upserted.Should().ContainSingle().Subject;
        written.RelationshipId.Should().Be("old-edge");
        written.SourceMessageIds.Should().Contain("first-message");
        written.Description.Should().Be("since the move");
        written.ValidFrom.Should().Be(T0.AddYears(-2));
        written.ValidUntil.Should().Be(T0.AddYears(1));
    }

    [Fact]
    public async Task A_replacement_that_failed_to_store_ends_nothing()
    {
        var stage = Stage(supersede: true, Edge("hamburg", "lives_in", "Hamburg"));
        _relationships.UpsertAsync(Arg.Any<Relationship>(), Arg.Any<CancellationToken>())
            .Returns<Task<Relationship>>(_ => throw new InvalidOperationException("write failed"));

        await stage.PersistAsync(Says("Oskar", "lives_in", "Copenhagen"), ownerId: "u1");

        _ended.Should().BeEmpty("the person must not be left with no residence");
    }

    [Fact]
    public async Task A_history_form_ends_no_current_edge()
    {
        await Stage(supersede: true, Edge("meta", "works_at", "Meta")).PersistAsync(Says("Oskar", "worked_at", "Google"), ownerId: "u1");

        _ended.Should().BeEmpty();
    }

    /// <summary>
    /// 36.6 review round 2: "I'm Rosa, my sister is Carmen" in one turn. The speaker's entity is written as Rosa and the
    /// relationship from "user" lands on it (it was dropped: "user" was skipped and Rosa was not an entity yet).
    /// </summary>
    [Fact]
    public async Task A_relationship_from_the_speaker_lands_on_the_named_person_in_the_same_turn()
    {
        var entities = Substitute.For<IEntityRepository>();
        var written = new List<Entity>();
        entities.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns(ci => { written.Add(ci.Arg<Entity>()); return Task.FromResult(ci.Arg<Entity>()); });
        var facts = Substitute.For<IFactRepository>();
        facts.UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>()).Returns(ci => Task.FromResult(ci.Arg<Fact>()));
        _relationships.GetBySourceEntityAsync(Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Relationship>>([]));
        _relationships.UpsertAsync(Arg.Any<Relationship>(), Arg.Any<CancellationToken>())
            .Returns(ci => { _upserted.Add(ci.Arg<Relationship>()); return Task.FromResult(ci.Arg<Relationship>()); });
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(T0);
        var ids = Substitute.For<IIdGenerator>();
        ids.GenerateId().Returns(_ => Guid.NewGuid().ToString("N"));
        var stage = new PersistenceStage(Substitute.For<IEmbeddingOrchestrator>(), entities, facts, Substitute.For<IPreferenceRepository>(),
            _relationships, clock, ids, NullLogger<PersistenceStage>.Instance, new PassThroughMemoryPersistenceTransaction(),
            Options.Create(new ExtractionOptions { EnableBatchMemoryUpserts = false }));

        await stage.PersistAsync(new ExtractionStageResult
        {
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase) { ["user"] = E("speaker-id"), ["Carmen"] = E("Carmen") },
            FilteredFacts = [new ExtractedFact { Subject = "user", Predicate = "is named", Object = "Rosa", Confidence = 1 }],
            FilteredRelationships = [new ExtractedRelationship { SourceEntity = "user", RelationshipType = "SIBLING_OF", TargetEntity = "Carmen", Confidence = 0.9 }],
        }, ownerId: "u1");

        written.Select(e => e.Name).Should().BeEquivalentTo(["Rosa", "Carmen"]);
        _upserted.Should().ContainSingle().Which.SourceEntityId.Should().Be("speaker-id", "the relationship hangs from the person, not from nothing");
    }
}
