using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// D5 transaction-clock behaviour against live Neo4j: soft-invalidating a fact (stamping
/// <c>invalidated_at</c>) removes it from live recall but keeps it — it is still visible to as-of recall
/// for a time before invalidation (the bitemporal transaction-time axis). Invalidation is owner-scoped
/// (R1): a scoped invalidate cannot touch another owner's fact.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class InvalidationIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jFactRepository _facts;

    private static readonly float[] Emb = [0.5f, 0.5f, 0.5f, 0.5f];

    public InvalidationIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _facts = new Neo4jFactRepository(fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Invalidate_RemovesFromLiveRecall_ButKeepsForAsOfBeforeInvalidation()
    {
        await _facts.UpsertAsync(new Fact
        {
            FactId = "f1", Subject = "sky", Predicate = "is", Object = "blue",
            Confidence = 0.9, Embedding = Emb, CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-10),
        });

        // Present in live recall before invalidation.
        (await _facts.SearchByVectorAsync(Emb, limit: 5))
            .Should().ContainSingle(r => r.Fact.FactId == "f1");

        // A point in time after creation but before invalidation (the fact was still believed then).
        var asOfBefore = DateTimeOffset.UtcNow.AddDays(-5);

        var invalidated = await _facts.InvalidateAsync("f1");
        invalidated.Should().BeTrue();

        // Live recall now excludes it (transaction clock — no longer believed).
        (await _facts.SearchByVectorAsync(Emb, limit: 5))
            .Should().NotContain(r => r.Fact.FactId == "f1",
                "a soft-invalidated fact drops out of live recall");

        // But as-of a time before invalidation, it is still recalled — nothing was deleted.
        (await _facts.SearchByVectorAsOfAsync(Emb, asOfBefore, limit: 5))
            .Should().Contain(r => r.Fact.FactId == "f1",
                "the fact was still believed at the earlier time, so as-of recall keeps it");
    }

    [Fact]
    public async Task Invalidate_IsOwnerScoped_CannotTouchAnotherOwner()
    {
        await _facts.UpsertAsync(new Fact
        {
            FactId = "f-bob", Subject = "bob", Predicate = "likes", Object = "x",
            Confidence = 0.9, Embedding = Emb, OwnerId = "bob", CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
        });

        // Alice tries to invalidate Bob's fact — must not match.
        var hit = await _facts.InvalidateAsync("f-bob", MemoryScope.For("alice"));
        hit.Should().BeFalse("a scoped invalidate must not touch another owner's fact");

        // Bob's fact is untouched and still in live recall.
        (await _facts.SearchByVectorAsync(Emb, limit: 5))
            .Should().Contain(r => r.Fact.FactId == "f-bob");
    }
}
