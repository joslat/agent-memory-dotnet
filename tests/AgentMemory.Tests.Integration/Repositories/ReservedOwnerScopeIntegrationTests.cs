using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Core.Services;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Neo4j.Services;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.Driver;

namespace AgentMemory.Tests.Integration.Repositories;

[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class ReservedOwnerScopeIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jFactRepository _facts;
    private readonly Neo4jEntityRepository _entities;
    public ReservedOwnerScopeIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _facts = new(fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance);
        _entities = new(fixture.TransactionRunner, NullLogger<Neo4jEntityRepository>.Instance);
    }
    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    private static Fact Item(string? owner, string obj = "Córdoba", string subject = "Pepe") => new()
    { FactId = Guid.NewGuid().ToString("N"), OwnerId = owner, Subject = subject, Predicate = "lives in", Object = obj,
        Confidence = 1, SourceMessageIds = ["message"], CreatedAtUtc = DateTimeOffset.UnixEpoch };
    private Task<Entity> Entity(string? owner) => _entities.UpsertAsync(new()
    { EntityId = Guid.NewGuid().ToString("N"), OwnerId = owner, Name = "Pepe", Type = "PERSON", Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch });
    private async Task Query(string text, object? parameters = null) => await _fixture.TransactionRunner.WriteAsync(async runner => { await runner.RunAsync(text, parameters); });

    [Theory]
    [InlineData("batch")] [InlineData("fused")]
    public async Task Batch_writers_do_not_collapse_shared_and_reserved_private_scopes(string path)
    {
        var items = new[] { Item(null), Item("*"), Item("**"), Item("pablito") };
        var written = path == "batch" ? await _facts.UpsertBatchAsync(items) : await _facts.UpsertFusedBatchAsync(items);
        written.Should().HaveCount(4);
        written.Select(f => f.FactId).Should().OnlyHaveUniqueItems();
        written.Select(f => f.OwnerId).Should().BeEquivalentTo(new string?[] { null, "*", "**", "pablito" });
        foreach (var owner in new[] { "*", "**", "pablito" })
            (await _facts.FindByTripleAsync("Pepe", "lives in", "Córdoba", MemoryScope.For(owner, false)))!.OwnerId.Should().Be(owner);
        (await _facts.FindByTripleAsync("Pepe", "lives in", "Córdoba", SharedScopes.SharedOnly))!.OwnerId.Should().BeNull();
    }

    [Fact]
    public async Task Shared_entity_reads_exclude_historical_private_rows_with_the_old_shared_key()
    {
        var privateEntity = await Entity("*"); var shared = await Entity(null);
        await Query("MATCH (e:Entity {id:$id}) SET e.owner_key = '*'", new { id = privateEntity.EntityId });
        var results = await _entities.GetByTypeAsync("PERSON", SharedScopes.SharedOnly);
        results.Should().ContainSingle().Which.EntityId.Should().Be(shared.EntityId);
    }

    [Fact]
    public async Task Shared_fact_reads_exclude_historical_private_rows_with_the_old_shared_key()
    {
        var fact = await _facts.UpsertAsync(Item("*"));
        await Query("MATCH (f:Fact {id:$id}) SET f.owner_key = '*'", new { id = fact.FactId });
        (await _facts.FindByTripleAsync(fact.Subject, fact.Predicate, fact.Object, SharedScopes.SharedOnly)).Should().BeNull();
    }

    [Theory]
    [InlineData("single")] [InlineData("batch")] [InlineData("fused")]
    public async Task Pre_migration_foreign_key_collisions_fail_before_any_scope_is_mutated(string path)
    {
        var privateFact = await _facts.UpsertAsync(Item("*"));
        await Query("MATCH (f:Fact {id:$id}) SET f.owner_key = '*'", new { id = privateFact.FactId });
        var incoming = Item(null);
        var action = async () =>
        {
            if (path == "single") await _facts.UpsertAsync(incoming);
            else if (path == "batch") await _facts.UpsertBatchAsync([Item("pablito", subject: "Another"), incoming]);
            else await _facts.UpsertFusedBatchAsync([Item("pablito", subject: "Another"), incoming]);
        };
        await action.Should().ThrowAsync<InvalidOperationException>();
        (await _facts.GetByIdAsync(privateFact.FactId)).Should().BeEquivalentTo(privateFact);
        var count = await _fixture.TransactionRunner.ReadAsync(async runner =>
        { var cursor = await runner.RunAsync("MATCH (f:Fact) RETURN count(f) AS count"); return global::Neo4j.Driver.ValueExtensions.As<long>((await cursor.SingleAsync())["count"]); });
        count.Should().Be(1);
    }

    [Fact]
    public async Task Explicit_migration_is_idempotent_and_preserves_authoritative_owner_and_fact_identity()
    {
        var fact = await _facts.UpsertAsync(Item("*"));
        await Query("MATCH (f:Fact {id:$id}) SET f.owner_key = '*'", new { id = fact.FactId });
        var script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Schema/Migrations/0012_reserved_owner_storage_keys.cypher"));
        var statements = string.Join('\n', script.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)))
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var pass = 0; pass < 2; pass++) foreach (var statement in statements) await Query(statement);
        var shared = await _facts.UpsertAsync(Item(null)); shared.FactId.Should().NotBe(fact.FactId);
        (await _facts.GetByIdAsync(fact.FactId)).Should().BeEquivalentTo(fact);
        (await _facts.FindByTripleAsync(fact.Subject, fact.Predicate, fact.Object, MemoryScope.For("*", false)))!.FactId.Should().Be(fact.FactId);
    }

    [Fact]
    public async Task Summaries_of_one_shared_entity_keep_private_star_and_shared_content_separate()
    {
        var entity = await Entity(null); var repository = new Neo4jEntitySummaryRepository(_fixture.TransactionRunner, NullLogger<Neo4jEntitySummaryRepository>.Instance);
        foreach (var owner in new string?[] { null, "*", "**" })
            await repository.UpsertAsync(new EntitySummary { SummaryId = Guid.NewGuid().ToString("N"), EntityId = entity.EntityId,
                OwnerId = owner, Content = owner ?? "shared", SourceFactIds = [], SourceFingerprint = "fixture", GeneratedAtUtc = DateTimeOffset.UnixEpoch });
        foreach (var owner in new[] { "*", "**" })
            (await repository.GetByEntityAsync(entity.EntityId, MemoryScope.For(owner, false)))!.Content.Should().Be(owner);
        (await repository.GetByEntityAsync(entity.EntityId, SharedScopes.SharedOnly))!.Content.Should().Be("shared");
    }

    [Fact]
    public async Task Conflict_groups_do_not_conflate_shared_and_private_star_facts()
    {
        await _facts.UpsertAsync(Item(null)); await _facts.UpsertAsync(Item("*", "Bariloche"));
        var service = new Neo4jConflictDetectionService(_fixture.TransactionRunner, TimeProvider.System, NullLogger<Neo4jConflictDetectionService>.Instance);
        (await service.DetectConflictsAsync()).FactConflicts.Should().BeEmpty();
        await _facts.UpsertAsync(Item("*", "Salta"));
        var report = await service.DetectConflictsAsync(); report.FactConflicts.Should().ContainSingle().Which.OwnerId.Should().Be("*");
    }

    [Fact]
    public async Task Supersession_cannot_cross_shared_and_reserved_private_scope_even_for_a_global_caller()
    {
        var loser = await _facts.UpsertAsync(Item(null)); var winner = await _facts.UpsertAsync(Item("*", "Bariloche"));
        (await _facts.SupersedeAsync(loser.FactId, winner.FactId)).Should().BeFalse();
        (await _facts.GetByIdAsync(loser.FactId))!.InvalidatedAtUtc.Should().BeNull();
    }
}
