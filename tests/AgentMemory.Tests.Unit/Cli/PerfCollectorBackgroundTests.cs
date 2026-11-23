using System.Diagnostics;
using AgentMemory.Abstractions.Diagnostics;
using AgentMemory.Cli.Perf;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Cli;

/// <summary>
/// PERF-R07-ORDER: work a turn starts in the background (the owner's row counts, counted again once 30 s have passed)
/// runs in the turn's async context but is not the turn's. Its spans already stay out of the turn; the harness's own
/// counters, which no span carries, must stay out too.
/// </summary>
public sealed class PerfCollectorBackgroundTests
{
    [Fact]
    public void A_harness_counter_raised_under_a_background_span_is_not_charged_to_the_turn()
    {
        using var collector = new PerfCollector();
        using var turn = collector.BeginTurn("test", 0, "measure");

        PerfCollector.ChargeToTurn("injected.database_delay.calls");
        using (AgentMemoryDiagnostics.Source.StartActivity("memory.background.owner_counts"))
        using (AgentMemoryDiagnostics.Source.StartActivity("memory.db.tx"))
            PerfCollector.ChargeToTurn("injected.database_delay.calls");

        turn.Record.Counter("injected.database_delay.calls").Should().Be(1, "only the turn's own transaction waited for it");
    }

    /// <summary>
    /// The product starts a query span with its transaction's context as the parent (Neo4jTransactionRunner's
    /// CountingQueryRunner), not the transaction Activity itself, so the span's Parent chain ends at the span.
    /// </summary>
    [Fact]
    public void A_query_of_a_background_transaction_is_not_charged_to_the_turn()
    {
        using var collector = new PerfCollector();
        using var turn = collector.BeginTurn("test", 0, "measure");

        using (AgentMemoryDiagnostics.Source.StartActivity("memory.background.owner_counts"))
        using (var tx = AgentMemoryDiagnostics.Source.StartActivity("memory.db.tx"))
        using (AgentMemoryDiagnostics.Source.StartActivity("memory.db.query", ActivityKind.Client, tx!.Context)) { }
        using (var tx = AgentMemoryDiagnostics.Source.StartActivity("memory.db.tx"))
        using (AgentMemoryDiagnostics.Source.StartActivity("memory.db.query", ActivityKind.Client, tx!.Context)) { }

        turn.Record.Counter("neo4j.queries").Should().Be(1, "the background count's query is not the turn's");
        turn.Record.Counter("neo4j.tx.read").Should().Be(1);
    }

    /// <summary>
    /// Only the read notes a turn queued are its background work (counted while its notes are flushed). The owner counts
    /// refreshed once 30 s have passed belong to no turn: counted wherever a flush happened to be open, they made the
    /// perf gate's background counters change from run to run (PR #274's CI: background.neo4j.queries 1 to 3).
    /// </summary>
    [Fact]
    public void A_timed_background_refresh_is_not_counted_with_the_read_notes_being_flushed()
    {
        using var collector = new PerfCollector();
        TurnRecord record;
        using (var turn = collector.BeginTurn("test", 0, "measure")) record = turn.Record;

        using (collector.BackgroundFor(record))
        {
            using (AgentMemoryDiagnostics.Source.StartActivity("memory.background.access_tracking"))
            using (var tx = AgentMemoryDiagnostics.Source.StartActivity("memory.db.tx"))
            using (AgentMemoryDiagnostics.Source.StartActivity("memory.db.query", ActivityKind.Client, tx!.Context)) { }
            using (AgentMemoryDiagnostics.Source.StartActivity("memory.background.owner_counts"))
            using (var tx = AgentMemoryDiagnostics.Source.StartActivity("memory.db.tx"))
            using (AgentMemoryDiagnostics.Source.StartActivity("memory.db.query", ActivityKind.Client, tx!.Context)) { }
        }

        record.Counter("background.neo4j.queries").Should().Be(1, "only the read notes' query is the turn's background work");
        record.Counter("background.neo4j.tx.read").Should().Be(1);
        record.Counter("background.access_tracking.writes").Should().Be(1);
    }

    /// <summary>
    /// A read-note write is counted whole, where its root span ends. Its statements end as they are sent, long before the
    /// commit; counted one by one, those sent before the harness opened its flush window were lost, so the gate's
    /// background.neo4j.queries read 1 to 3 from run to run (PR #274's CI, PERF-R-07 in the remote profile).
    /// </summary>
    [Fact]
    public void A_read_note_write_whose_statements_ended_before_the_flush_is_counted_whole()
    {
        using var collector = new PerfCollector();
        TurnRecord record;
        using (var turn = collector.BeginTurn("test", 0, "measure")) record = turn.Record;

        var root = AgentMemoryDiagnostics.Source.StartActivity("memory.background.access_tracking");
        var tx = AgentMemoryDiagnostics.Source.StartActivity("memory.db.tx");
        for (var statement = 0; statement < 3; statement++)
            using (AgentMemoryDiagnostics.Source.StartActivity("memory.db.query", ActivityKind.Client, tx!.Context)) { }
        using (collector.BackgroundFor(record))
        {
            tx!.Dispose();
            root!.Dispose();
        }

        record.Counter("background.neo4j.queries").Should().Be(3, "the write's three statements belong to it, whenever each was sent");
        record.Counter("background.neo4j.tx.read").Should().Be(1);
        record.Counter("background.access_tracking.writes").Should().Be(1);
    }

    /// <summary>And a write that ends outside any flush window is counted nowhere, its statements included.</summary>
    [Fact]
    public void A_read_note_write_that_ends_outside_a_flush_is_not_counted_in_part()
    {
        using var collector = new PerfCollector();
        TurnRecord record;
        using (var turn = collector.BeginTurn("test", 0, "measure")) record = turn.Record;

        var root = AgentMemoryDiagnostics.Source.StartActivity("memory.background.access_tracking");
        using (collector.BackgroundFor(record))
        {
            using var tx = AgentMemoryDiagnostics.Source.StartActivity("memory.db.tx");
            using (AgentMemoryDiagnostics.Source.StartActivity("memory.db.query", ActivityKind.Client, tx!.Context)) { }
        }
        root!.Dispose();

        record.Counter("background.neo4j.queries").Should().Be(0);
        record.Counter("background.neo4j.tx.read").Should().Be(0);
        record.Counter("background.access_tracking.writes").Should().Be(0);
    }
}
