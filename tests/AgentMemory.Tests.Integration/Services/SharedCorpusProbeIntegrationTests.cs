using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Core.Services;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Neo4j.Services;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

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

    public static TheoryData<string> WritePaths => new()
    {
        "fact", "fact-batch", "fact-fused", "fact-derived",
        "entity", "entity-batch", "entity-fused",
        "preference", "preference-batch", "preference-fused",
    };

    /// <summary>
    /// A book taught a moment ago is recalled at once: a shared row written through any repository path turns a cached
    /// "none" into "some" without waiting out <see cref="Neo4jSharedCorpusProbe.NoneFor"/>. An owned row does not.
    /// </summary>
    [Theory]
    [MemberData(nameof(WritePaths))]
    public async Task A_shared_write_in_this_process_is_seen_at_once(string path)
    {
        var probe = new Neo4jSharedCorpusProbe(_fixture.TransactionRunner, time: new ManualTime(DateTimeOffset.UnixEpoch));
        var kind = path.Split('-')[0] switch { "fact" => SharedKind.Fact, "entity" => SharedKind.Entity, _ => SharedKind.Preference };
        (await probe.HasSharedAsync(kind, CancellationToken.None)).Should().BeFalse("the store starts with no shared rows");

        await WriteAsync(probe, path, owner: "u1");
        (await probe.HasSharedAsync(kind, CancellationToken.None)).Should().BeFalse("an owned row is not shared knowledge");

        await WriteAsync(probe, path, owner: null);
        (await probe.HasSharedAsync(kind, CancellationToken.None)).Should().BeTrue($"{path} wrote a shared row");
    }

    private Task WriteAsync(Neo4jSharedCorpusProbe probe, string path, string? owner)
    {
        var id = $"{path}-{owner ?? "shared"}";
        var facts = new Neo4jFactRepository(_fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance, sharedCorpus: probe);
        var entities = new Neo4jEntityRepository(_fixture.TransactionRunner, NullLogger<Neo4jEntityRepository>.Instance, sharedCorpus: probe);
        var preferences = new Neo4jPreferenceRepository(_fixture.TransactionRunner, NullLogger<Neo4jPreferenceRepository>.Instance, sharedCorpus: probe);
        var fact = new Fact { FactId = id, Subject = "Alice", Predicate = "found", Object = id, Confidence = 0.9, OwnerId = owner, CreatedAtUtc = DateTimeOffset.UtcNow };
        var entity = new Entity { EntityId = id, Name = id, Type = "OBJECT", Confidence = 1, OwnerId = owner, CreatedAtUtc = DateTimeOffset.UtcNow };
        var preference = new Preference { PreferenceId = id, Category = "style", PreferenceText = id, Confidence = 0.9, OwnerId = owner, CreatedAtUtc = DateTimeOffset.UtcNow };
        return path switch
        {
            "fact" => facts.UpsertAsync(fact),
            "fact-batch" => facts.UpsertBatchAsync([fact]),
            "fact-fused" => facts.UpsertFusedBatchAsync([fact]),
            "fact-derived" => DerivedAsync(facts, fact),
            "entity" => entities.UpsertAsync(entity),
            "entity-batch" => entities.UpsertBatchAsync([entity]),
            "entity-fused" => entities.UpsertFusedBatchAsync([entity]),
            "preference" => preferences.UpsertAsync(preference),
            "preference-batch" => preferences.UpsertBatchAsync([preference]),
            _ => preferences.UpsertFusedBatchAsync([preference]),
        };
    }

    /// <summary>The input is written without the probe, so only the derived write can turn the answer to "some".</summary>
    private async Task DerivedAsync(Neo4jFactRepository facts, Fact fact)
    {
        var input = fact with { FactId = fact.FactId + "-input", Object = fact.Object + "-input" };
        await new Neo4jFactRepository(_fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance).UpsertAsync(input);
        await facts.UpsertDerivedAsync(
            fact with { Metadata = MemoryDerivationMetadataExtensions.CreateWithDerivation(DerivationOperators.Count, "counted", [input.FactId]) },
            [input.FactId]);
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
