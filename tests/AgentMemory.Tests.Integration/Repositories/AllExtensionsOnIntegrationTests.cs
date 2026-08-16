using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Schema.Extensions;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// Wave C close, the runnable half: <b>every shipped extension activated at once</b>, against a live
/// graph, from the real migration scripts.
/// </summary>
/// <remarks>
/// <para>
/// Each extension has been verified alone. That is not the same claim as all of them together: the
/// namespaced migration keys have to coexist under one unique constraint, the indexes have to not
/// collide, and the owners report has to attribute every applied row to exactly one owner. The
/// <c>0012</c> collision that motivated the <c>ext/&lt;id&gt;/</c> namespace in the first place —
/// two designs independently claiming the same number — is precisely a failure that only appears when
/// two extensions are enabled on one database.
/// </para>
/// <para>
/// <b>What this is not.</b> The full Wave C gate is the 178-case upstream conformance suite run twice
/// on one build, all-off versus all-on, diffed for identical results. That suite is
/// <c>neo4j-labs/agent-memory-tck</c>, a separate Python repository, and it is not present on this
/// machine — so that run is recorded as not taken rather than claimed. This test covers the part that
/// can be checked here: that the four extensions install together and are attributable afterwards.
/// </para>
/// </remarks>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class AllExtensionsOnIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;

    /// <summary>The indexes the shipped extension migrations create, by name.</summary>
    private static readonly string[] ExtensionIndexes =
    [
        // delta-recall/0001
        "fact_created_at_idx", "fact_invalidated_at_idx", "fact_valid_from_idx",
        "fact_valid_until_idx", "preference_created_at_idx", "preference_invalidated_at_idx",
        "entity_created_at_idx",
        // arithmetic/0001
        "fact_derivation_key_idx", "fact_kind_idx",
    ];

    public AllExtensionsOnIntegrationTests(Neo4jIntegrationFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // The migrations are idempotent and this fixture is shared, so the indexes are left in place;
        // only the bookkeeping rows this test's run added are cleaned, and only the ext ones -- base
        // migration history belongs to the fixture.
        await _fixture.TransactionRunner.WriteAsync(async runner =>
        {
            await runner.RunAsync(
                "MATCH (m:Migration) WHERE m.extension_id IS NOT NULL DETACH DELETE m");
        });
    }

    private static string MigrationsDirectory() =>
        Path.Combine(AppContext.BaseDirectory, "Schema", "Migrations");

    private Task<List<(string Version, string? ExtensionId)>> AppliedExtensionRowsAsync() =>
        _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                "MATCH (m:Migration) WHERE m.extension_id IS NOT NULL "
                + "RETURN m.version AS version, m.extension_id AS ext ORDER BY version");
            var records = await cursor.ToListAsync();
            return records
                .Select(record => (
                    global::Neo4j.Driver.ValueExtensions.As<string>(record["version"]),
                    global::Neo4j.Driver.ValueExtensions.As<string?>(record["ext"])))
                .ToList();
        });

    private Task<List<string>> PresentIndexesAsync(string[] names) =>
        _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                "SHOW INDEXES YIELD name WHERE name IN $names RETURN name ORDER BY name",
                new { names });
            var records = await cursor.ToListAsync();
            return records
                .Select(r => global::Neo4j.Driver.ValueExtensions.As<string>(r["name"]))
                .ToList();
        });

    private async Task RunAllExtensionsAsync()
    {
        var runner = new MigrationRunner(
            _fixture.TransactionRunner,
            NullLogger<MigrationRunner>.Instance,
            MigrationsDirectory(),
            SchemaExtensionRegistry.CreateShipped());
        await runner.RunMigrationsAsync();
    }

    [Fact]
    public async Task EveryShippedExtensionInstallsOnOneDatabaseTogether()
    {
        await RunAllExtensionsAsync();

        var applied = await AppliedExtensionRowsAsync();

        // `procedural` declares no script of its own — 0011 is base-resident and stays there, because
        // re-declaring an already-applied base migration under an extension key would replay one index
        // creation under two version keys and make the same physical index appear twice in history. So
        // three of the four contribute rows.
        //
        // Asserted by OWNER, not by count: an extension that gains its first script must fail here
        // loudly rather than shift a number nobody re-reads. (It did — this expectation named two
        // owners on its first draft and `working-memory` was the third.)
        applied.Select(row => row.ExtensionId).Distinct().Should().BeEquivalentTo(
            ["arithmetic", "delta-recall", "working-memory"]);
    }

    [Fact]
    public async Task EveryExtensionRowIsNamespacedAndAttributable()
    {
        // The 0012 collision that motivated the ext/<id>/ namespace: two designs each correctly
        // reasoned "next free after 0011", and on a database enabling both, one would have been
        // silently skipped as already-applied, leaving an index missing that nobody could see.
        await RunAllExtensionsAsync();

        var applied = await AppliedExtensionRowsAsync();

        applied.Should().NotBeEmpty();
        applied.Should().OnlyContain(row => row.Version.StartsWith("ext/", StringComparison.Ordinal));
        applied.Select(row => row.Version).Should().OnlyHaveUniqueItems();
        foreach (var row in applied)
            row.Version.Should().StartWith($"ext/{row.ExtensionId}/");
    }

    [Fact]
    public async Task EveryIndexTheExtensionsDeclareExistsAfterwards()
    {
        await RunAllExtensionsAsync();

        var present = await PresentIndexesAsync(ExtensionIndexes);

        present.Should().BeEquivalentTo(ExtensionIndexes,
            "an extension that reports applied while its index is missing is the exact failure the "
            + "namespaced migration keys exist to prevent");
    }

    [Fact]
    public async Task ASecondRunWithEverythingOnAppliesNothingNew()
    {
        await RunAllExtensionsAsync();
        var first = await AppliedExtensionRowsAsync();

        await RunAllExtensionsAsync();
        var second = await AppliedExtensionRowsAsync();

        second.Should().BeEquivalentTo(first,
            "re-running with the same set must be a no-op, or every restart re-stamps history");
    }

    [Fact]
    public async Task TheBaseSequenceIsUnaffectedByHavingEveryExtensionOn()
    {
        // R3 in its most basic form: turning every extension on must not touch base bookkeeping.
        var baseBefore = await _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                "MATCH (m:Migration) WHERE m.extension_id IS NULL RETURN count(*) AS c");
            var record = await cursor.SingleAsync();
            return global::Neo4j.Driver.ValueExtensions.As<long>(record["c"]);
        });

        await RunAllExtensionsAsync();

        var baseAfter = await _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                "MATCH (m:Migration) WHERE m.extension_id IS NULL RETURN count(*) AS c");
            var record = await cursor.SingleAsync();
            return global::Neo4j.Driver.ValueExtensions.As<long>(record["c"]);
        });

        baseAfter.Should().Be(baseBefore);
    }
}
