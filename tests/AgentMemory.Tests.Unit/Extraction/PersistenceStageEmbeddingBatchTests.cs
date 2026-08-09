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

public sealed class PersistenceStageEmbeddingBatchTests
{
    private static readonly string[] ExpectedTexts =
    [
        "Alice",
        "Bob",
        "Alice likes coffee",
        "Bob likes tea",
        "Prefers concise answers"
    ];

    private readonly IEmbeddingOrchestrator _orchestrator = Substitute.For<IEmbeddingOrchestrator>();
    private readonly IEntityRepository _entityRepository = Substitute.For<IEntityRepository>();
    private readonly IFactRepository _factRepository = Substitute.For<IFactRepository>();
    private readonly IPreferenceRepository _preferenceRepository = Substitute.For<IPreferenceRepository>();
    private readonly IRelationshipRepository _relationshipRepository = Substitute.For<IRelationshipRepository>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IIdGenerator _idGenerator = Substitute.For<IIdGenerator>();

    public PersistenceStageEmbeddingBatchTests()
    {
        _clock.UtcNow.Returns(DateTimeOffset.Parse("2026-08-03T00:00:00Z"));
        _idGenerator.GenerateId().Returns("fact-1", "fact-2", "preference-1");
        _orchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => [(float)call.Arg<string>().Length]);

        _entityRepository.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Entity>());
        _factRepository.UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Fact>());
        _preferenceRepository.UpsertAsync(Arg.Any<Preference>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Preference>());
        _relationshipRepository.UpsertAsync(Arg.Any<Relationship>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Relationship>());
    }

    [Fact]
    public void ExtractionOptions_DefaultsLearnedEmbeddingBatchingOn()
    {
        new ExtractionOptions().UseBatchEmbeddingRequests.Should().BeTrue();
    }

    [Fact]
    public async Task PersistAsync_Default_BatchesMissingLearnedEmbeddingsInStableOrder()
    {
        _orchestrator.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Vectors(1, 2, 3, 4, 5));

        var result = await CreateSut().PersistAsync(CreateExtraction());

        await _orchestrator.Received(1).EmbedBatchAsync(
            Arg.Is<IReadOnlyList<string>>(texts => texts.SequenceEqual(ExpectedTexts)),
            Arg.Any<CancellationToken>());
        await _orchestrator.DidNotReceive().EmbedAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());

        await _entityRepository.Received(1).UpsertAsync(
            Arg.Is<Entity>(item => item.Name == "Alice" && item.Embedding![0] == 1),
            Arg.Any<CancellationToken>());
        await _entityRepository.Received(1).UpsertAsync(
            Arg.Is<Entity>(item => item.Name == "Bob" && item.Embedding![0] == 2),
            Arg.Any<CancellationToken>());
        await _factRepository.Received(1).UpsertAsync(
            Arg.Is<Fact>(item => item.Subject == "Alice" && item.Embedding != null && item.Embedding.Length > 0 && item.Embedding[0] == 3),
            Arg.Any<CancellationToken>());
        await _factRepository.Received(1).UpsertAsync(
            Arg.Is<Fact>(item => item.Subject == "Bob" && item.Embedding != null && item.Embedding.Length > 0 && item.Embedding[0] == 4),
            Arg.Any<CancellationToken>());
        await _preferenceRepository.Received(1).UpsertAsync(
            Arg.Is<Preference>(item => item.PreferenceText == "Prefers concise answers" && item.Embedding != null && item.Embedding.Length > 0 && item.Embedding[0] == 5),
            Arg.Any<CancellationToken>());

        result.EntityCount.Should().Be(2);
        result.FactCount.Should().Be(2);
        result.PreferenceCount.Should().Be(1);
    }

    [Fact]
    public async Task PersistAsync_OptionOff_PreservesFiveSingleRequests()
    {
        await CreateSut(new ExtractionOptions { UseBatchEmbeddingRequests = false })
            .PersistAsync(CreateExtraction());

        await _orchestrator.DidNotReceive().EmbedBatchAsync(
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        await _orchestrator.Received(5).EmbedAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistAsync_BatchCountMismatch_ReplaysWholeBatch()
    {
        _orchestrator.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Vectors(99));

        var result = await CreateSut().PersistAsync(CreateExtraction());

        await _orchestrator.Received(5).EmbedAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        result.EntityCount.Should().Be(2);
        result.FactCount.Should().Be(2);
        result.PreferenceCount.Should().Be(1);
    }

    [Fact]
    public async Task PersistAsync_AlignedEmptySlot_ReplaysOnlyThatSlot()
    {
        _orchestrator.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new List<float[]> { new[] { 1f }, new[] { 2f }, Array.Empty<float>(), new[] { 4f }, new[] { 5f } });

        await CreateSut().PersistAsync(CreateExtraction());

        await _orchestrator.Received(1).EmbedAsync(
            "Alice likes coffee", Arg.Any<CancellationToken>());
        await _orchestrator.Received(1).EmbedAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistAsync_ThrowingBatch_ReplaysWholeBatch()
    {
        _orchestrator.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<float[]>>(_ => throw new InvalidOperationException("batch failed"));

        var result = await CreateSut().PersistAsync(CreateExtraction());

        await _orchestrator.Received(5).EmbedAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        result.EntityCount.Should().Be(2);
        result.FactCount.Should().Be(2);
        result.PreferenceCount.Should().Be(1);
    }

    [Fact]
    public async Task PersistAsync_CancelledBatch_PropagatesCancellationWithoutFallback()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _orchestrator.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), cts.Token)
            .Returns<IReadOnlyList<float[]>>(_ => throw new OperationCanceledException(cts.Token));

        var act = () => CreateSut().PersistAsync(CreateExtraction(), cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await _orchestrator.DidNotReceive().EmbedAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistAsync_PreEmbeddedEntity_IsExcludedFromBatchAndRetained()
    {
        var extraction = CreateExtraction() with
        {
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase)
            {
                ["Alice"] = Entity("entity-1", "Alice") with { Embedding = [42] },
                ["Bob"] = Entity("entity-2", "Bob")
            }
        };
        _orchestrator.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Vectors(2, 3, 4, 5));

        await CreateSut().PersistAsync(extraction);

        await _orchestrator.Received(1).EmbedBatchAsync(
            Arg.Is<IReadOnlyList<string>>(texts =>
                texts.SequenceEqual(ExpectedTexts.Skip(1))),
            Arg.Any<CancellationToken>());
        await _entityRepository.Received(1).UpsertAsync(
            Arg.Is<Entity>(item => item.Name == "Alice" && item.Embedding![0] == 42),
            Arg.Any<CancellationToken>());
    }

    private PersistenceStage CreateSut(ExtractionOptions? options = null) =>
        new(
            _orchestrator,
            _entityRepository,
            _factRepository,
            _preferenceRepository,
            _relationshipRepository,
            _clock,
            _idGenerator,
            NullLogger<PersistenceStage>.Instance,
            new PassThroughMemoryPersistenceTransaction(),
            Options.Create(options ?? new ExtractionOptions()));

    private static ExtractionStageResult CreateExtraction() =>
        new()
        {
            SourceMessageIds = [],
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase)
            {
                ["Alice"] = Entity("entity-1", "Alice"),
                ["Bob"] = Entity("entity-2", "Bob")
            },
            FilteredFacts =
            [
                new ExtractedFact
                {
                    Subject = "Alice",
                    Predicate = "likes",
                    Object = "coffee",
                    Confidence = 0.9
                },
                new ExtractedFact
                {
                    Subject = "Bob",
                    Predicate = "likes",
                    Object = "tea",
                    Confidence = 0.8
                }
            ],
            FilteredPreferences =
            [
                new ExtractedPreference
                {
                    Category = "style",
                    PreferenceText = "Prefers concise answers",
                    Confidence = 0.9
                }
            ],
            FilteredRelationships = []
        };

    private static Entity Entity(string id, string name) =>
        new()
        {
            EntityId = id,
            Name = name,
            Type = "Person",
            Confidence = 0.9,
            CreatedAtUtc = DateTimeOffset.Parse("2026-08-03T00:00:00Z")
        };

    private static IReadOnlyList<float[]> Vectors(params float[] values) =>
        values.Select(value => new[] { value }).ToArray();
}
