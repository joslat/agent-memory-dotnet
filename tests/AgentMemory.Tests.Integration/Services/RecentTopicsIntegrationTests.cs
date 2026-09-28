using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Services;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentMemory.Tests.Integration.Services;

/// <summary>
/// 37.5, on a real store: the profile block's "Lately" line counts what the person said in the window (facts'
/// EXTRACTED_FROM provenance with the message's time), names only the owner's entities, never the person.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class RecentTopicsIntegrationTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    private const string Owner = "owner-lately";
    private readonly Neo4jIntegrationFixture _fixture;

    public RecentTopicsIntegrationTests(Neo4jIntegrationFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class Ids : IIdGenerator
    {
        public string GenerateId() => Guid.NewGuid().ToString("N");
    }

    /// <summary>A fact about <paramref name="topic"/> said in a message <paramref name="daysAgo"/> days ago.</summary>
    private Task SaidAsync(string subject, string predicate, string @object, double daysAgo, string owner = Owner, string role = "user") =>
        _fixture.TransactionRunner.WriteAsync(async runner => await runner.RunAsync(@"
            CREATE (m:Message {id: randomUUID(), role: $role, content: 'x', timestamp: datetime($at)})
            MERGE (f:Fact {subject: $subject, predicate: $predicate, object: $object, owner_id: $owner})
              ON CREATE SET f.id = randomUUID(), f.created_at = datetime($at),
                            f.subject_key = toLower($subject), f.predicate_key = toLower(replace($predicate, '_', ' '))
            CREATE (f)-[:EXTRACTED_FROM]->(m)",
            new { subject, predicate, @object, owner, role, at = Now.AddDays(-daysAgo).ToString("O") }));

    private Task EntityAsync(string name, string owner = Owner) =>
        _fixture.TransactionRunner.WriteAsync(async runner => await runner.RunAsync(
            "CREATE (:Entity {id: randomUUID(), name: $name, owner_id: $owner, type: 'CONCEPT'})", new { name, owner }));

    private async Task<string> BlockAsync(int days = 7)
    {
        var options = new MemoryOptions();
        options.WorkingMemory.MinFactMentionCount = 1;
        options.WorkingMemory.RecentTopicsDays = days;
        var service = new Neo4jWorkingMemoryService(
            _fixture.TransactionRunner, new FixedClock(), new Ids(), Options.Create(options),
            NullLogger<Neo4jWorkingMemoryService>.Instance);
        return await service.ComposeAsync(Owner, Now, CancellationToken.None);
    }

    [Fact]
    public async Task What_the_person_talked_about_most_this_week_is_lately_and_they_are_not()
    {
        foreach (var name in new[] { "Dana", "marathon", "Ana", "Porto", "Lisbon" }) await EntityAsync(name);
        await SaidAsync("user", "is_named", "Dana", daysAgo: 6);                        // the name found by its key
        for (var day = 1; day <= 5; day++) await SaidAsync("Dana", $"trained for the marathon on day {day}", "marathon", daysAgo: day);
        await SaidAsync("Ana", "is the sister of", "Dana", daysAgo: 2);
        await SaidAsync("Ana", "loves", "pottery", daysAgo: 3);
        await SaidAsync("Dana", "visited", "Porto", daysAgo: 4);                        // once: not lately
        await SaidAsync("Dana", "ate", "pasta", daysAgo: 1);                             // twice, but not an entity
        await SaidAsync("Dana", "cooked", "pasta", daysAgo: 2);
        await SaidAsync("Mem", "suggested", "Porto", daysAgo: 1, role: "assistant");     // the agent's words never count
        await SaidAsync("Mem", "recommended", "Porto", daysAgo: 2, role: "assistant");
        for (var day = 20; day <= 24; day++) await SaidAsync("Dana", $"lived in Lisbon {day}", "Lisbon", daysAgo: day); // outside the week

        var block = await BlockAsync();

        block.Should().EndWith("Lately (7 days): marathon (5 mentions), Ana (2 mentions)");
    }

    [Fact]
    public async Task Another_owner_s_talk_never_counts()
    {
        await EntityAsync("marathon");
        await EntityAsync("marathon", owner: "someone-else");
        for (var day = 1; day <= 3; day++) await SaidAsync("Kim", $"ran {day}", "marathon", daysAgo: day, owner: "someone-else");

        (await BlockAsync()).Should().NotContain("Lately");
    }

    [Fact]
    public async Task Off_by_default_there_is_no_lately_line()
    {
        await EntityAsync("marathon");
        for (var day = 1; day <= 3; day++) await SaidAsync("Dana", $"ran {day}", "marathon", daysAgo: day);

        (await BlockAsync(days: 0)).Should().NotContain("Lately");
    }
}
