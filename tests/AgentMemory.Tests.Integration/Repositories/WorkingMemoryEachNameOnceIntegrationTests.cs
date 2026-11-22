using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Services;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// IBP-1: the profile block's key entities show each line once. A thing stored as several entities (a split, as the
/// identity battery finds for a dog, a café or a town) said "Luna (OBJECT)" three times and took three of the block's
/// places; now it says it once and the next entities take the places.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class WorkingMemoryEachNameOnceIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;

    public WorkingMemoryEachNameOnceIntegrationTests(Neo4jIntegrationFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class Ids : IIdGenerator
    {
        public string GenerateId() => Guid.NewGuid().ToString("N");
    }

    private Neo4jWorkingMemoryService Service(int maxTopEntities)
    {
        var options = new MemoryOptions();
        options.WorkingMemory.Enabled = true;
        options.WorkingMemory.MaxTopEntities = maxTopEntities;
        return new Neo4jWorkingMemoryService(_fixture.TransactionRunner, TimeProvider.System, new Ids(),
            Options.Create(options), NullLogger<Neo4jWorkingMemoryService>.Instance);
    }

    private Task Store(params (string Name, string Type)[] entities) => _fixture.TransactionRunner.WriteAsync(async runner =>
    {
        foreach (var (name, type) in entities)
            await runner.RunAsync(
                "CREATE (:Entity {id: $id, owner_id: 'alice', owner_key: 'alice', name: $name, type: $type, created_at: datetime()})",
                new { id = Guid.NewGuid().ToString("N"), name, type });
    });

    private static string[] KeyEntities(string block) =>
        block.Split('\n').SkipWhile(line => line != "Key entities:").Skip(1).TakeWhile(line => !line.EndsWith(':') && !line.StartsWith("Lately", StringComparison.Ordinal)).ToArray();

    [Fact]
    public async Task A_name_repeated_by_a_split_is_shown_once_and_the_next_entities_take_its_places()
    {
        await Store(("Luna", "OBJECT"), ("Luna", "OBJECT"), ("luna", "OBJECT"), ("Max", "PERSON"), ("Nora", "PERSON"));
        var service = Service(maxTopEntities: 3);

        await service.RebuildAsync("alice");
        var block = await service.GetAsync("alice");

        KeyEntities(block!.Text).Should().Equal("Luna (OBJECT)", "Max (PERSON)", "Nora (PERSON)");
    }

    [Fact]
    public async Task Namesakes_of_different_types_keep_their_own_lines()
    {
        await Store(("Luna", "OBJECT"), ("Luna", "PERSON"));
        var service = Service(maxTopEntities: 6);

        await service.RebuildAsync("alice");
        var block = await service.GetAsync("alice");

        KeyEntities(block!.Text).Should().Equal("Luna (OBJECT)", "Luna (PERSON)");
    }
}
