using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain.Schema;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Schema;
using AgentMemory.Core.Stubs;
using AgentMemory.Neo4j.Services;
using AgentMemory.Tests.Integration.Fixtures;
using Neo4j.Driver;
using NSubstitute;

namespace AgentMemory.Tests.Integration.Services;

/// <summary>
/// Live-DB coverage for <see cref="Neo4jSchemaManager"/> (G4): the versioned <c>:Schema</c> node
/// persistence Cypher. Verifies round-trip of a serialized schema, the one-active-version-per-name
/// invariant (a new active save deactivates prior versions), specific-version load, inactive saves,
/// name-prefix listing, and delete-by-id.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class Neo4jSchemaManagerIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jSchemaManager _manager;
    private readonly DateTimeOffset _t0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public Neo4jSchemaManagerIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;

        var clock = Substitute.For<IClock>();
        // Successive saves get strictly increasing created_at so "newest version" ordering is deterministic.
        clock.UtcNow.Returns(
            _t0, _t0.AddSeconds(1), _t0.AddSeconds(2), _t0.AddSeconds(3), _t0.AddSeconds(4), _t0.AddSeconds(5));

        _manager = new Neo4jSchemaManager(
            _fixture.TransactionRunner,
            new GuidIdGenerator(),
            clock,
            NullLogger<Neo4jSchemaManager>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task SaveThenLoadActive_RoundTripsConfig()
    {
        var schema = SchemaLoader.CreateForTypes(["PATIENT", "DRUG", "DIAGNOSIS"]) with
        {
            Name = "medical",
            Version = "1.0",
            Description = "Medical ontology"
        };

        await _manager.SaveSchemaAsync(schema);

        (await _manager.SchemaExistsAsync("medical")).Should().BeTrue();

        var loaded = await _manager.LoadSchemaAsync("medical");
        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("medical");
        loaded.Version.Should().Be("1.0");
        loaded.Description.Should().Be("Medical ontology");
        loaded.GetEntityTypeNames().Should().BeEquivalentTo(["PATIENT", "DRUG", "DIAGNOSIS"]);

        var list = await _manager.ListSchemasAsync();
        list.Should().ContainSingle();
        list[0].Name.Should().Be("medical");
        list[0].LatestVersion.Should().Be("1.0");
        list[0].VersionCount.Should().Be(1);
        list[0].IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task SaveNewActiveVersion_DeactivatesPrior_AndBecomesActive()
    {
        await _manager.SaveSchemaAsync(SchemaForVersion("1.0"));
        await _manager.SaveSchemaAsync(SchemaForVersion("2.0"));

        var active = await _manager.LoadSchemaAsync("medical");
        active!.Version.Should().Be("2.0", "the newest active save supersedes the prior active version");

        var list = await _manager.ListSchemasAsync();
        list.Should().ContainSingle();
        list[0].VersionCount.Should().Be(2);
        list[0].LatestVersion.Should().Be("2.0");
        list[0].IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task LoadSchemaVersion_ReturnsRequestedVersion_NotJustActive()
    {
        await _manager.SaveSchemaAsync(SchemaForVersion("1.0"));
        await _manager.SaveSchemaAsync(SchemaForVersion("2.0"));

        var v1 = await _manager.LoadSchemaVersionAsync("medical", "1.0");
        v1.Should().NotBeNull();
        v1!.Version.Should().Be("1.0");
    }

    [Fact]
    public async Task SaveInactive_NotReturnedByLoadActive_ButLoadableByVersion()
    {
        await _manager.SaveSchemaAsync(SchemaForVersion("1.0"), setActive: false);

        (await _manager.LoadSchemaAsync("medical")).Should().BeNull("no version is active");
        (await _manager.LoadSchemaVersionAsync("medical", "1.0")).Should().NotBeNull();

        var list = await _manager.ListSchemasAsync();
        list.Should().ContainSingle();
        list[0].IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task ListSchemas_WithNameFilter_MatchesByPrefix()
    {
        await _manager.SaveSchemaAsync(SchemaForVersion("1.0"));                       // "medical"
        await _manager.SaveSchemaAsync(SchemaForVersion("1.0") with { Name = "legal" });

        var filtered = await _manager.ListSchemasAsync("med");

        filtered.Should().ContainSingle();
        filtered[0].Name.Should().Be("medical");
    }

    [Fact]
    public async Task DeleteSchema_RemovesRevision_AndIsIdempotent()
    {
        await _manager.SaveSchemaAsync(SchemaForVersion("1.0"));
        var id = await GetSchemaIdAsync("medical", "1.0");

        (await _manager.DeleteSchemaAsync(id)).Should().BeTrue();
        (await _manager.SchemaExistsAsync("medical")).Should().BeFalse();
        (await _manager.LoadSchemaAsync("medical")).Should().BeNull();

        (await _manager.DeleteSchemaAsync(id)).Should().BeFalse("the revision was already deleted");
    }

    private static EntitySchemaConfig SchemaForVersion(string version) =>
        SchemaLoader.CreateForTypes(["PATIENT", "DRUG"]) with
        {
            Name = "medical",
            Version = version,
            Description = $"Medical ontology {version}"
        };

    // The manager's id is internal (not surfaced by the interface); fetch it directly for the delete test.
    private async Task<string> GetSchemaIdAsync(string name, string version) =>
        await _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                "MATCH (s:Schema {name: $name, version: $version}) RETURN s.id AS id",
                new { name, version });
            var records = await cursor.ToListAsync();
            return global::Neo4j.Driver.ValueExtensions.As<string>(records[0]["id"]);
        });
}
