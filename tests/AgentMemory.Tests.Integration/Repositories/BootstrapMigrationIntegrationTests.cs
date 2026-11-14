using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Tests.Integration.Fixtures;
using Neo4j.Driver;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// An application's one startup step, schema bootstrap, gives the store its migration scripts: the base sequence and
/// every active extension's, identity's unique constraints included. Before 2.3.0 only the repository's CLI and tests
/// ran them, so an installed application ran identity without its constraints.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class BootstrapMigrationIntegrationTests : IAsyncLifetime
{
    private static readonly string[] IdentityConstraints = ["identity_scope_lock", "identity_decision_observation", "identity_binding_id"];
    private readonly Neo4jIntegrationFixture _fixture;

    public BootstrapMigrationIntegrationTests(Neo4jIntegrationFixture fixture) => _fixture = fixture;

    // A store that has never been migrated: no (:Migration) records and none of identity's constraints.
    public async Task InitializeAsync()
    {
        await _fixture.CleanDatabaseAsync();
        foreach (var name in IdentityConstraints)
            await _fixture.TransactionRunner.WriteAsync(async runner =>
            {
                var cursor = await runner.RunAsync($"DROP CONSTRAINT {name} IF EXISTS");
                await cursor.ConsumeAsync();
            });
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task An_application_that_bootstraps_gets_the_identity_schema_once()
    {
        await using var provider = Application(applyMigrations: true);
        var bootstrapper = provider.GetRequiredService<ISchemaBootstrapper>();

        await bootstrapper.BootstrapAsync();
        var applied = await AppliedAsync();
        var stamps = await NamesAsync(StampsQuery);
        await bootstrapper.BootstrapAsync();

        (await ConstraintsAsync()).Should().Contain(IdentityConstraints);
        applied.Should().Contain(["0012_reserved_owner_storage_keys", "ext/identity-evidence/0001_identity_evidence",
            "ext/identity-evidence/0004_identity_relationship_observations"]);
        (await NamesAsync(StampsQuery)).Should().Equal(stamps, "a second start applies nothing again");
    }

    [Fact]
    public async Task An_application_whose_operator_owns_the_schema_bootstraps_without_migrating()
    {
        await using var provider = Application(applyMigrations: false);

        await provider.GetRequiredService<ISchemaBootstrapper>().BootstrapAsync();

        (await ConstraintsAsync()).Should().NotContain(IdentityConstraints);
        (await AppliedAsync()).Should().BeEmpty();
    }

    private ServiceProvider Application(bool applyMigrations)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNeo4jAgentMemory(options =>
        {
            options.Uri = _fixture.ConnectionString;
            options.Username = _fixture.User;
            options.Password = _fixture.Password;
            options.EmbeddingDimensions = Neo4jIntegrationFixture.TestEmbeddingDimensions;
            options.Extensions.Add("identity-evidence");
            options.ApplyMigrationsOnBootstrap = applyMigrations;
        });
        return services.BuildServiceProvider();
    }

    private Task<List<string>> ConstraintsAsync() => NamesAsync("SHOW CONSTRAINTS YIELD name RETURN name ORDER BY name");

    private const string StampsQuery = "MATCH (m:Migration) RETURN m.version + ' ' + m.appliedAtUtc AS name ORDER BY name";

    private Task<List<string>> AppliedAsync() => NamesAsync("MATCH (m:Migration) RETURN m.version AS name ORDER BY name");

    private Task<List<string>> NamesAsync(string query) => _fixture.TransactionRunner.ReadAsync(async runner =>
    {
        var cursor = await runner.RunAsync(query);
        return (await cursor.ToListAsync()).Select(record => global::Neo4j.Driver.ValueExtensions.As<string>(record["name"])).ToList();
    });
}
