using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Schema.Extensions;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.Driver;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// The replay a real deployment performs: enable an extension, later upgrade to a library that ships
/// new base migrations, re-run — against a live Neo4j.
/// </summary>
/// <remarks>
/// <para>
/// The unit tests prove the ORDER of the plan through the directory seam. They cannot prove the part
/// that actually bit: that a namespaced version key survives the unique <c>migration_version</c>
/// constraint alongside base keys, that re-running skips rather than duplicating or erroring, and that
/// <c>extension_id</c> lands on the node. Those are properties of the database, not of the planner.
/// </para>
/// <para>
/// Uses the internal test-seam constructor with a temp directory, unique 99xx versions and its own
/// labels, and cleans up both, so the shared container is unaffected.
/// </para>
/// </remarks>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class SchemaExtensionMigrationIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private string _dir = string.Empty;

    private const string ExtensionId = "itestext";
    private const string BaseV1 = "9910_itest_ext_base_first";
    private const string BaseV2 = "9911_itest_ext_base_second";
    private const string ExtV1 = $"ext/{ExtensionId}/0001_itest_ext_first";

    private static readonly string[] Versions = [BaseV1, BaseV2, ExtV1];
    private static readonly string[] IndexNames =
        ["itest_ext_base_a_idx", "itest_ext_base_b_idx", "itest_ext_own_idx"];

    public SchemaExtensionMigrationIntegrationTests(Neo4jIntegrationFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "itest-extmig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        File.WriteAllText(Path.Combine(_dir, BaseV1 + ".cypher"),
            "CREATE INDEX itest_ext_base_a_idx IF NOT EXISTS FOR (n:ItestExtNode) ON (n.a);\n");

        var extensionDirectory = Path.Combine(_dir, MigrationRunner.ExtensionFolder, ExtensionId);
        Directory.CreateDirectory(extensionDirectory);
        File.WriteAllText(Path.Combine(extensionDirectory, "0001_itest_ext_first.cypher"),
            "CREATE INDEX itest_ext_own_idx IF NOT EXISTS FOR (n:ItestExtNode) ON (n.own);\n");

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var index in IndexNames)
            await _fixture.TransactionRunner.WriteAsync(
                async runner => { await runner.RunAsync($"DROP INDEX {index} IF EXISTS"); });

        await _fixture.TransactionRunner.WriteAsync(async runner =>
        {
            await runner.RunAsync(
                "MATCH (m:Migration) WHERE m.version IN $versions DETACH DELETE m",
                new { versions = Versions });
        });

        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Adds the base migration a later library version would ship.</summary>
    private void ShipBaseUpgrade() =>
        File.WriteAllText(Path.Combine(_dir, BaseV2 + ".cypher"),
            "CREATE INDEX itest_ext_base_b_idx IF NOT EXISTS FOR (n:ItestExtNode) ON (n.b);\n");

    private MigrationRunner Runner(params ISchemaExtension[] active) =>
        new(_fixture.TransactionRunner, NullLogger<MigrationRunner>.Instance, _dir, active);

    private Task<List<(string Version, string? ExtensionId)>> AppliedAsync() =>
        _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                "MATCH (m:Migration) WHERE m.version IN $versions "
                + "RETURN m.version AS version, m.extension_id AS ext ORDER BY version",
                new { versions = Versions });
            var records = await cursor.ToListAsync();
            // Fully qualified: FluentAssertions and the Neo4j driver both define an `As<T>` extension,
            // and the sibling MigrationRunnerIntegrationTests hit the same ambiguity.
            return records
                .Select(record => (
                    global::Neo4j.Driver.ValueExtensions.As<string>(record["version"]),
                    global::Neo4j.Driver.ValueExtensions.As<string?>(record["ext"])))
                .ToList();
        });

    private Task<long> CountIndexesAsync(params string[] names) =>
        _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                "SHOW INDEXES YIELD name WHERE name IN $names RETURN count(*) AS c",
                new { names });
            var record = await cursor.SingleAsync();
            return global::Neo4j.Driver.ValueExtensions.As<long>(record["c"]);
        });

    [Fact]
    public async Task AnExtensionMigrationAppliesAlongsideBaseAndRecordsItsOwner()
    {
        await Runner(new ItestExtension()).RunMigrationsAsync();

        var applied = await AppliedAsync();

        applied.Should().HaveCount(2);
        applied.Should().Contain((BaseV1, null), "a base migration carries no extension id");
        applied.Should().Contain((ExtV1, ExtensionId), "an extension migration names its owner");
        (await CountIndexesAsync("itest_ext_base_a_idx", "itest_ext_own_idx")).Should().Be(2);
    }

    [Fact]
    public async Task ANamespacedVersionKeyCoexistsWithBaseKeysUnderTheUniqueConstraint()
    {
        // The reason no new constraint was needed: 'ext/<id>/000N' and '000N' are simply different
        // strings, so the existing unique migration_version constraint covers both namespaces. Asserted
        // against the live constraint rather than reasoned about.
        await Runner(new ItestExtension()).RunMigrationsAsync();

        var constraintExists = await _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                "SHOW CONSTRAINTS YIELD name WHERE name = 'migration_version' RETURN count(*) AS c");
            var record = await cursor.SingleAsync();
            return global::Neo4j.Driver.ValueExtensions.As<long>(record["c"]) > 0;
        });

        constraintExists.Should().BeTrue();
        (await AppliedAsync()).Select(m => m.Version).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task ExtensionThenBaseUpgradeThenReRun_AppliesOnlyTheNewBaseScript()
    {
        // THE replay. A database enabled the extension at base 9910; the library later ships 9911.
        // Re-running must apply 9911, skip everything already applied, and leave the extension's own
        // row untouched and unduplicated.
        await Runner(new ItestExtension()).RunMigrationsAsync();
        (await AppliedAsync()).Should().HaveCount(2);

        ShipBaseUpgrade();
        await Runner(new ItestExtension()).RunMigrationsAsync();

        var applied = await AppliedAsync();
        applied.Should().HaveCount(3);
        applied.Select(m => m.Version).Should().OnlyHaveUniqueItems(
            "a replay must never duplicate a (:Migration) row");
        applied.Should().Contain((BaseV2, null));
        applied.Should().Contain((ExtV1, ExtensionId));
        (await CountIndexesAsync(IndexNames)).Should().Be(3);
    }

    [Fact]
    public async Task ReRunningWithTheExtensionSwitchedOffLeavesItsSchemaAndHistoryIntact()
    {
        // Deactivation is not a down-migration. The applied schema is additive and harmless (R3), and
        // its history must remain so the owners report can still explain where the shape came from.
        await Runner(new ItestExtension()).RunMigrationsAsync();

        ShipBaseUpgrade();
        await Runner().RunMigrationsAsync();

        var applied = await AppliedAsync();
        applied.Should().Contain((ExtV1, ExtensionId), "history survives deactivation");
        applied.Should().Contain((BaseV2, null), "base still moves forward");
        (await CountIndexesAsync("itest_ext_own_idx")).Should().Be(1,
            "the extension's index is left in place, not dropped");
    }

    [Fact]
    public async Task ReActivatingAfterDeactivationSkipsRatherThanReApplying()
    {
        await Runner(new ItestExtension()).RunMigrationsAsync();
        await Runner().RunMigrationsAsync();
        await Runner(new ItestExtension()).RunMigrationsAsync();

        (await AppliedAsync()).Select(m => m.Version).Should().OnlyHaveUniqueItems();
        (await CountIndexesAsync("itest_ext_own_idx")).Should().Be(1);
    }

    private sealed class ItestExtension : ISchemaExtension
    {
        public string Id => ExtensionId;
        public int Version => 1;

        public IReadOnlyDictionary<string, IReadOnlySet<string>> DeclaredProperties { get; } =
            new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

        public IReadOnlySet<string> DeclaredRelationshipTypes { get; } =
            new HashSet<string>(StringComparer.Ordinal);

        public IReadOnlySet<string> DeclaredLabels { get; } = new HashSet<string>(StringComparer.Ordinal);
        public IReadOnlyList<string> MigrationScripts { get; } = ["0001_itest_ext_first.cypher"];

        public IReadOnlySet<string> BaseResidentMigrations { get; } =
            new HashSet<string>(StringComparer.Ordinal);

        public SchemaParityDelta ParityDelta { get; } = SchemaParityDelta.Empty;
        public IReadOnlySet<string> DependsOn { get; } = new HashSet<string>(StringComparer.Ordinal);
        public TckProfileDescriptor TckProfile { get; } = TckProfileDescriptor.None;
    }
}
