using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Neo4j.Services;
using AgentMemory.Tests.Integration.Fixtures;
using NSubstitute;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// End-to-end conflict detection (detect-only): fact contradictions (same subject + predicate + owner,
/// ≥2 distinct objects), grouped per owner so R1 isolation is respected, with a confidence gate.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class ConflictDetectionIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jConflictDetectionService _svc;
    private readonly Neo4jFactRepository _facts;

    public ConflictDetectionIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(new DateTimeOffset(2026, 6, 6, 0, 0, 0, TimeSpan.Zero));
        _svc = new Neo4jConflictDetectionService(
            fixture.TransactionRunner, clock, NullLogger<Neo4jConflictDetectionService>.Instance);
        _facts = new Neo4jFactRepository(fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private Task SeedFactAsync(string subject, string predicate, string obj, string? owner, double confidence) =>
        _facts.UpsertAsync(new Fact
        {
            FactId = $"fact-{Guid.NewGuid():N}",
            Subject = subject,
            Predicate = predicate,
            Object = obj,
            OwnerId = owner,
            Confidence = confidence,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });

    [Fact]
    public async Task DetectConflictsAsync_FindsContradictions_GroupedPerOwner()
    {
        // Shared scope: Alice/works_at has two objects → conflict.
        await SeedFactAsync("Alice", "works_at", "Acme", null, 0.9);
        await SeedFactAsync("Alice", "works_at", "Globex", null, 0.9);
        // Shared, single object → no conflict.
        await SeedFactAsync("Bob", "lives_in", "Seattle", null, 0.9);
        // Alice's OWN scope: same subject/predicate but a single object → must NOT merge with shared.
        await SeedFactAsync("Alice", "works_at", "Initech", "alice", 0.9);
        // Alice's own scope: a genuine owner-private conflict.
        await SeedFactAsync("Carol", "age", "30", "alice", 0.9);
        await SeedFactAsync("Carol", "age", "31", "alice", 0.9);

        var report = await _svc.DetectConflictsAsync();

        report.FactConflictCount.Should().Be(2);

        var shared = report.FactConflicts.Single(c => c.OwnerId is null);
        shared.Subject.Should().Be("Alice");
        shared.Predicate.Should().Be("works_at");
        shared.Values.Select(v => v.Object).Should().BeEquivalentTo(new[] { "Acme", "Globex" });

        var alice = report.FactConflicts.Single(c => c.OwnerId == "alice");
        alice.Subject.Should().Be("Carol");
        alice.Values.Should().HaveCount(2);
    }

    [Fact]
    public async Task DetectConflictsAsync_MinConfidence_GatesLowConfidenceFacts()
    {
        await SeedFactAsync("Alice", "works_at", "Acme", null, 0.9);
        await SeedFactAsync("Alice", "works_at", "Globex", null, 0.8); // below the gate

        var report = await _svc.DetectConflictsAsync(new ConflictDetectionOptions { MinConfidence = 0.85 });

        report.FactConflictCount.Should().Be(0, "the 0.8 fact is gated out, leaving a single object");
    }

    [Fact]
    public async Task DetectConflictsAsync_NoContradictions_ReturnsEmpty()
    {
        await SeedFactAsync("Bob", "lives_in", "Seattle", null, 0.9);

        (await _svc.DetectConflictsAsync()).FactConflictCount.Should().Be(0);
    }
}
