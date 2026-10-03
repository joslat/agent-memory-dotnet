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
/// G4 (PLAN 40.48): an ingestion outcome says what its write did: a new memory, one already stored, and, for a fact that
/// replaced others, which it closed. Additive: every outcome a caller read before reads the same.
/// </summary>
public sealed class WriteEffectsTests
{
    private static readonly DateTimeOffset Today = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    private readonly IFactRepository _facts = Substitute.For<IFactRepository>();
    private readonly IPreferenceRepository _preferences = Substitute.For<IPreferenceRepository>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public WriteEffectsTests()
    {
        _clock.UtcNow.Returns(Today);
        _facts.UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>()).Returns(ci => Task.FromResult(ci.Arg<Fact>()));
        _facts.GetBySubjectAsync(Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>([]));
        _facts.FindSupersededCandidatesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<DateTimeOffset>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>([]));
        _facts.SupersedeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        _preferences.UpsertAsync(Arg.Any<Preference>(), Arg.Any<CancellationToken>()).Returns(ci => Task.FromResult(ci.Arg<Preference>()));
    }

    private static ExtractedFact Lives(string city) => new() { Subject = "user", Predicate = "lives in", Object = city, Confidence = 0.9 };

    private PersistenceStage Sut()
    {
        var embeddings = Substitute.For<IEmbeddingOrchestrator>();
        embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[8]);
        var ids = Substitute.For<IIdGenerator>();
        ids.GenerateId().Returns(_ => Guid.NewGuid().ToString("N"));
        return new PersistenceStage(embeddings, Substitute.For<IEntityRepository>(), _facts, _preferences,
            Substitute.For<IRelationshipRepository>(), _clock, ids, NullLogger<PersistenceStage>.Instance,
            new PassThroughMemoryPersistenceTransaction(),
            Options.Create(new ExtractionOptions { SupersedeReplacedFacts = true, EnableBatchMemoryUpserts = false }));
    }

    private static IngestionItemOutcome Only(PersistenceResult result, MemoryItemKind kind) =>
        result.Outcomes.Single(o => o.Kind == kind && o.Status == IngestionItemStatus.Succeeded);

    [Fact]
    public async Task A_new_fact_is_created_and_closes_nothing()
    {
        var result = await Sut().PersistAsync(new ExtractionStageResult { FilteredFacts = [Lives("Madrid")] }, ownerId: "owner-1");

        var outcome = Only(result, MemoryItemKind.Fact);
        outcome.Effect.Should().Be(MemoryWriteEffect.Created);
        outcome.Closed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_fact_the_store_already_held_is_already_stored()
    {
        // The upsert MERGEs on the natural triple and returns the stored fact's id.
        _facts.UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<Fact>() with { FactId = "stored-madrid" }));

        var result = await Sut().PersistAsync(new ExtractionStageResult { FilteredFacts = [Lives("Madrid")] }, ownerId: "owner-1");

        var outcome = Only(result, MemoryItemKind.Fact);
        outcome.Effect.Should().Be(MemoryWriteEffect.AlreadyStored);
        outcome.PersistedId.Should().Be("stored-madrid");
    }

    [Fact]
    public async Task A_change_says_which_fact_it_closed()
    {
        var bilbao = new Fact
        {
            FactId = "bilbao", Subject = "user", Predicate = "lives in", Object = "Bilbao", Confidence = 0.9, CreatedAtUtc = Today.AddYears(-1),
        };
        _facts.FindSupersededCandidatesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<DateTimeOffset>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>([bilbao]));

        var result = await Sut().PersistAsync(new ExtractionStageResult { FilteredFacts = [Lives("Madrid")] }, ownerId: "owner-1");

        var outcome = Only(result, MemoryItemKind.Fact);
        outcome.Effect.Should().Be(MemoryWriteEffect.Created);
        outcome.Closed.Should().Equal(["bilbao"]);
    }

    [Fact]
    public async Task A_preference_says_whether_it_was_new()
    {
        var created = await Sut().PersistAsync(
            new ExtractionStageResult { FilteredPreferences = [new ExtractedPreference { Category = "drinks", PreferenceText = "Prefers tea", Confidence = 0.9 }] },
            ownerId: "owner-1");
        Only(created, MemoryItemKind.Preference).Effect.Should().Be(MemoryWriteEffect.Created);

        _preferences.UpsertAsync(Arg.Any<Preference>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<Preference>() with { PreferenceId = "stored-tea" }));
        var again = await Sut().PersistAsync(
            new ExtractionStageResult { FilteredPreferences = [new ExtractedPreference { Category = "drinks", PreferenceText = "Prefers tea", Confidence = 0.9 }] },
            ownerId: "owner-1");
        Only(again, MemoryItemKind.Preference).Effect.Should().Be(MemoryWriteEffect.AlreadyStored);
    }
}
