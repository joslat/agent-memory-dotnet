using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Services;
using Neo4j.Driver;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Services;

public sealed class Neo4jConsolidationServiceTests
{
    private readonly INeo4jTransactionRunner _tx = Substitute.For<INeo4jTransactionRunner>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IIdGenerator _idGen = Substitute.For<IIdGenerator>();

    private Neo4jConsolidationService CreateSut()
    {
        _clock.UtcNow.Returns(new DateTimeOffset(2026, 6, 6, 0, 0, 0, TimeSpan.Zero));
        _idGen.GenerateId().Returns("run-1");
        return new Neo4jConsolidationService(_tx, _clock, _idGen, NullLogger<Neo4jConsolidationService>.Instance);
    }

    [Fact]
    public async Task DryRun_DoesNotMutate_AndDoesNotRecordAudit()
    {
        var report = await CreateSut().ConsolidateAsync(new ConsolidationOptions { DryRun = true });

        report.DryRun.Should().BeTrue();
        report.RunId.Should().Be("run-1");
        report.RanAtUtc.Should().Be(new DateTimeOffset(2026, 6, 6, 0, 0, 0, TimeSpan.Zero));

        // A dry run only reads counts — never a write (mutation) or the audit-node write.
        await _tx.DidNotReceive().WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task<int>>>(), Arg.Any<CancellationToken>());
        await _tx.DidNotReceive().WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_Mutates_AndRecordsAudit()
    {
        var report = await CreateSut().ConsolidateAsync(new ConsolidationOptions { DryRun = false });

        report.DryRun.Should().BeFalse();
        // Archive + remove-duplicate-preferences each run a mutating write.
        await _tx.Received().WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task<int>>>(), Arg.Any<CancellationToken>());
        // The :ConsolidationRun audit node is written exactly once.
        await _tx.Received(1).WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisabledOperations_AreSkipped()
    {
        var opts = new ConsolidationOptions
        {
            DryRun = true,
            ArchiveExpiredConversations = false,
            RemoveDuplicatePreferences = false,
            DetectDuplicateEntities = false,
            DetectLongTraces = false,
        };

        await CreateSut().ConsolidateAsync(opts);

        // Nothing enabled → no reads at all.
        await _tx.DidNotReceive().ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<int>>>(), Arg.Any<CancellationToken>());
    }
}
