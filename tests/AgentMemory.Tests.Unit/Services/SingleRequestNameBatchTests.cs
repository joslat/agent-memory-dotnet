using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;
using AgentMemory.Core.Resolution;
using AgentMemory.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// H-1: one agent turn (a single extraction request) embeds its new entity names in ONE request.
/// </summary>
/// <remarks>
/// Measured in a traced turn: “My brother Pablo lives in Seville and my sister Lena lives in Berlin” made four
/// sequential single-name embedding calls in resolution, because only the multi-session batch path opened the
/// resolution batch the name pre-embedding needs; the single-request path (every agent turn) never did.
/// </remarks>
public sealed class SingleRequestNameBatchTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);
    private readonly IEntityRepository _entities = Substitute.For<IEntityRepository>();
    private readonly IEmbeddingOrchestrator _embeddings = Substitute.For<IEmbeddingOrchestrator>();
    private readonly IPersistenceStage _persistence = Substitute.For<IPersistenceStage>();

    public SingleRequestNameBatchTests()
    {
        _entities.GetByTypeAsync(Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>([]));
        _embeddings.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<IReadOnlyList<float[]>>(
                ci.Arg<IReadOnlyList<string>>().Select((_, i) => new[] { 1f, i, 0f }).ToList()));
        _embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new[] { 0f, 0f, 1f }));
        _persistence.PersistAsync(Arg.Any<ExtractionStageResult>(), Arg.Any<string?>(), Arg.Any<MemoryTrustLevel>(), Arg.Any<CancellationToken>())
            .Returns(new PersistenceResult());
    }

    private MemoryExtractionPipeline Pipeline(IEntityExtractor extractor)
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(T0);
        var ids = Substitute.For<IIdGenerator>();
        ids.GenerateId().Returns(_ => Guid.NewGuid().ToString("N"));
        var options = Options.Create(new ExtractionOptions());
        var resolver = new CompositeEntityResolver(
            _entities, _embeddings, options, clock, ids, NullLogger<CompositeEntityResolver>.Instance);
        var stage = new ExtractionStage([extractor], [], [], [], [], resolver, options, NullLogger<ExtractionStage>.Instance);
        return new MemoryExtractionPipeline(
            stage, _persistence, NullLogger<MemoryExtractionPipeline>.Instance,
            new DefaultMemoryIsolationPolicy(Options.Create(new MemoryIsolationOptions()), NullLogger<DefaultMemoryIsolationPolicy>.Instance),
            options, []);
    }

    private static IEntityExtractor Extracts(params (string Name, string Type)[] entities)
    {
        var extractor = Substitute.For<IEntityExtractor>();
        extractor.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(entities.Select(e => new ExtractedEntity { Name = e.Name, Type = e.Type, Confidence = 0.99 }).ToList());
        return extractor;
    }

    private static ExtractionRequest Turn() => new()
    {
        SessionId = "s1",
        UserId = "owner-1",
        TypesToExtract = ExtractionTypes.Entities,
        Messages =
        [
            new Message
            {
                MessageId = "m1", SessionId = "s1", ConversationId = "s1", Role = "user", TimestampUtc = T0,
                Content = "My brother Pablo lives in Seville and my sister Lena lives in Berlin.",
            },
        ],
    };

    [Fact]
    public async Task One_turn_embeds_its_new_names_in_one_request()
    {
        var sut = Pipeline(Extracts(("Pablo", "PERSON"), ("Seville", "LOCATION"), ("Lena", "PERSON"), ("Berlin", "LOCATION")));

        await sut.ExtractAsync(Turn());

        await _embeddings.Received(1).EmbedBatchAsync(
            Arg.Is<IReadOnlyList<string>>(names => names.OrderBy(n => n).SequenceEqual(new[] { "Berlin", "Lena", "Pablo", "Seville" })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_single_request_inside_an_open_batch_joins_it_instead_of_failing()
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(T0);
        var resolver = new CompositeEntityResolver(
            _entities, _embeddings, Options.Create(new ExtractionOptions()), clock, Substitute.For<IIdGenerator>(),
            NullLogger<CompositeEntityResolver>.Instance);

        using var outer = resolver.BeginBatch();
        var joined = () => resolver.BeginOrJoinBatch().Dispose();

        joined.Should().NotThrow();
        var nested = () => resolver.BeginBatch();
        nested.Should().Throw<InvalidOperationException>("the outer batch is still open: joining did not end it");
    }
}
