using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;
using Neo4j.Driver;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// End-to-end multi-user isolation guarantees (R1). Verifies that owner-scoped vector recall returns
/// only the owner's (plus shared/global) knowledge, that shared rows are visible to everyone, that the
/// same triple stored by different owners does not overwrite, and that an owner filter is not starved
/// by higher-volume foreign rows (over-fetch).
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class OwnerScopeIsolationIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jFactRepository _repo;

    // Identical embeddings → all candidates score 1.0, so ranking can never "naturally" prefer the
    // owner's rows; only the owner WHERE-filter (and over-fetch) can keep them from being starved.
    private static readonly float[] Embedding = [0.3f, 0.1f, 0.4f, 0.2f];

    public OwnerScopeIsolationIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _repo = new Neo4jFactRepository(fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private Fact NewFact(string subject, string predicate, string @object, string? ownerId) => new()
    {
        FactId = $"fact-{Guid.NewGuid():N}",
        Subject = subject,
        Predicate = predicate,
        Object = @object,
        Confidence = 0.9,
        OwnerId = ownerId,
        Embedding = Embedding,
        CreatedAtUtc = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task ScopedSearch_ReturnsOnlyOwnersAndSharedFacts_NotOtherUsers()
    {
        await _repo.UpsertAsync(NewFact("Alice", "prefers", "dark mode", ownerId: "alice"));
        await _repo.UpsertAsync(NewFact("Bob", "prefers", "light mode", ownerId: "bob"));
        await _repo.UpsertAsync(NewFact("Org", "policy", "remote-first", ownerId: null)); // shared

        var results = await _repo.SearchByVectorAsync(
            Embedding, limit: 10, scope: MemoryScope.For("alice"));

        var owners = results.Select(r => r.Fact.OwnerId).ToList();
        owners.Should().Contain("alice");
        owners.Should().Contain((string?)null);          // shared visible
        owners.Should().NotContain("bob");               // other user's memory never leaks
    }

    [Fact]
    public async Task ScopedSearch_IncludeSharedFalse_ReturnsOwnerOnly()
    {
        await _repo.UpsertAsync(NewFact("Alice", "prefers", "dark mode", ownerId: "alice"));
        await _repo.UpsertAsync(NewFact("Org", "policy", "remote-first", ownerId: null)); // shared

        var results = await _repo.SearchByVectorAsync(
            Embedding, limit: 10, scope: MemoryScope.For("alice", includeShared: false));

        results.Should().NotBeEmpty();
        results.Select(r => r.Fact.OwnerId).Should().AllBe("alice");
    }

    [Fact]
    public async Task UnscopedSearch_ReturnsEveryone() // backward-compatible default
    {
        await _repo.UpsertAsync(NewFact("Alice", "prefers", "dark mode", ownerId: "alice"));
        await _repo.UpsertAsync(NewFact("Bob", "prefers", "light mode", ownerId: "bob"));
        await _repo.UpsertAsync(NewFact("Org", "policy", "remote-first", ownerId: null));

        var results = await _repo.SearchByVectorAsync(Embedding, limit: 10); // scope: null

        results.Select(r => r.Fact.OwnerId).Should().Contain(new[] { "alice", "bob", (string?)null });
    }

    [Fact]
    public async Task SameTriple_DifferentOwners_ProducesDistinctNodes_NoOverwrite()
    {
        await _repo.UpsertAsync(NewFact("Alice", "works_at", "Acme", ownerId: "alice"));
        await _repo.UpsertAsync(NewFact("Alice", "works_at", "Acme", ownerId: "bob"));
        await _repo.UpsertAsync(NewFact("Alice", "works_at", "Acme", ownerId: null)); // shared

        var count = await _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                "MATCH (f:Fact {subject:$s, predicate:$p, object:$o}) RETURN count(f) AS c",
                new { s = "Alice", p = "works_at", o = "Acme" });
            var record = await cursor.SingleAsync();
            return global::Neo4j.Driver.ValueExtensions.As<long>(record["c"]);
        });

        count.Should().Be(3, "owner_key keeps the same triple distinct per owner (and shared)");
    }

    [Fact]
    public async Task SameTriple_SameOwner_Deduplicates()
    {
        await _repo.UpsertAsync(NewFact("Alice", "works_at", "Acme", ownerId: "alice"));
        await _repo.UpsertAsync(NewFact("Alice", "works_at", "Acme", ownerId: "alice"));

        var count = await _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                "MATCH (f:Fact {subject:$s, predicate:$p, object:$o}) RETURN count(f) AS c",
                new { s = "Alice", p = "works_at", o = "Acme" });
            var record = await cursor.SingleAsync();
            return global::Neo4j.Driver.ValueExtensions.As<long>(record["c"]);
        });

        count.Should().Be(1, "the same triple from the same owner merges onto one node");
    }

    [Fact]
    public async Task OwnerFilter_NotStarvedByHighVolumeForeignRows()
    {
        // 50 foreign facts all tied at score 1.0 would crowd out the owner's 2 rows if the search
        // limited BEFORE filtering. Over-fetch (topK >> limit) then post-WHERE then LIMIT prevents that.
        for (var i = 0; i < 50; i++)
            await _repo.UpsertAsync(NewFact($"Foreign{i}", "knows", "thing", ownerId: "bob"));

        await _repo.UpsertAsync(NewFact("Alice", "prefers", "dark mode", ownerId: "alice"));
        await _repo.UpsertAsync(NewFact("Alice", "uses", "Neo4j", ownerId: "alice"));

        var results = await _repo.SearchByVectorAsync(
            Embedding, limit: 5, scope: MemoryScope.For("alice"));

        results.Should().HaveCount(2, "both of the owner's facts survive despite 50 higher-volume foreign rows");
        results.Select(r => r.Fact.OwnerId).Should().AllBe("alice");
    }
}
