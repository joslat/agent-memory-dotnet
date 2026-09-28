using AgentMemory.Core.Services;
using AgentMemory.Neo4j.Services;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;

namespace AgentMemory.Tests.Integration.Services;

/// <summary>37.1b, on a real store: whether shared memory of a kind exists, and the answer is cached.</summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class SharedCorpusProbeIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;

    public SharedCorpusProbeIntegrationTests(Neo4jIntegrationFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private Task RunAsync(string cypher) =>
        _fixture.TransactionRunner.WriteAsync(async runner => await runner.RunAsync(cypher));

    [Fact]
    public async Task Each_kind_is_shared_only_when_an_owner_less_row_of_it_exists()
    {
        await RunAsync("CREATE (:Fact {id: 'own', owner_id: 'u1', owner_key: 'u1'}), (:Entity {id: 'e', owner_key: '*'}), (:Preference {id: 'p', owner_id: 'u1'})");
        var probe = new Neo4jSharedCorpusProbe(_fixture.TransactionRunner);

        (await probe.HasSharedAsync(SharedKind.Fact, CancellationToken.None)).Should().BeFalse();
        (await probe.HasSharedAsync(SharedKind.Entity, CancellationToken.None)).Should().BeTrue();
        (await probe.HasSharedAsync(SharedKind.Preference, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task A_none_is_cached_briefly_and_seen_again_after_its_window()
    {
        var time = new ManualTime(DateTimeOffset.UnixEpoch);
        var probe = new Neo4jSharedCorpusProbe(_fixture.TransactionRunner, time: time);
        (await probe.HasSharedAsync(SharedKind.Fact, CancellationToken.None)).Should().BeFalse();

        await RunAsync("CREATE (:Fact {id: 'shared', owner_key: '*'})");
        (await probe.HasSharedAsync(SharedKind.Fact, CancellationToken.None)).Should().BeFalse("cached");

        time.Now += Neo4jSharedCorpusProbe.NoneFor + TimeSpan.FromSeconds(1);
        (await probe.HasSharedAsync(SharedKind.Fact, CancellationToken.None)).Should().BeTrue();
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
