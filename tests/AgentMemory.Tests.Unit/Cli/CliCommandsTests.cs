using FluentAssertions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Cli.Commands;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Schema.Extensions;
using Neo4j.Driver;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Cli;

public sealed class CliCommandsTests
{
    private readonly StringWriter _output = new();

    [Fact]
    public async Task MigrateCommand_RunsMigrations_AndReturnsZero()
    {
        var runner = Substitute.For<IMigrationRunner>();

        var exit = await new MigrateCommand(runner, _output).ExecuteAsync();

        exit.Should().Be(0);
        await runner.Received(1).RunMigrationsAsync(Arg.Any<CancellationToken>());
        _output.ToString().Should().Contain("Migrations complete");
    }

    [Fact]
    public async Task BootstrapCommand_BootstrapsSchema_AndReturnsZero()
    {
        var bootstrapper = Substitute.For<ISchemaBootstrapper>();

        var exit = await new BootstrapCommand(bootstrapper, _output).ExecuteAsync();

        exit.Should().Be(0);
        await bootstrapper.Received(1).BootstrapAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SchemaCheckCommand_AllObjectsPresent_ReturnsZero()
    {
        var options = Options.Create(new Neo4jOptions { EmbeddingDimensions = 1536, Database = "neo4j" });
        var present = new HashSet<string>(SchemaConformance.ExpectedObjectNames(1536), StringComparer.Ordinal);
        var runner = Substitute.For<INeo4jTransactionRunner>();
        runner.ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<HashSet<string>>>>(), Arg.Any<CancellationToken>())
              .Returns(present);

        var exit = await new SchemaCheckCommand(runner, options, _output).ExecuteAsync();

        exit.Should().Be(0);
        _output.ToString().Should().Contain("OK").And.Contain("neo4j");
    }

    [Fact]
    public async Task SchemaCheckCommand_WithExtensionRegistry_ReportsOwnersAlongsideConformance()
    {
        // 30.14. The two halves answer different questions -- "are the objects present?" and "whose
        // shape is each of them?" -- and the second is the one nothing could answer before. trace_kind
        // shipped in migration 0011 with its rationale in a Cypher comment and no owner anywhere.
        var options = Options.Create(new Neo4jOptions { EmbeddingDimensions = 1536, Database = "neo4j" });
        options.Value.Extensions.Add("procedural");
        var present = new HashSet<string>(SchemaConformance.ExpectedObjectNames(1536), StringComparer.Ordinal);
        var runner = Substitute.For<INeo4jTransactionRunner>();
        runner.ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<HashSet<string>>>>(), Arg.Any<CancellationToken>())
              .Returns(present);

        var exit = await new SchemaCheckCommand(
            runner, options, _output, new SchemaExtensionRegistry([new ProceduralSchemaExtension()]))
            .ExecuteAsync();

        exit.Should().Be(0);
        _output.ToString().Should()
            .Contain("policy base 0.5.0")
            .And.Contain("[procedural v1]")
            .And.Contain("ReasoningTrace.trace_kind")
            .And.Contain("owner: procedural")
            .And.Contain("OK");
    }

    [Fact]
    public async Task SchemaCheckCommand_OrphanExtensionMigration_ReturnsOne_ThoughEveryObjectIsPresent()
    {
        // The failure only the owners report can produce: the database carries schema applied by a
        // module this binary does not have. Every index is present, so conformance says OK -- and the
        // graph still contains shapes nothing can account for.
        var options = Options.Create(new Neo4jOptions { EmbeddingDimensions = 1536, Database = "neo4j" });
        var present = new HashSet<string>(SchemaConformance.ExpectedObjectNames(1536), StringComparer.Ordinal);
        var runner = Substitute.For<INeo4jTransactionRunner>();
        runner.ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<HashSet<string>>>>(), Arg.Any<CancellationToken>())
              .Returns(present);
        runner.ReadAsync(
                Arg.Any<Func<IAsyncQueryRunner, Task<Dictionary<string, string?>>>>(),
                Arg.Any<CancellationToken>())
              .Returns(new Dictionary<string, string?>(StringComparer.Ordinal)
              {
                  ["ext/arithmetic/0001_derived_fact"] = "2026-08-20T10:00:00Z",
              });

        var exit = await new SchemaCheckCommand(
            runner, options, _output, new SchemaExtensionRegistry([new ProceduralSchemaExtension()]))
            .ExecuteAsync();

        exit.Should().Be(1);
        _output.ToString().Should().Contain("no owner").And.Contain("arithmetic");
    }

    [Fact]
    public async Task SchemaCheckCommand_WithoutRegistry_BehavesExactlyAsBefore()
    {
        // SchemaCheckCommand is public API. A host constructing it directly must not start failing on
        // a check it never asked for, so the two-argument constructor skips the owners report entirely.
        var options = Options.Create(new Neo4jOptions { EmbeddingDimensions = 1536, Database = "neo4j" });
        var present = new HashSet<string>(SchemaConformance.ExpectedObjectNames(1536), StringComparer.Ordinal);
        var runner = Substitute.For<INeo4jTransactionRunner>();
        runner.ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<HashSet<string>>>>(), Arg.Any<CancellationToken>())
              .Returns(present);

        var exit = await new SchemaCheckCommand(runner, options, _output).ExecuteAsync();

        exit.Should().Be(0);
        _output.ToString().Should().NotContain("owner:");
    }

    [Fact]
    public async Task SchemaCheckCommand_MissingObjects_ReturnsOne_AndListsThem()
    {
        var options = Options.Create(new Neo4jOptions { EmbeddingDimensions = 1536, Database = "neo4j" });
        // The live database is missing two of the expected objects.
        var present = new HashSet<string>(
            SchemaConformance.ExpectedObjectNames(1536)
                .Where(n => n != "entity_location_idx" && n != "fact_owner_idx"),
            StringComparer.Ordinal);
        var runner = Substitute.For<INeo4jTransactionRunner>();
        runner.ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<HashSet<string>>>>(), Arg.Any<CancellationToken>())
              .Returns(present);

        var exit = await new SchemaCheckCommand(runner, options, _output).ExecuteAsync();

        exit.Should().Be(1);
        _output.ToString().Should()
            .Contain("FAILED")
            .And.Contain("entity_location_idx")
            .And.Contain("fact_owner_idx")
            .And.Contain("bootstrap");
    }

    /// <summary>
    /// L10. A FAILED index is <b>present by name</b>, so a name-only conformance check reports OK on
    /// exactly the condition an operator opens <c>schema-check</c> to diagnose: queries still succeed
    /// through full scans, and the only symptom is unexplained slowness.
    /// </summary>
    [Fact]
    public async Task SchemaCheckCommand_OwnedIndexFailed_ReturnsOne_EvenThoughEveryNameIsPresent()
    {
        var options = Options.Create(new Neo4jOptions { EmbeddingDimensions = 1536, Database = "neo4j" });
        var present = new HashSet<string>(SchemaConformance.ExpectedObjectNames(1536), StringComparer.Ordinal);
        var broken = SchemaConformance.ExpectedObjectNames(1536)[0];
        var runner = Substitute.For<INeo4jTransactionRunner>();
        runner.ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<HashSet<string>>>>(), Arg.Any<CancellationToken>())
              .Returns(present);
        runner.ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<IndexState[]>>>(), Arg.Any<CancellationToken>())
              .Returns([new IndexState(broken, "FAILED", "RANGE", null)]);

        var exit = await new SchemaCheckCommand(runner, options, _output).ExecuteAsync();

        exit.Should().Be(1);
        _output.ToString().Should().Contain("FAILED").And.Contain(broken);
    }

    /// <summary>
    /// The scoping half: a neighbouring application's failed index on a shared database is reported
    /// for the operator's benefit but is not this library's conformance failure, so the exit code
    /// stays 0 — otherwise <c>schema-check</c> can never pass on a shared instance.
    /// </summary>
    [Fact]
    public async Task SchemaCheckCommand_ForeignIndexFailed_StillReturnsZero_ButSaysSo()
    {
        var options = Options.Create(new Neo4jOptions { EmbeddingDimensions = 1536, Database = "neo4j" });
        var present = new HashSet<string>(SchemaConformance.ExpectedObjectNames(1536), StringComparer.Ordinal);
        var runner = Substitute.For<INeo4jTransactionRunner>();
        runner.ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<HashSet<string>>>>(), Arg.Any<CancellationToken>())
              .Returns(present);
        runner.ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<IndexState[]>>>(), Arg.Any<CancellationToken>())
              .Returns([new IndexState("someone_elses_idx", "FAILED", "RANGE", null)]);

        var exit = await new SchemaCheckCommand(runner, options, _output).ExecuteAsync();

        exit.Should().Be(0);
        _output.ToString().Should().Contain("someone_elses_idx").And.Contain("not created by AgentMemory");
    }

    /// <summary>
    /// P6. A POPULATING index is neither healthy nor failed, and it was the state this command could
    /// not describe at all.
    /// </summary>
    /// <remarks>
    /// It matters most on the vector indexes: a search against a half-built one succeeds and returns a
    /// <b>subset</b> of the corpus, so recall is quietly partial and the symptom is "memory seems to
    /// have forgotten things" rather than any error. Transient, so it must not fail the check -- but
    /// silence is how an operator spends an afternoon debugging retrieval quality on a half-built
    /// index.
    /// </remarks>
    [Fact]
    public async Task SchemaCheckCommand_PopulatingIndex_ReturnsZero_ButSaysWhatItMeans()
    {
        var options = Options.Create(new Neo4jOptions { EmbeddingDimensions = 1536, Database = "neo4j" });
        var present = new HashSet<string>(SchemaConformance.ExpectedObjectNames(1536), StringComparer.Ordinal);
        var building = SchemaConformance.ExpectedObjectNames(1536)[0];
        var runner = Substitute.For<INeo4jTransactionRunner>();
        runner.ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<HashSet<string>>>>(), Arg.Any<CancellationToken>())
              .Returns(present);
        runner.ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<IndexState[]>>>(), Arg.Any<CancellationToken>())
              .Returns([new IndexState(building, "POPULATING", "VECTOR", 42.5)]);

        var exit = await new SchemaCheckCommand(runner, options, _output).ExecuteAsync();

        exit.Should().Be(0, "populating is transient and legitimate right after bootstrap");
        _output.ToString().Should().Contain("POPULATING").And.Contain(building)
            .And.Contain("42.5", "the percentage is the difference between 'wait' and 'something is wrong'")
            .And.Contain("subset", "an operator must be told WHY a half-built index matters");
    }

    [Fact]
    public async Task ConsolidateCommand_DefaultsToDryRun()
    {
        var svc = Substitute.For<IConsolidationService>();
        svc.ConsolidateAsync(Arg.Any<ConsolidationOptions>(), Arg.Any<CancellationToken>())
            .Returns(Report(dryRun: true));

        var exit = await new ConsolidateCommand(svc, _output).ExecuteAsync(apply: false);

        exit.Should().Be(0);
        await svc.Received(1).ConsolidateAsync(
            Arg.Is<ConsolidationOptions>(o => o.DryRun == true), Arg.Any<CancellationToken>());
        _output.ToString().Should().Contain("DRY-RUN").And.Contain("--apply");
    }

    [Fact]
    public async Task ConsolidateCommand_Apply_RunsMutating()
    {
        var svc = Substitute.For<IConsolidationService>();
        svc.ConsolidateAsync(Arg.Any<ConsolidationOptions>(), Arg.Any<CancellationToken>())
            .Returns(Report(dryRun: false));

        var exit = await new ConsolidateCommand(svc, _output).ExecuteAsync(apply: true);

        exit.Should().Be(0);
        await svc.Received(1).ConsolidateAsync(
            Arg.Is<ConsolidationOptions>(o => o.DryRun == false), Arg.Any<CancellationToken>());
        _output.ToString().Should().Contain("APPLIED");
    }

    [Fact]
    public async Task ConflictsCommand_PrintsReport_AndReturnsZero()
    {
        var svc = Substitute.For<IConflictDetectionService>();
        svc.DetectConflictsAsync(Arg.Any<ConflictDetectionOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ConflictReport
            {
                RanAtUtc = new DateTimeOffset(2026, 6, 6, 0, 0, 0, TimeSpan.Zero),
                FactConflicts = new[]
                {
                    new FactConflict("Alice", "works_at", null, new[]
                    {
                        new ConflictingFactValue("f1", "Acme", 0.9),
                        new ConflictingFactValue("f2", "Globex", 0.8),
                    }),
                },
            });

        var exit = await new ConflictsCommand(svc, _output).ExecuteAsync();

        exit.Should().Be(0);
        _output.ToString().Should().Contain("Alice / works_at").And.Contain("Acme").And.Contain("Globex");
    }

    [Fact]
    public async Task ConflictsCommand_NoConflicts_SaysSo()
    {
        var svc = Substitute.For<IConflictDetectionService>();
        svc.DetectConflictsAsync(Arg.Any<ConflictDetectionOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ConflictReport { RanAtUtc = new DateTimeOffset(2026, 6, 6, 0, 0, 0, TimeSpan.Zero) });

        var exit = await new ConflictsCommand(svc, _output).ExecuteAsync();

        exit.Should().Be(0);
        _output.ToString().Should().Contain("No contradictions found");
    }

    [Fact]
    public async Task DecayCommand_WithOwner_PrunesScoped_AndReturnsZero()
    {
        var svc = Substitute.For<IMemoryDecayService>();
        svc.PruneExpiredMemoriesAsync(Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>()).Returns(3);

        var exit = await new DecayCommand(svc, _output).ExecuteAsync("user-42");

        exit.Should().Be(0);
        _output.ToString().Should().Contain("Pruned 3").And.Contain("user-42");
        await svc.Received(1).PruneExpiredMemoriesAsync(
            Arg.Is<MemoryScope?>(s => s != null && s.OwnerId == "user-42"), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task DecayCommand_WithoutOwner_PrunesGlobal_AndReturnsZero(string? owner)
    {
        var svc = Substitute.For<IMemoryDecayService>();
        svc.PruneExpiredMemoriesAsync(Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>()).Returns(7);

        var exit = await new DecayCommand(svc, _output).ExecuteAsync(owner);

        exit.Should().Be(0);
        _output.ToString().Should().Contain("Pruned 7").And.Contain("global");
        await svc.Received(1).PruneExpiredMemoriesAsync(
            Arg.Is<MemoryScope?>(s => s == null), Arg.Any<CancellationToken>());
    }

    // ── invalidate ───────────────────────────────────────────────────────

    private static InvalidateCommand NewInvalidate(IFactRepository f, IEntityRepository e, IPreferenceRepository p, TextWriter o)
        => new(f, e, p, o);

    [Fact]
    public async Task InvalidateCommand_Fact_Scoped_Invalidates_AndReturnsZero()
    {
        var (facts, entities, prefs) = LongTermRepos();
        facts.InvalidateAsync("f1", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>()).Returns(true);

        var exit = await NewInvalidate(facts, entities, prefs, _output).ExecuteAsync("fact", "f1", "alice");

        exit.Should().Be(0);
        _output.ToString().Should().Contain("Invalidated fact 'f1'").And.Contain("alice");
        await facts.Received(1).InvalidateAsync(
            "f1", Arg.Is<MemoryScope?>(s => s != null && s.OwnerId == "alice"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvalidateCommand_Entity_Unscoped_RoutesToEntityRepo()
    {
        var (facts, entities, prefs) = LongTermRepos();
        entities.InvalidateAsync("e1", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>()).Returns(true);

        var exit = await NewInvalidate(facts, entities, prefs, _output).ExecuteAsync("entity", "e1", owner: null);

        exit.Should().Be(0);
        await entities.Received(1).InvalidateAsync("e1", Arg.Is<MemoryScope?>(s => s == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvalidateCommand_NotFound_ReturnsOne()
    {
        var (facts, entities, prefs) = LongTermRepos();
        prefs.InvalidateAsync("p1", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>()).Returns(false);

        var exit = await NewInvalidate(facts, entities, prefs, _output).ExecuteAsync("preference", "p1", owner: null);

        exit.Should().Be(1);
        _output.ToString().Should().Contain("No matching preference 'p1'");
    }

    [Theory]
    [InlineData(null, "id")]
    [InlineData("fact", null)]
    [InlineData("widget", "id")]
    public async Task InvalidateCommand_BadArgs_ReturnsOne_WithoutCallingRepo(string? type, string? id)
    {
        var (facts, entities, prefs) = LongTermRepos();

        var exit = await NewInvalidate(facts, entities, prefs, _output).ExecuteAsync(type, id, owner: null);

        exit.Should().Be(1);
        await facts.DidNotReceive().InvalidateAsync(Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    // ── supersede ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SupersedeCommand_Fact_Scoped_Supersedes_AndReturnsZero()
    {
        var (facts, _, prefs) = LongTermRepos();
        facts.SupersedeAsync("loser", "winner", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>()).Returns(true);

        var exit = await new SupersedeCommand(facts, prefs, _output).ExecuteAsync("fact", "loser", "winner", "alice");

        exit.Should().Be(0);
        _output.ToString().Should().Contain("Superseded fact 'loser' with 'winner'").And.Contain("alice");
        await facts.Received(1).SupersedeAsync(
            "loser", "winner", Arg.Is<MemoryScope?>(s => s != null && s.OwnerId == "alice"), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null, "l", "w")]
    [InlineData("fact", null, "w")]
    [InlineData("fact", "l", null)]
    [InlineData("entity", "l", "w")] // entity is not supersedable
    public async Task SupersedeCommand_BadArgs_ReturnsOne(string? type, string? loser, string? winner)
    {
        var (facts, _, prefs) = LongTermRepos();

        var exit = await new SupersedeCommand(facts, prefs, _output).ExecuteAsync(type, loser, winner, owner: null);

        exit.Should().Be(1);
        await facts.DidNotReceive().SupersedeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    // ── history ────────────────────────────────────────────────────────────

    [Fact]
    public async Task HistoryCommand_RequestsFilteredHistory_AndPrintsRows()
    {
        var svc = Substitute.For<IMemoryHistoryService>();
        var records = new[]
        {
            new MemoryHistoryRecord
            {
                Kind = MemoryHistoryKind.Fact,
                Id = "f1",
                Summary = "Alice works_at Acme",
                OwnerId = "alice",
                Status = MemoryHistoryStatus.Invalidated,
                CreatedAtUtc = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
                InvalidatedAtUtc = new DateTimeOffset(2026, 1, 3, 3, 4, 5, TimeSpan.Zero),
                SupersededByIds = new[] { "f2" },
                SourceMessageIds = new[] { "m1" },
            },
        };
        svc.GetHistoryAsync(Arg.Any<MemoryHistoryQuery>(), Arg.Any<CancellationToken>()).Returns(records);

        var exit = await new HistoryCommand(svc, _output).ExecuteAsync(
            "fact", "f1", "alice", liveOnly: false, ownOnly: false, limitValue: "25");

        exit.Should().Be(0);
        await svc.Received(1).GetHistoryAsync(
            Arg.Is<MemoryHistoryQuery>(q =>
                q.Kind == MemoryHistoryKind.Fact &&
                q.Id == "f1" &&
                q.OwnerId == "alice" &&
                q.IncludeInvalidated &&
                q.IncludeShared &&
                q.Limit == 25),
            Arg.Any<CancellationToken>());
        _output.ToString().Should()
            .Contain("history: 1 memory record")
            .And.Contain("Alice works_at Acme")
            .And.Contain("superseded_by: f2")
            .And.Contain("source_messages: m1");
    }

    [Theory]
    [InlineData("widget", "10")]
    [InlineData("fact", "0")]
    [InlineData("fact", "not-a-number")]
    public async Task HistoryCommand_BadArgs_ReturnsOne_WithoutCallingService(string? type, string? limit)
    {
        var svc = Substitute.For<IMemoryHistoryService>();

        var exit = await new HistoryCommand(svc, _output).ExecuteAsync(
            type, id: null, owner: null, liveOnly: false, ownOnly: false, limitValue: limit);

        exit.Should().Be(1);
        await svc.DidNotReceive().GetHistoryAsync(Arg.Any<MemoryHistoryQuery>(), Arg.Any<CancellationToken>());
    }
    private static (IFactRepository Facts, IEntityRepository Entities, IPreferenceRepository Preferences) LongTermRepos()
        => (Substitute.For<IFactRepository>(), Substitute.For<IEntityRepository>(), Substitute.For<IPreferenceRepository>());

    private static ConsolidationReport Report(bool dryRun) => new()
    {
        RunId = "run-1",
        DryRun = dryRun,
        RanAtUtc = new DateTimeOffset(2026, 6, 6, 0, 0, 0, TimeSpan.Zero),
        ConversationsArchived = 1,
        DuplicatePreferencesRemoved = 2,
        DuplicateEntitiesDetected = 3,
        LongTraceCandidates = 4,
    };
}
