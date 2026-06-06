using FluentAssertions;
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
    public async Task DecayCommand_WithSession_Prunes_AndReturnsZero()
    {
        var svc = Substitute.For<IMemoryDecayService>();
        svc.PruneExpiredMemoriesAsync("user-42", Arg.Any<CancellationToken>()).Returns(3);

        var exit = await new DecayCommand(svc, _output).ExecuteAsync("user-42");

        exit.Should().Be(0);
        _output.ToString().Should().Contain("Pruned 3");
        await svc.Received(1).PruneExpiredMemoriesAsync("user-42", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task DecayCommand_WithoutSession_ReturnsUsageError(string? session)
    {
        var svc = Substitute.For<IMemoryDecayService>();

        var exit = await new DecayCommand(svc, _output).ExecuteAsync(session);

        exit.Should().Be(2);
        _output.ToString().Should().Contain("--session");
        await svc.DidNotReceive().PruneExpiredMemoriesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

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
