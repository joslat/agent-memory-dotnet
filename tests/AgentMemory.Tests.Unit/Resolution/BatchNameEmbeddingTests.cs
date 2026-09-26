using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Resolution;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Resolution;

/// <summary>
/// New entity names in one extraction are embedded in ONE request, not one round trip each.
/// </summary>
/// <remarks>
/// Measured live before: "Priya Nair", "Contoso", "Madrid" cost three sequential ~1 s embedding calls
/// during resolution. Names a string matcher resolves need no vector and are left out, so a turn that
/// only mentions known entities makes no request at all.
/// </remarks>
public sealed class BatchNameEmbeddingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly IEntityRepository _entities = Substitute.For<IEntityRepository>();
    private readonly IEmbeddingOrchestrator _embeddings = Substitute.For<IEmbeddingOrchestrator>();

    public BatchNameEmbeddingTests()
    {
        _entities.GetByTypeAsync(Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>(
            [
                new Entity
                {
                    EntityId = "alice", Name = "Alice", Type = "PERSON", Confidence = 1,
                    Embedding = [1f, 0f, 0f], CreatedAtUtc = T0,
                },
            ]));
        _embeddings.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<IReadOnlyList<float[]>>(
                ci.Arg<IReadOnlyList<string>>().Select((_, i) => new[] { 0f, 1f, i }).ToList()));
        _embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new[] { 0f, 0f, 1f }));
    }

    private CompositeEntityResolver Sut()
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(T0);
        var ids = Substitute.For<IIdGenerator>();
        ids.GenerateId().Returns(_ => Guid.NewGuid().ToString("N"));
        return new CompositeEntityResolver(
            _entities, _embeddings, Options.Create(new ExtractionOptions()), clock, ids,
            NullLogger<CompositeEntityResolver>.Instance);
    }

    private static ExtractedEntity E(string name, string type = "PERSON") => new() { Name = name, Type = type };

    [Fact]
    public async Task Unknown_names_are_embedded_together_and_the_matcher_uses_those_vectors()
    {
        var sut = Sut();
        using var batch = sut.BeginBatch();
        ExtractedEntity[] extracted = [E("Alice"), E("Contoso", "ORGANIZATION"), E("Madrid", "LOCATION")];
        await sut.PrepareCandidatesAsync(extracted.Select(e => e.Type).ToArray());

        await sut.PrepareNameEmbeddingsAsync(extracted);

        await _embeddings.Received(1).EmbedBatchAsync(
            Arg.Is<IReadOnlyList<string>>(names => names.SequenceEqual(new[] { "Contoso", "Madrid" })),
            Arg.Any<CancellationToken>());

        var contoso = await sut.ResolveForPersistenceAsync(extracted[1], []);
        var madrid = await sut.ResolveForPersistenceAsync(extracted[2], []);
        var alice = await sut.ResolveForPersistenceAsync(extracted[0], []);

        contoso.Embedding.Should().Equal(0f, 1f, 0f);
        madrid.Embedding.Should().Equal(0f, 1f, 1f);
        alice.EntityId.Should().Be("alice");
        await _embeddings.DidNotReceive().EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_turn_that_mentions_only_known_names_makes_no_request()
    {
        var sut = Sut();
        using var batch = sut.BeginBatch();
        ExtractedEntity[] extracted = [E("Alice"), E("alice")];
        await sut.PrepareCandidatesAsync(["PERSON"]);

        await sut.PrepareNameEmbeddingsAsync(extracted);

        await _embeddings.DidNotReceiveWithAnyArgs().EmbedBatchAsync(default!, default);
    }

    [Fact]
    public async Task A_single_new_name_is_left_to_the_normal_path()
    {
        var sut = Sut();
        using var batch = sut.BeginBatch();
        await sut.PrepareCandidatesAsync(["PERSON", "ORGANIZATION"]);

        await sut.PrepareNameEmbeddingsAsync([E("Alice"), E("Contoso", "ORGANIZATION")]);

        await _embeddings.DidNotReceiveWithAnyArgs().EmbedBatchAsync(default!, default);
    }

    [Fact]
    public async Task Without_a_batch_nothing_is_prepared()
    {
        await Sut().PrepareNameEmbeddingsAsync([E("Contoso", "ORGANIZATION"), E("Madrid", "LOCATION")]);

        await _embeddings.DidNotReceiveWithAnyArgs().EmbedBatchAsync(default!, default);
    }
}
