using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// DRT-5 (part 3): the two readers of the read notes wait for this process's queue first, so a host does not have to.
/// The history of an item read right after a recall includes that recall; the decay job does not prune on a count a
/// queued note has not raised yet.
/// </summary>
public sealed class ReadNoteFlushCallersTests
{
    [Fact]
    public async Task The_history_service_flushes_the_queue_before_it_reads()
    {
        var order = new List<string>();
        var tracker = Substitute.For<IMemoryAccessTracker>();
        tracker.FlushAsync(Arg.Any<CancellationToken>()).Returns(_ => { order.Add("flush"); return Task.CompletedTask; });
        var tx = Substitute.For<INeo4jTransactionRunner>();
        tx.ReadAsync(Arg.Any<Func<global::Neo4j.Driver.IAsyncQueryRunner, Task<IReadOnlyList<MemoryHistoryRecord>>>>(), Arg.Any<CancellationToken>())
            .Returns(_ => { order.Add("read"); return Task.FromResult<IReadOnlyList<MemoryHistoryRecord>>([]); });

        await new Neo4jMemoryHistoryService(tx, tracker).GetHistoryAsync(new MemoryHistoryQuery { Kind = MemoryHistoryKind.Fact, Id = "f1" });

        order.Should().Equal("flush", "read");
    }

    [Fact]
    public async Task The_decay_job_flushes_the_queue_before_it_prunes()
    {
        var order = new List<string>();
        var tracker = Substitute.For<IMemoryAccessTracker>();
        tracker.FlushAsync(Arg.Any<CancellationToken>()).Returns(_ => { order.Add("flush"); return Task.CompletedTask; });
        var tx = Substitute.For<INeo4jTransactionRunner>();
        tx.WriteAsync(Arg.Any<Func<global::Neo4j.Driver.IAsyncQueryRunner, Task<int>>>(), Arg.Any<CancellationToken>())
            .Returns(_ => { order.Add("prune"); return Task.FromResult(0); });
        var decay = new Neo4jMemoryDecayService(tx, TimeProvider.System, Options.Create(new MemoryDecayOptions()),
            NullLogger<Neo4jMemoryDecayService>.Instance, accessTracker: tracker);

        await decay.PruneExpiredMemoriesAsync();

        order.Should().NotBeEmpty().And.StartWith("flush");
        order.Should().Contain("prune");
    }

    [Fact]
    public async Task Without_a_tracker_both_read_as_before()
    {
        var tx = Substitute.For<INeo4jTransactionRunner>();
        tx.ReadAsync(Arg.Any<Func<global::Neo4j.Driver.IAsyncQueryRunner, Task<IReadOnlyList<MemoryHistoryRecord>>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MemoryHistoryRecord>>([]));

        var history = await new Neo4jMemoryHistoryService(tx).GetHistoryAsync(new MemoryHistoryQuery { Kind = MemoryHistoryKind.Fact, Id = "f1" });

        history.Should().BeEmpty();
    }
}
