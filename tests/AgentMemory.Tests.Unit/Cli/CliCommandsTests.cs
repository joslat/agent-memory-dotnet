using FluentAssertions;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Cli.Commands;
using AgentMemory.Neo4j.Infrastructure;
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
