using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Neo4j.Infrastructure;
using Neo4j.AgentMemory.Tests.Integration.Fixtures;
using Neo4j.Driver;

namespace Neo4j.AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// Integration tests verifying that SchemaBootstrapper correctly creates constraints and indexes.
/// These tests run against the shared container but operate on SHOW CONSTRAINTS / SHOW INDEXES,
/// which are not affected by CleanDatabaseAsync (schema is structural, not graph data).
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class SchemaBootstrapperIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;

    public SchemaBootstrapperIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    // No data to clean between tests — schema was already bootstrapped in fixture.InitializeAsync
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData("conversation_id")]
    [InlineData("message_id")]
    [InlineData("entity_id")]
    [InlineData("fact_id")]
    [InlineData("preference_id")]
    [InlineData("reasoning_trace_id")]
    [InlineData("reasoning_step_id")]
    [InlineData("tool_call_id")]
    public async Task BootstrapAsync_CreatesUniqueConstraints(string constraintName)
    {
        var exists = await _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                $"SHOW CONSTRAINTS YIELD name WHERE name = '{constraintName}' RETURN count(*) AS c");
            var records = await cursor.ToListAsync();
            return records.Count > 0 && global::Neo4j.Driver.ValueExtensions.As<long>(records[0]["c"]) > 0;
        });

        exists.Should().BeTrue(because: $"constraint '{constraintName}' should have been created by SchemaBootstrapper");
    }

    [Theory]
    [InlineData("message_content")]
    [InlineData("entity_name")]
    [InlineData("fact_content")]
    public async Task BootstrapAsync_CreatesFulltextIndexes(string indexName)
    {
        var exists = await _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                $"SHOW INDEXES YIELD name, type WHERE name = '{indexName}' AND type = 'FULLTEXT' RETURN count(*) AS c");
            var records = await cursor.ToListAsync();
            return records.Count > 0 && global::Neo4j.Driver.ValueExtensions.As<long>(records[0]["c"]) > 0;
        });

        exists.Should().BeTrue(because: $"fulltext index '{indexName}' should have been created by SchemaBootstrapper");
    }

    [Theory]
    [InlineData("message_embedding_idx")]
    [InlineData("entity_embedding_idx")]
    [InlineData("preference_embedding_idx")]
    [InlineData("fact_embedding_idx")]
    [InlineData("task_embedding_idx")]
    public async Task BootstrapAsync_CreatesVectorIndexes(string indexName)
    {
        var exists = await _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                $"SHOW INDEXES YIELD name, type WHERE name = '{indexName}' AND type = 'VECTOR' RETURN count(*) AS c");
            var records = await cursor.ToListAsync();
            return records.Count > 0 && global::Neo4j.Driver.ValueExtensions.As<long>(records[0]["c"]) > 0;
        });

        exists.Should().BeTrue(because: $"vector index '{indexName}' should have been created by SchemaBootstrapper");
    }

    [Fact]
    public async Task BootstrapAsync_IsIdempotent_NoErrorsOnSecondRun()
    {
        var options = Options.Create(new Neo4jOptions
        {
            EmbeddingDimensions = Neo4jIntegrationFixture.TestEmbeddingDimensions
        });

        var bootstrapper = new SchemaBootstrapper(
            _fixture.TransactionRunner,
            options,
            NullLogger<SchemaBootstrapper>.Instance);

        // Running again should not throw — IF NOT EXISTS guards each statement
        var act = async () => await bootstrapper.BootstrapAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task BootstrapAsync_AllVectorIndexesAreOnline()
    {
        var offlineCount = await _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                "SHOW INDEXES YIELD name, type, state WHERE type = 'VECTOR' AND state <> 'ONLINE' RETURN count(*) AS c");
            var records = await cursor.ToListAsync();
            return records.Count > 0 ? global::Neo4j.Driver.ValueExtensions.As<long>(records[0]["c"]) : 0L;
        });

        offlineCount.Should().Be(0, because: "all vector indexes should be ONLINE after fixture initialization");
    }
}
