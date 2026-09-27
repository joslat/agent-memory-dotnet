using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Infrastructure;
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

    /// <summary>A connection that can read but not write: a reader role, read-only routing, a lock timeout.</summary>
    private sealed class ReadOnlyRunner(INeo4jTransactionRunner inner) : INeo4jTransactionRunner
    {
        public Task<T> ReadAsync<T>(Func<global::Neo4j.Driver.IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(work, cancellationToken);
        public Task ReadAsync(Func<global::Neo4j.Driver.IAsyncQueryRunner, Task> work, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(work, cancellationToken);
        public Task<T> WriteAsync<T>(Func<global::Neo4j.Driver.IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Writing is not allowed on this connection.");
        public Task WriteAsync(Func<global::Neo4j.Driver.IAsyncQueryRunner, Task> work, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Writing is not allowed on this connection.");
    }

    private Neo4jWorkingMemoryService WorkingMemory(INeo4jTransactionRunner? runner = null) => new(
        runner ?? _fixture.TransactionRunner, _clock, new Ids(), Options.Create(_options), NullLogger<Neo4jWorkingMemoryService>.Instance);

    private Neo4jMemoryDecayService HardPrune() => new(
        _fixture.TransactionRunner, _clock,
        Options.Create(new MemoryDecayOptions { DecayHalfLifeDays = 1, MinRetentionScore = 0.5, NonDestructive = false }),
        NullLogger<Neo4jMemoryDecayService>.Instance,
        Options.Create(_options));

    private Fact NewFact(string @object, DateTimeOffset? validUntil = null, string owner = "alice") => new()
    {
        FactId = Guid.NewGuid().ToString("N"),
        Subject = "user",
        Predicate = "works_at",
        Object = @object,
        Confidence = 0.95,
        CreatedAtUtc = _clock.UtcNow.AddDays(-1),
        ValidUntil = validUntil,
        OwnerId = owner,
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

        (await HardPrune().PruneExpiredMemoriesAsync(MemoryScope.For("alice", includeShared: false))).Should().BeGreaterThan(0);

        (await workingMemory.GetAsync("alice")).Should().BeNull("the pruned fact's text must not survive in the block");
    }

    [Fact]
    public async Task After_a_prune_the_next_read_serves_what_remains()
    {
        // Review round 2: the prune cleared the block and nothing rebuilt it until the owner wrote again,
        // so an owner who only reads ("what do you know about me?") got no profile at all.
        var workingMemory = WorkingMemory();
        await _facts.UpsertAsync(NewFact("Acme") with { CreatedAtUtc = _clock.UtcNow.AddYears(-5) });
        await _facts.UpsertAsync(NewFact("Globex") with { CreatedAtUtc = _clock.UtcNow });
        await workingMemory.RebuildAsync("alice");

        await HardPrune().PruneExpiredMemoriesAsync(MemoryScope.For("alice", includeShared: false));

        var block = await workingMemory.GetAsync("alice");
        block!.Text.Should().Contain("Globex").And.NotContain("Acme");
    }

    [Fact]
    public async Task A_prune_across_owners_leaves_every_other_owner_with_a_profile()
    {
        // The nightly unscoped prune clears every block when any owner lost something.
        var workingMemory = WorkingMemory();
        await _facts.UpsertAsync(NewFact("Acme") with { CreatedAtUtc = _clock.UtcNow.AddYears(-5) });
        await _facts.UpsertAsync(NewFact("Initech", owner: "bob") with { CreatedAtUtc = _clock.UtcNow });
        await workingMemory.RebuildAsync("alice");
        await workingMemory.RebuildAsync("bob");

        (await HardPrune().PruneExpiredMemoriesAsync()).Should().BeGreaterThan(0);

        (await workingMemory.GetAsync("bob"))!.Text.Should().Contain("Initech");
        (await workingMemory.GetAsync("alice")).Should().BeNull();
    }

    [Fact]
    public async Task A_block_that_is_empty_until_a_future_fact_starts_is_rebuilt_when_it_starts()
    {
        // The owner's only fact starts on Monday: the rebuild stores no text and Monday as the boundary.
        // The boundary was dropped with the empty text, so the block stayed empty after Monday.
        var workingMemory = WorkingMemory();
        await _facts.UpsertAsync(NewFact("Acme") with { ValidFrom = _clock.UtcNow.AddDays(2) });
        await workingMemory.RebuildAsync("alice");
        (await workingMemory.GetAsync("alice")).Should().BeNull();

        _clock.UtcNow = _clock.UtcNow.AddDays(3);

        (await workingMemory.GetAsync("alice"))!.Text.Should().Contain("Acme");
    }

    [Fact]
    public async Task A_rebuild_on_read_that_cannot_write_does_not_fail_recall()
    {
        // Review round 2: GetAsync became a write on the recall path, outside the rebuilder's never-throw.
        await _facts.UpsertAsync(NewFact("Acme", validUntil: _clock.UtcNow.AddHours(1)));
        await WorkingMemory().RebuildAsync("alice");
        _clock.UtcNow = _clock.UtcNow.AddHours(2);

        var read = async () => await WorkingMemory(new ReadOnlyRunner(_fixture.TransactionRunner)).GetAsync("alice");

        (await read.Should().NotThrowAsync()).Subject.Should().BeNull("the stored block asserts an expired fact");
    }

    [Fact]
    public async Task A_rebuild_that_failed_is_not_retried_on_every_recall()
    {
        // Review round 3: with the block due and the write failing, every recall ran the rebuild's reads,
        // a failing write and a warning, for as long as the cause lasted.
        await _facts.UpsertAsync(NewFact("Acme", validUntil: _clock.UtcNow.AddHours(1)));
        await WorkingMemory().RebuildAsync("alice");
        _clock.UtcNow = _clock.UtcNow.AddHours(2);
        var runner = new CountingReadOnlyRunner(_fixture.TransactionRunner);
        var backoff = new WorkingMemoryRebuildBackoff();
        Neo4jWorkingMemoryService Service() => new(runner, _clock, new Ids(), Options.Create(_options),
            NullLogger<Neo4jWorkingMemoryService>.Instance, backoff);

        await Service().GetAsync("alice");
        await Service().GetAsync("alice");
        runner.WriteAttempts.Should().Be(1, "the second recall waits out the backoff");

        _clock.UtcNow += WorkingMemoryRebuildBackoff.Delay;
        await Service().GetAsync("alice");
        runner.WriteAttempts.Should().Be(2, "after the backoff the rebuild is tried again");
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(0, false)]
    public async Task A_new_fact_said_once_reaches_a_block_full_of_facts_said_twice(int recentSlots, bool expected)
    {
        // D19 (review round 2): slots went by mention count first, so once all 12 held facts mentioned twice
        // or more, a new job or city (mentioned once) never reached the block.
        _options.WorkingMemory.RecentStableFactSlots = recentSlots;
        for (int i = 0; i < 12; i++)
        {
            var fact = NewFact($"Old{i}") with { Predicate = $"likes_{i}", CreatedAtUtc = _clock.UtcNow.AddDays(-30 + i) };
            await _facts.UpsertAsync(fact);
            await _facts.UpsertAsync(fact with { FactId = Guid.NewGuid().ToString("N") });   // a second mention
        }
        await _facts.UpsertAsync(NewFact("Initech") with { Predicate = "works_at", CreatedAtUtc = _clock.UtcNow });

        await WorkingMemory().RebuildAsync("alice");

        var text = (await WorkingMemory().GetAsync("alice"))!.Text;
        text.Contains("Initech").Should().Be(expected);
        text.Split('\n').Count(l => l.StartsWith("user ", StringComparison.Ordinal)).Should().Be(12, "the block keeps its size");
    }

    private sealed class CountingReadOnlyRunner(INeo4jTransactionRunner inner) : INeo4jTransactionRunner
    {
        public int WriteAttempts;
        public Task<T> ReadAsync<T>(Func<global::Neo4j.Driver.IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(work, cancellationToken);
        public Task ReadAsync(Func<global::Neo4j.Driver.IAsyncQueryRunner, Task> work, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(work, cancellationToken);
        public Task<T> WriteAsync<T>(Func<global::Neo4j.Driver.IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref WriteAttempts);
            throw new InvalidOperationException("Writing is not allowed on this connection.");
        }
        public Task WriteAsync(Func<global::Neo4j.Driver.IAsyncQueryRunner, Task> work, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref WriteAttempts);
            throw new InvalidOperationException("Writing is not allowed on this connection.");
        }
    }
}
