using System.Diagnostics;
using AgentMemory.Abstractions.Diagnostics;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Queries;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// DRT-2: start-up on an existing store lists the schema objects once and sends only the missing statements; the
/// migrations' applied versions are read once. The result is the same as the previous path, by fewer transactions.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class BootstrapOnlyMissingIntegrationTests(Neo4jIntegrationFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.CleanDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private ServiceProvider Application(bool onlyMissing)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNeo4jAgentMemory(options =>
        {
            options.Uri = fixture.ConnectionString;
            options.Username = fixture.User;
            options.Password = fixture.Password;
            options.EmbeddingDimensions = Neo4jIntegrationFixture.TestEmbeddingDimensions;
            options.Extensions.Add("identity-evidence");
            options.BootstrapOnlyMissingSchema = onlyMissing;
        });
        return services.BuildServiceProvider();
    }

    /// <summary>The write transactions a bootstrap opens, counted from the library's own spans.</summary>
    private static async Task<int> WritesDuringAsync(Func<Task> work)
    {
        var writes = 0;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgentMemoryDiagnostics.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == "memory.db.tx" && activity.GetTagItem("db.mode") as string == "write")
                    Interlocked.Increment(ref writes);
            },
        };
        ActivitySource.AddActivityListener(listener);
        await work();
        return writes;
    }

    [Fact]
    public async Task On_a_complete_store_almost_nothing_is_written_and_the_previous_path_writes_every_statement()
    {
        await using (var first = Application(onlyMissing: true))
            await first.GetRequiredService<ISchemaBootstrapper>().BootstrapAsync();

        await using var lean = Application(onlyMissing: true);
        var leanWrites = await WritesDuringAsync(() => lean.GetRequiredService<ISchemaBootstrapper>().BootstrapAsync());
        await using var previous = Application(onlyMissing: false);
        var previousWrites = await WritesDuringAsync(() => previous.GetRequiredService<ISchemaBootstrapper>().BootstrapAsync());

        var statements = SchemaQueries.Constraints.Length + SchemaQueries.FulltextIndexes.Length + SchemaQueries.PropertyIndexes.Length;
        // Left: the migration ledger's own constraint, and the owner-key and period-key backfills, which write only what
        // is missing. Every one of the 52 schema statements is skipped and the applied migrations are read once.
        leanWrites.Should().BeLessThanOrEqualTo(3);
        previousWrites.Should().BeGreaterThanOrEqualTo(statements, "the previous path sends every statement");
    }

    [Fact]
    public async Task A_missing_object_is_created_and_the_schema_ends_complete()
    {
        await using (var first = Application(onlyMissing: true))
            await first.GetRequiredService<ISchemaBootstrapper>().BootstrapAsync();
        var dropped = SchemaConformance.ParseObjectName(SchemaQueries.Constraints[0]);
        await fixture.TransactionRunner.WriteAsync(async runner => { await (await runner.RunAsync($"DROP CONSTRAINT {dropped} IF EXISTS")).ConsumeAsync(); });

        await using var again = Application(onlyMissing: true);
        await again.GetRequiredService<ISchemaBootstrapper>().BootstrapAsync();
        var inspection = await again.GetRequiredService<ISchemaInspector>().InspectAsync();

        inspection.Missing.Should().BeEmpty();
        inspection.Complete.Should().BeTrue();
    }
}
