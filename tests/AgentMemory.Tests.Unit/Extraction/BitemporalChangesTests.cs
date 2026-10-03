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
/// 40.65: with <see cref="ExtractionOptions.BitemporalChanges"/> every closing says why. A change is closed as a change at
/// the moment the new value began (its stated start, else when it was said), a marked correction as a correction; a
/// change's edge ends when the change took effect. Without the option, the exact call every closing made before.
/// </summary>
public sealed class BitemporalChangesTests
{
    private static readonly DateTimeOffset Today = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset MovedIn = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly IFactRepository _facts = Substitute.For<IFactRepository>();
    private readonly List<(string Loser, string Winner, FactClosureReason Reason, DateTimeOffset? ChangedAt)> _closed = [];
    private readonly List<(string Loser, string Winner)> _closedWithoutReason = [];
    private readonly IClock _clock = Substitute.For<IClock>();

    public BitemporalChangesTests()
    {
        _clock.UtcNow.Returns(Today);
        _facts.UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>()).Returns(ci => Task.FromResult(ci.Arg<Fact>()));
        _facts.FindSupersededCandidatesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<DateTimeOffset>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>([Stored("bilbao", "Bilbao")]));
        _facts.GetBySubjectAsync(Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>([Stored("bilbao", "Bilbao")]));
        _facts.SupersedeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<FactClosureReason>(), Arg.Any<DateTimeOffset?>(),
                Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _closed.Add((ci.ArgAt<string>(0), ci.ArgAt<string>(1), ci.ArgAt<FactClosureReason>(2), ci.ArgAt<DateTimeOffset?>(3)));
                return Task.FromResult(true);
            });
        _facts.SupersedeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(ci => { _closedWithoutReason.Add((ci.ArgAt<string>(0), ci.ArgAt<string>(1))); return Task.FromResult(true); });
    }

    private static Fact Stored(string id, string city) => new()
    {
        FactId = id, Subject = "user", Predicate = "lives in", Object = city, Confidence = 0.9, CreatedAtUtc = Today.AddYears(-1),
    };

    private static ExtractedFact Lives(string city) => new() { Subject = "user", Predicate = "lives in", Object = city, Confidence = 0.9 };

    private PersistenceStage Sut(bool bitemporal, IEntityRepository? entities = null, IRelationshipRepository? relationships = null)
    {
        var embeddings = Substitute.For<IEmbeddingOrchestrator>();
        embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[8]);
        var ids = Substitute.For<IIdGenerator>();
        ids.GenerateId().Returns(_ => Guid.NewGuid().ToString("N"));
        return new PersistenceStage(embeddings, entities ?? Substitute.For<IEntityRepository>(), _facts,
            Substitute.For<IPreferenceRepository>(), relationships ?? Substitute.For<IRelationshipRepository>(), _clock, ids,
            NullLogger<PersistenceStage>.Instance, new PassThroughMemoryPersistenceTransaction(),
            Options.Create(new ExtractionOptions
            {
                SupersedeReplacedFacts = true, BitemporalChanges = bitemporal, EnableBatchMemoryUpserts = false,
            }));
    }

    [Fact]
    public async Task A_change_is_closed_as_a_change_when_the_new_value_began()
    {
        await Sut(bitemporal: true).PersistAsync(
            new ExtractionStageResult { FilteredFacts = [Lives("Madrid") with { ValidFrom = MovedIn, ValidFromPrecision = DatePrecision.Month }] },
            ownerId: "owner-1");

        _closed.Should().ContainSingle().Which.Should().Be(("bilbao", _closed[0].Winner, FactClosureReason.Change, MovedIn));
        _closedWithoutReason.Should().BeEmpty();
    }

    [Fact]
    public async Task An_undated_change_is_closed_when_it_was_said()
    {
        await Sut(bitemporal: true).PersistAsync(
            new ExtractionStageResult { FilteredFacts = [Lives("Madrid")] }, ownerId: "owner-1");

        _closed.Should().ContainSingle().Which.ChangedAt.Should().Be(Today, "with no stated start, the change took effect when it was said");
    }

    [Fact]
    public async Task A_marked_correction_is_closed_as_a_correction()
    {
        // As the store answers once the correction has closed Bilbao: supersession finds no live candidate left.
        _facts.FindSupersededCandidatesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<DateTimeOffset>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>([]));

        await Sut(bitemporal: true).PersistAsync(
            new ExtractionStageResult { FilteredFacts = [Lives("Madrid") with { Replaces = "Bilbao" }] }, ownerId: "owner-1");

        _closed.Should().ContainSingle().Which.Should().Match<(string Loser, string Winner, FactClosureReason Reason, DateTimeOffset? ChangedAt)>(
            closing => closing.Loser == "bilbao" && closing.Reason == FactClosureReason.Correction && closing.ChangedAt == null);
    }

    [Fact]
    public async Task Without_the_option_every_closing_is_the_call_it_was()
    {
        await Sut(bitemporal: false).PersistAsync(
            new ExtractionStageResult { FilteredFacts = [Lives("Madrid") with { ValidFrom = MovedIn }] }, ownerId: "owner-1");

        _closedWithoutReason.Should().ContainSingle().Which.Loser.Should().Be("bilbao");
        _closed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_changes_edge_ends_when_the_change_took_effect()
    {
        var entities = Substitute.For<IEntityRepository>();
        var relationships = Substitute.For<IRelationshipRepository>();
        Entity E(string id, string name) => new() { EntityId = id, Name = name, Type = "LOCATION", Confidence = 1, CreatedAtUtc = Today };
        entities.FindLiveByNameAsync("user", null, Arg.Any<MemoryScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Entity?>(E("ane", "Ane")));
        entities.GetByIdAsync("bilbao-place", Arg.Any<CancellationToken>()).Returns(Task.FromResult<Entity?>(E("bilbao-place", "Bilbao")));
        relationships.GetBySourceEntityAsync("ane", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Relationship>>(
            [
                new Relationship
                {
                    RelationshipId = "e-bilbao", SourceEntityId = "ane", TargetEntityId = "bilbao-place", RelationshipType = "LIVES_IN",
                    Confidence = 1, CreatedAtUtc = Today.AddYears(-1),
                },
            ]));
        relationships.EndAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        await Sut(bitemporal: true, entities, relationships).PersistAsync(
            new ExtractionStageResult { FilteredFacts = [Lives("Madrid") with { ValidFrom = MovedIn, ValidFromPrecision = DatePrecision.Month }] },
            ownerId: "owner-1");

        await relationships.Received(1).EndAsync("e-bilbao", MovedIn, Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }
}
