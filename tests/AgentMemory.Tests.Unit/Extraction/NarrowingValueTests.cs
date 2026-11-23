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

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// SUPERSEDE-NARROWER: on 2.3.1 Pablo's people scenario lost "the go-live takes place at Acme's office in Córdoba" when the
/// writer named "the go-live takes place at a war room at Acme during the go-live week" as its replacement and the judge
/// agreed. A value that only narrows the stored one is kept beside it; a value that names something new still replaces.
/// </summary>
public sealed class NarrowingValueTests
{
    [Theory]
    [InlineData("Acme's office in Córdoba", "a war room at Acme during the go-live week", true)]
    [InlineData("Acme's office in Córdoba", "Acme's office in Rosario", false)]
    [InlineData("Lisbon", "Madrid", false)]
    [InlineData("Paris", "Paris, 15th arrondissement", false)]
    [InlineData("Friday, December 11", "Friday, December 18", false)]
    [InlineData("Friday, December 11", "December 11 at 7 am", true)]
    [InlineData("the new billing system", "the old billing system", false)]
    public void A_value_narrows_only_when_it_drops_a_name_and_adds_none(string stored, string written, bool narrows) =>
        NarrowingValue.Narrows(stored, written).Should().Be(narrows);

    private const string Event = "Acme Logistics go-live";
    private static readonly DateTimeOffset At = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    private readonly IFactRepository _facts = Substitute.For<IFactRepository>();
    private readonly IMemoryUpdateJudge _judge = Substitute.For<IMemoryUpdateJudge>();

    public NarrowingValueTests()
    {
        _facts.UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<Fact>() with { FactId = "new" });
        _facts.GetByIdAsync("cordoba", Arg.Any<CancellationToken>()).Returns(new Fact
        {
            FactId = "cordoba", Subject = Event, Predicate = "takes place at", Object = "Acme's office in Córdoba",
            OwnerId = "bruna", Confidence = 1, CreatedAtUtc = At.AddMonths(-2),
        });
        _facts.SupersedeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>()).Returns(true);
        _judge.IsEnabled.Returns(true);
        // The judge of the 2.3.1 run: it agreed that the narrower place replaces the stored one.
        _judge.JudgeAsync(Arg.Any<MemoryUpdateRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
            (IReadOnlyDictionary<string, double>)call.Arg<MemoryUpdateRequest>().Pairs.ToDictionary(p => p.Key, _ => 1.0));
    }

    private Task<PersistenceResult> WriteAsync(string place, bool correction = false)
    {
        var embeddings = Substitute.For<IEmbeddingOrchestrator>();
        embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 1, 0 });
        var ids = Substitute.For<IIdGenerator>(); ids.GenerateId().Returns(_ => Guid.NewGuid().ToString("N"));
        var stage = new PersistenceStage(embeddings, Substitute.For<IEntityRepository>(), _facts,
            Substitute.For<IPreferenceRepository>(), Substitute.For<IRelationshipRepository>(), TimeProvider.System, ids,
            NullLogger<PersistenceStage>.Instance, new PassThroughMemoryPersistenceTransaction(),
            Options.Create(new ExtractionOptions { UseBatchEmbeddingRequests = false }), updateJudge: _judge);
        return stage.PersistAsync(new()
        {
            SourceMessageIds = ["m1"], SourceText = "We need a war room at Acme during the go-live week.", WrittenByWriter = true,
            FilteredFacts = [new() { Subject = Event, Predicate = "takes place at", Object = place, Confidence = 1,
                ReplacesId = "cordoba", ReplacementIsCorrection = correction }],
        }, "bruna");
    }

    [Fact]
    public async Task A_narrower_place_is_kept_beside_the_stored_one_and_never_judged()
    {
        await WriteAsync("a war room at Acme during the go-live week");

        await _facts.DidNotReceive().SupersedeAsync("cordoba", Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
        await _judge.DidNotReceive().JudgeAsync(Arg.Any<MemoryUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_new_place_still_replaces_the_stored_one()
    {
        await WriteAsync("Acme's office in Rosario");

        await _facts.Received(1).SupersedeAsync("cordoba", "new", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_correction_the_writer_flags_is_still_judged()
    {
        await WriteAsync("a war room at Acme during the go-live week", correction: true);

        await _judge.Received(1).JudgeAsync(Arg.Any<MemoryUpdateRequest>(), Arg.Any<CancellationToken>());
    }
}
