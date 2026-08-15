using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Schema.Extensions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.Driver;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Schema.Extensions;

/// <summary>
/// Optional modules in a linear migration sequence: base first, then each active extension in its own
/// namespace.
/// </summary>
/// <remarks>
/// <para>
/// <b>The problem this solves, concretely.</b> Two independently-written designs each named their
/// migration <c>0012</c>, each correctly reasoning "next free number after 0011". A database that
/// enabled one and then the other a month later would have had two different scripts fighting over a
/// single key in the unique-constrained <c>(:Migration {version})</c> bookkeeping. The linear sequence
/// cannot host optional modules, and that scenario is asserted directly below.
/// </para>
/// <para>
/// These run against the existing directory test seam rather than a live database: ORDER is the
/// load-bearing property, and order is what a live test would prove slowest and least clearly.
/// </para>
/// </remarks>
public sealed class MigrationRunnerExtensionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "agentmemory-extmig-" + Guid.NewGuid().ToString("N"));

    public MigrationRunnerExtensionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private void WriteBase(string name) =>
        File.WriteAllText(Path.Combine(_root, name + ".cypher"),
            $"CREATE INDEX {name}_idx IF NOT EXISTS FOR (n:N) ON (n.x);");

    private void WriteExtension(string extensionId, string name)
    {
        var directory = Path.Combine(_root, MigrationRunner.ExtensionFolder, extensionId);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name + ".cypher"),
            $"CREATE INDEX {extensionId}_{name}_idx IF NOT EXISTS FOR (n:N) ON (n.y);");
    }

    private MigrationRunner Runner(params ISchemaExtension[] active) =>
        new(Substitute.For<INeo4jTransactionRunner>(), NullLogger<MigrationRunner>.Instance, _root, active);

    // ── the byte-identical default ────────────────────────────────────────

    [Fact]
    public void WithNoExtensionsActiveThePlanIsExactlyTheBaseFileList()
    {
        // The guarantee, at the discovery seam. Note the ext/ folder EXISTS here and is still ignored:
        // presence on disk must not activate anything, or shipping an extension would change every
        // database that merely upgraded the package.
        WriteBase("0002_a");
        WriteBase("0003_b");
        WriteExtension("arithmetic", "0001_derived");

        Runner().DiscoverMigrations().Select(m => m.Version).Should().Equal("0002_a", "0003_b");
    }

    [Fact]
    public void AnInactiveExtensionsFolderIsIgnored()
    {
        WriteBase("0002_a");
        WriteExtension("arithmetic", "0001_derived");
        WriteExtension("delta-recall", "0001_clocks");

        Runner(new StubExtension("arithmetic")).DiscoverMigrations()
            .Select(m => m.Version).Should().Equal("0002_a", "ext/arithmetic/0001_derived");
    }

    // ── ordering ──────────────────────────────────────────────────────────

    [Fact]
    public void BaseRunsBeforeEveryExtension()
    {
        // Absolute. An extension migration that ran before the base sequence would build on schema
        // that does not exist yet on a fresh database.
        WriteBase("0002_a");
        WriteBase("0011_trace_kind");
        WriteExtension("arithmetic", "0001_derived");

        Runner(new StubExtension("arithmetic")).DiscoverMigrations()
            .Select(m => m.Version)
            .Should().Equal("0002_a", "0011_trace_kind", "ext/arithmetic/0001_derived");
    }

    [Fact]
    public void ExtensionsRunInTheOrderTheRegistryDecided()
    {
        WriteBase("0002_a");
        WriteExtension("alpha", "0001_x");
        WriteExtension("zulu", "0001_y");

        // Passed in registry order (topological, ordinal tiebreak) -- the runner honours it and does
        // not re-sort by folder name, which would silently discard dependency ordering.
        var runner = Runner(new StubExtension("zulu"), new StubExtension("alpha"));

        runner.DiscoverMigrations().Select(m => m.Version)
            .Should().Equal("0002_a", "ext/zulu/0001_y", "ext/alpha/0001_x");
    }

    [Fact]
    public void OneExtensionsScriptsRunInFilenameOrder()
    {
        WriteExtension("arithmetic", "0002_second");
        WriteExtension("arithmetic", "0001_first");

        Runner(new StubExtension("arithmetic")).DiscoverMigrations().Select(m => m.Version)
            .Should().Equal("ext/arithmetic/0001_first", "ext/arithmetic/0002_second");
    }

    // ── the collision that motivated the namespace ────────────────────────

    [Fact]
    public void TwoExtensionsMayEachOwnAScriptNumberedTheSame()
    {
        // THE scenario. arithmetic and delta-recall each claimed "0012, next free after 0011". Under
        // the old linear sequence those were one key and one of them would have been skipped as
        // "already applied" -- the database would have been missing an index nobody could see was
        // missing. Namespaced, they are simply two keys.
        WriteBase("0011_trace_kind");
        WriteExtension("arithmetic", "0001_derived_fact");
        WriteExtension("delta-recall", "0001_clock_indexes");

        var versions = Runner(new StubExtension("arithmetic"), new StubExtension("delta-recall"))
            .DiscoverMigrations().Select(m => m.Version).ToList();

        versions.Should().Equal(
            "0011_trace_kind", "ext/arithmetic/0001_derived_fact", "ext/delta-recall/0001_clock_indexes");
        versions.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void AnExtensionVersionKeyCanNeverCollideWithABaseOne()
    {
        // Why no new unique constraint was needed: a base version key never contains '/'. If a base
        // script were ever named with one, this stops it before the constraint does.
        WriteBase("0002_a");
        WriteExtension("arithmetic", "0002_a");

        var plan = Runner(new StubExtension("arithmetic")).DiscoverMigrations();

        plan.Select(m => m.Version).Should().OnlyHaveUniqueItems();
        plan.Where(m => m.ExtensionId is null).Should().OnlyContain(m => !m.Version.Contains('/'));
        plan.Where(m => m.ExtensionId is not null).Should().OnlyContain(m => m.Version.Contains('/'));
    }

    // ── ownership recorded ────────────────────────────────────────────────

    [Fact]
    public void BaseMigrationsCarryNoExtensionIdAndExtensionOnesCarryTheirOwn()
    {
        // The discriminator that makes the owners report answerable: without it, a shape applied by an
        // extension is indistinguishable in the bookkeeping from one applied by base.
        WriteBase("0002_a");
        WriteExtension("arithmetic", "0001_derived");

        var plan = Runner(new StubExtension("arithmetic")).DiscoverMigrations();

        plan.Single(m => m.Version == "0002_a").ExtensionId.Should().BeNull();
        plan.Single(m => m.Version == "ext/arithmetic/0001_derived").ExtensionId.Should().Be("arithmetic");
    }

    [Fact]
    public async Task ApplyingAnExtensionMigrationRecordsItsOwner()
    {
        WriteExtension("arithmetic", "0001_derived");
        var recorded = new List<object?>();
        var txRunner = FakeTransactionRunner(recorded);

        await new MigrationRunner(
                txRunner, NullLogger<MigrationRunner>.Instance, _root, [new StubExtension("arithmetic")])
            .RunMigrationsAsync();

        // The MERGE parameters carry both the namespaced key and the owning extension.
        recorded.Should().Contain(p =>
            p!.GetType().GetProperty("version")!.GetValue(p)!.Equals("ext/arithmetic/0001_derived") &&
            "arithmetic".Equals(p.GetType().GetProperty("extensionId")!.GetValue(p)));
    }

    [Fact]
    public async Task ApplyingABaseMigrationRecordsANullOwner()
    {
        WriteBase("0002_a");
        var recorded = new List<object?>();

        await new MigrationRunner(
                FakeTransactionRunner(recorded), NullLogger<MigrationRunner>.Instance, _root)
            .RunMigrationsAsync();

        recorded.Should().Contain(p =>
            p!.GetType().GetProperty("version")!.GetValue(p)!.Equals("0002_a") &&
            p.GetType().GetProperty("extensionId")!.GetValue(p) == null);
    }

    // ── packaging fault ───────────────────────────────────────────────────

    [Fact]
    public void AnActiveExtensionWithNoFolderContributesNothingRatherThanThrowing()
    {
        // A zero-migration extension is a legal shape (a properties-only feature needs no script), so
        // an absent folder cannot be fatal. The mismatch that IS a fault -- declared scripts with no
        // folder -- is logged as a warning by the runner rather than silently producing an empty plan.
        WriteBase("0002_a");

        Runner(new StubExtension("forgetting")).DiscoverMigrations()
            .Select(m => m.Version).Should().Equal("0002_a");
    }

    private static INeo4jTransactionRunner FakeTransactionRunner(List<object?> recordedParameters)
    {
        var txRunner = Substitute.For<INeo4jTransactionRunner>();

        // IsMigrationApplied -> false: FetchAsync on a substituted cursor returns false by default.
        txRunner.ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task<bool>>>();
                var runner = Substitute.For<IAsyncQueryRunner>();
                runner.RunAsync(Arg.Any<string>(), Arg.Any<object>())
                    .Returns(_ => Task.FromResult(Substitute.For<IResultCursor>()));
                return work(runner);
            });

        txRunner.WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task>>();
                var runner = Substitute.For<IAsyncQueryRunner>();
                runner.RunAsync(Arg.Any<string>())
                    .Returns(_ => Task.FromResult(Substitute.For<IResultCursor>()));
                runner.RunAsync(Arg.Any<string>(), Arg.Any<object>())
                    .Returns(ci =>
                    {
                        if (ci.Arg<string>().Contains("MERGE (m:Migration", StringComparison.Ordinal))
                            recordedParameters.Add(ci.ArgAt<object>(1));
                        return Task.FromResult(Substitute.For<IResultCursor>());
                    });
                return work(runner);
            });

        return txRunner;
    }

    private sealed class StubExtension(string id, params string[] scripts) : ISchemaExtension
    {
        public string Id => id;
        public int Version => 1;

        public IReadOnlyDictionary<string, IReadOnlySet<string>> DeclaredProperties { get; } =
            new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

        public IReadOnlySet<string> DeclaredRelationshipTypes { get; } =
            new HashSet<string>(StringComparer.Ordinal);

        public IReadOnlySet<string> DeclaredLabels { get; } = new HashSet<string>(StringComparer.Ordinal);
        public IReadOnlyList<string> MigrationScripts { get; } = [.. scripts];

        public IReadOnlySet<string> BaseResidentMigrations { get; } =
            new HashSet<string>(StringComparer.Ordinal);

        public SchemaParityDelta ParityDelta { get; } = SchemaParityDelta.Empty;
        public IReadOnlySet<string> DependsOn { get; } = new HashSet<string>(StringComparer.Ordinal);
        public TckProfileDescriptor TckProfile { get; } = TckProfileDescriptor.None;
    }
}
