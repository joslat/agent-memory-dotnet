using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Neo4j.Services;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// The profile block never serves a fact that expired or was pruned (review of the default-on flip,
/// 2026-09-26): it was rebuilt only on writes, so a fact that passed its valid_until, or a prune (a hard
/// one is storage reclamation, including erasure), left the text in every later prompt.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class WorkingMemoryExpiryAndPruneIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jFactRepository _facts;
    private readonly MovableClock _clock = new() { UtcNow = new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero) };
    private readonly MemoryOptions _options = new();

    private sealed class MovableClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; }
    }

    private sealed class Ids : IIdGenerator
    {
        public string GenerateId() => Guid.NewGuid().ToString("N");
    }

    public WorkingMemoryExpiryAndPruneIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _facts = new Neo4jFactRepository(fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance);
        _options.WorkingMemory.Enabled = true;
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private Neo4jWorkingMemoryService WorkingMemory() => new(
        _fixture.TransactionRunner, _clock, new Ids(), Options.Create(_options), NullLogger<Neo4jWorkingMemoryService>.Instance);

    private Fact NewFact(string @object, DateTimeOffset? validUntil = null) => new()
    {
        FactId = Guid.NewGuid().ToString("N"),
        Subject = "user",
        Predicate = "works_at",
        Object = @object,
        Confidence = 0.95,
        CreatedAtUtc = _clock.UtcNow.AddDays(-1),
        ValidUntil = validUntil,
        OwnerId = "alice",
    };

    [Fact]
    public async Task A_fact_that_expired_is_gone_from_the_block_on_the_next_read()
    {
        var workingMemory = WorkingMemory();
        await _facts.UpsertAsync(NewFact("Acme", validUntil: _clock.UtcNow.AddHours(1)));
        await _facts.UpsertAsync(NewFact("Globex"));
        await workingMemory.RebuildAsync("alice");
        (await workingMemory.GetAsync("alice"))!.Text.Should().Contain("Acme");

        // No write happens; only time passes the fact's valid_until.
        _clock.UtcNow = _clock.UtcNow.AddHours(2);

        var block = await workingMemory.GetAsync("alice");
        block!.Text.Should().NotContain("Acme").And.Contain("Globex");
    }

    [Fact]
    public async Task A_prune_clears_the_block_of_the_pruned_owner()
    {
        var workingMemory = WorkingMemory();
        await _facts.UpsertAsync(NewFact("Acme") with { CreatedAtUtc = _clock.UtcNow.AddYears(-5) });
        await workingMemory.RebuildAsync("alice");
        (await workingMemory.GetAsync("alice")).Should().NotBeNull();

        var decay = new Neo4jMemoryDecayService(
            _fixture.TransactionRunner, _clock,
            Options.Create(new MemoryDecayOptions { DecayHalfLifeDays = 1, MinRetentionScore = 0.5, NonDestructive = false }),
            NullLogger<Neo4jMemoryDecayService>.Instance,
            Options.Create(_options));
        (await decay.PruneExpiredMemoriesAsync(MemoryScope.For("alice", includeShared: false))).Should().BeGreaterThan(0);

        (await workingMemory.GetAsync("alice")).Should().BeNull("the pruned fact's text must not survive in the block");
    }
}
