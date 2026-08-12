using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// The payload projection against a real database, where a MAP is not a Node.
/// </summary>
/// <remarks>
/// <para>
/// The Cypher shape is unit-tested. What only Neo4j can show is that <c>node {.*, embedding: NULL}</c>
/// deserializes into the <i>same</i> memory the un-projected query returns — because the projected
/// path takes a different mapper overload, and a property that silently stopped arriving would show
/// up as a null field rather than as an error.
/// </para>
/// <para>
/// That is the whole risk of this change: it is on the hottest path in the system, and getting it
/// subtly wrong produces memories that look fine.
/// </para>
/// </remarks>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class RecallPayloadProjectionIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;

    public RecallPayloadProjectionIntegrationTests(Neo4jIntegrationFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // Four dimensions: the integration fixture's vector indexes are built at that width, and the
    // driver rejects a mismatched query vector outright.
    private static readonly float[] Vector = [0.5f, 0.5f, 0.5f, 0.5f];
    private static readonly MemoryScope Alice = MemoryScope.For("alice", includeShared: false);

    private Neo4jEntityRepository Entities(bool omit) =>
        new(_fixture.TransactionRunner, NullLogger<Neo4jEntityRepository>.Instance,
            memoryOptions: Options.Create(new MemoryOptions { OmitEmbeddingsFromRecall = omit }));

    private Neo4jFactRepository Facts(bool omit) =>
        new(_fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance,
            memoryOptions: Options.Create(new MemoryOptions { OmitEmbeddingsFromRecall = omit }));

    private async Task SeedEntityAsync() =>
        await Entities(false).UpsertAsync(new Entity
        {
            EntityId = "e-1",
            Name = "Alice",
            Type = "Person",
            Subtype = "Colleague",
            Description = "works in the Zurich office",
            Confidence = 0.87,
            OwnerId = "alice",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Embedding = Vector,
        });

    [Fact]
    public async Task TheProjectedEntityMatchesTheUnprojectedOneFieldForField()
    {
        // THE test. Two mapper overloads reading the same stored row must produce the same memory --
        // everything except the vector, which is the point.
        await SeedEntityAsync();

        var full = await Entities(false).SearchByVectorAsync(Vector, 10, 0.0, Alice);
        var projected = await Entities(true).SearchByVectorAsync(Vector, 10, 0.0, Alice);

        full.Should().ContainSingle();
        projected.Should().ContainSingle();

        var a = full[0].Entity;
        var b = projected[0].Entity;

        b.EntityId.Should().Be(a.EntityId);
        b.Name.Should().Be(a.Name);
        b.Type.Should().Be(a.Type);
        b.Subtype.Should().Be(a.Subtype);
        b.Description.Should().Be(a.Description);
        b.Confidence.Should().Be(a.Confidence);
        b.OwnerId.Should().Be(a.OwnerId);
        b.CreatedAtUtc.Should().Be(a.CreatedAtUtc);
    }

    [Fact]
    public async Task TheVectorIsPresentByDefaultAndAbsentWhenProjected()
    {
        // Both halves in one assertion pair. The first is what keeps the TCK bridge working; the
        // second is the ~3 KB an item that stops crossing the wire.
        await SeedEntityAsync();

        (await Entities(false).SearchByVectorAsync(Vector, 10, 0.0, Alice))[0]
            .Entity.Embedding.Should().NotBeNullOrEmpty();

        (await Entities(true).SearchByVectorAsync(Vector, 10, 0.0, Alice))[0]
            .Entity.Embedding.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task ScoresAreUnchangedByTheProjection()
    {
        // Similarity is computed inside the index, so projecting the payload must not touch ranking.
        // If it did, the change would trade a payload win for a silent relevance regression.
        await SeedEntityAsync();

        var full = await Entities(false).SearchByVectorAsync(Vector, 10, 0.0, Alice);
        var projected = await Entities(true).SearchByVectorAsync(Vector, 10, 0.0, Alice);

        projected[0].Score.Should().BeApproximately(full[0].Score, 1e-9);
    }

    [Fact]
    public async Task FactsProjectEquivalentlyToo()
    {
        await Facts(false).UpsertAsync(new Fact
        {
            FactId = "f-1",
            Subject = "Alice",
            Predicate = "lives in",
            Object = "Zurich",
            Confidence = 0.91,
            OwnerId = "alice",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Embedding = Vector,
        });

        var full = await Facts(false).SearchByVectorAsync(Vector, 10, 0.0, Alice);
        var projected = await Facts(true).SearchByVectorAsync(Vector, 10, 0.0, Alice);

        projected.Should().ContainSingle();
        projected[0].Fact.Subject.Should().Be(full[0].Fact.Subject);
        projected[0].Fact.Predicate.Should().Be(full[0].Fact.Predicate);
        projected[0].Fact.Object.Should().Be(full[0].Fact.Object);
        projected[0].Fact.Confidence.Should().Be(full[0].Fact.Confidence);
        projected[0].Fact.Embedding.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task OwnerIsolationSurvivesTheProjection()
    {
        // R1 is enforced in the WHERE clause, which the projection does not touch -- asserted rather
        // than assumed, because a payload change that widened visibility would be the worst possible
        // way to save 3 KB.
        await SeedEntityAsync();

        var otherOwner = await Entities(true)
            .SearchByVectorAsync(Vector, 10, 0.0, MemoryScope.For("bob", includeShared: false));

        otherOwner.Should().BeEmpty();
    }
}
