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

public sealed class DeclinedFactClosingTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Declined_fact_closing_keeps_its_mirrored_edge(bool correction, bool bitemporal)
    {
        var fixture = new Fixture(correction, bitemporal);

        var result = await fixture.PersistAsync();

        await fixture.AssertClosingAttemptedAsync("old-fact");
        result.FactCount.Should().Be(1, "the replacement still persists when the repository declines a closing");
        await fixture.Relationships.DidNotReceiveWithAnyArgs().EndAsync(default!, default, default, default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Declined_correction_keeps_its_source_event_date(bool bitemporal)
    {
        var fixture = new Fixture(correction: true, bitemporal);

        await fixture.PersistAsync();

        await fixture.AssertClosingAttemptedAsync("old-fact");
        await fixture.AssertClosingNotAttemptedAsync("event-date");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Partial_closing_keeps_edge_and_date_supported_by_the_fact_that_stays_live(bool correction, bool bitemporal)
    {
        var fixture = new Fixture(correction, bitemporal);
        fixture.AcceptClosing("old-fact");
        fixture.AddLiveTwin();

        await fixture.PersistAsync();

        await fixture.AssertClosingAttemptedAsync("old-fact");
        await fixture.AssertClosingAttemptedAsync("also-live");
        await fixture.Relationships.DidNotReceiveWithAnyArgs().EndAsync(default!, default, default, default);
        await fixture.AssertClosingNotAttemptedAsync("event-date");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Declined_edge_closing_keeps_the_event_date(bool bitemporal)
    {
        var fixture = new Fixture(correction: true, bitemporal) { DeclineEdgeClosing = true };
        fixture.AcceptClosing("old-fact");

        await fixture.PersistAsync();

        await fixture.AssertClosingAttemptedAsync("old-fact");
        await fixture.Relationships.Received(1).EndAsync("old-edge", Arg.Any<DateTimeOffset>(),
            Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
        await fixture.AssertClosingNotAttemptedAsync("event-date");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Accepted_correction_ends_its_edge_and_closes_its_source_date(bool bitemporal)
    {
        var fixture = new Fixture(correction: true, bitemporal);
        fixture.AcceptClosing("old-fact");

        await fixture.PersistAsync();

        await fixture.AssertClosingAttemptedAsync("old-fact");
        await fixture.Relationships.Received(1).EndAsync("old-edge", Arg.Any<DateTimeOffset>(),
            Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
        await fixture.AssertClosingAttemptedAsync("event-date");
    }

    private sealed class Fixture
    {
        private static readonly DateTimeOffset Now = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);
        private readonly IFactRepository _facts = Substitute.For<IFactRepository>();
        private readonly List<Fact> _stored = [];
        private readonly HashSet<string> _accepted = new(StringComparer.Ordinal) { "event-date" };
        private readonly PersistenceStage _stage;
        private readonly ExtractedFact _incoming;
        private readonly bool _bitemporal;
        private bool _edgeEnded;

        public IRelationshipRepository Relationships { get; } = Substitute.For<IRelationshipRepository>();
        public bool DeclineEdgeClosing { get; init; }

        public Fixture(bool correction, bool bitemporal)
        {
            _bitemporal = bitemporal;
            var predicate = correction ? "plans to run" : "lives in";
            var oldValue = correction ? "half marathon" : "Lyon";
            var newValue = correction ? "full marathon" : "Copenhagen";
            _stored.Add(new Fact
            {
                FactId = "old-fact", Subject = "Mira", Predicate = predicate, Object = oldValue,
                OwnerId = "owner", Confidence = 1, CreatedAtUtc = Now, SourceMessageIds = ["old-message"],
            });
            var date = new Fact
            {
                FactId = "event-date", Subject = oldValue, Predicate = "takes place in", Object = "2027-04",
                OwnerId = "owner", Confidence = 1, CreatedAtUtc = Now, SourceMessageIds = ["old-message"],
            };
            var edge = new Relationship
            {
                RelationshipId = "old-edge", SourceEntityId = "person", TargetEntityId = "old-target",
                RelationshipType = predicate, OwnerId = "owner", Confidence = 1, CreatedAtUtc = Now,
            };
            var person = new Entity { EntityId = "person", Name = "Mira", Type = "PERSON", OwnerId = "owner", Confidence = 1, CreatedAtUtc = Now };
            var target = new Entity { EntityId = "old-target", Name = oldValue, Type = "EVENT", OwnerId = "owner", Confidence = 1, CreatedAtUtc = Now };
            var entities = Substitute.For<IEntityRepository>();
            entities.FindLiveByNameAsync("Mira", null, Arg.Any<MemoryScope>(), Arg.Any<CancellationToken>()).Returns(person);
            entities.FindLiveByNameAsync(oldValue, null, Arg.Any<MemoryScope>(), Arg.Any<CancellationToken>()).Returns(target);
            entities.GetByIdAsync("old-target", Arg.Any<CancellationToken>()).Returns(target);
            Relationships.GetBySourceEntityAsync("person", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult<IReadOnlyList<Relationship>>(_edgeEnded ? [] : [edge]));
            Relationships.GetByTargetEntityAsync("old-target", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult<IReadOnlyList<Relationship>>(_edgeEnded ? [] : [edge]));
            Relationships.EndAsync("old-edge", Arg.Any<DateTimeOffset>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
                .Returns(_ => { _edgeEnded = !DeclineEdgeClosing; return Task.FromResult(_edgeEnded); });

            _facts.UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>()).Returns(ci => ci.Arg<Fact>());
            _facts.FindSupersededCandidatesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<DateTimeOffset>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult<IReadOnlyList<Fact>>(correction ? [] : _stored));
            _facts.GetBySubjectAsync(Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
                .Returns(ci => Task.FromResult<IReadOnlyList<Fact>>(ci.ArgAt<string>(0) switch
                {
                    "Mira" => _stored,
                    var subject when subject == oldValue => [date],
                    _ => [],
                }));
            _facts.SupersedeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
                .Returns(ci => _accepted.Contains(ci.ArgAt<string>(0)));
            _facts.SupersedeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<FactClosureReason>(),
                    Arg.Any<DateTimeOffset?>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
                .Returns(ci => _accepted.Contains(ci.ArgAt<string>(0)));

            var clock = Substitute.For<IClock>();
            clock.UtcNow.Returns(Now);
            var ids = Substitute.For<IIdGenerator>();
            ids.GenerateId().Returns("new-fact");
            var embeddings = Substitute.For<IEmbeddingOrchestrator>();
            embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[8]);
            _stage = new PersistenceStage(embeddings, entities, _facts, Substitute.For<IPreferenceRepository>(),
                Relationships, clock, ids, NullLogger<PersistenceStage>.Instance, new PassThroughMemoryPersistenceTransaction(),
                Options.Create(new ExtractionOptions
                {
                    SupersedeReplacedFacts = true,
                    BitemporalChanges = bitemporal,
                    EnableBatchMemoryUpserts = false,
                }));
            _incoming = new ExtractedFact
            {
                Subject = "Mira", Predicate = predicate, Object = newValue, Confidence = 0.9,
                Replaces = correction ? oldValue : null,
            };
        }

        public void AcceptClosing(string id) => _accepted.Add(id);

        public void AddLiveTwin() => _stored.Add(_stored[0] with
        {
            FactId = "also-live", Object = "the " + _stored[0].Object,
        });

        public Task<PersistenceResult> PersistAsync() => _stage.PersistAsync(new ExtractionStageResult
        {
            FilteredFacts = [_incoming], SourceMessageIds = ["new-message"],
        }, ownerId: "owner");

        public async Task AssertClosingAttemptedAsync(string id)
        {
            if (_bitemporal)
                await _facts.Received(1).SupersedeAsync(id, Arg.Any<string>(), Arg.Any<FactClosureReason>(),
                    Arg.Any<DateTimeOffset?>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
            else
                await _facts.Received(1).SupersedeAsync(id, Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
        }

        public async Task AssertClosingNotAttemptedAsync(string id)
        {
            await _facts.DidNotReceive().SupersedeAsync(id, Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
            await _facts.DidNotReceive().SupersedeAsync(id, Arg.Any<string>(), Arg.Any<FactClosureReason>(),
                Arg.Any<DateTimeOffset?>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
        }
    }
}
