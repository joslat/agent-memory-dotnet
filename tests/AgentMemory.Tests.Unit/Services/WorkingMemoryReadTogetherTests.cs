using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.Driver;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// DRT-1: a profile-block rebuild starts its independent reads together, outside a write unit only. Inside one (whose
/// one transaction runs one statement at a time), or with a runner that cannot tell, they run one after another.
/// </summary>
public sealed class WorkingMemoryReadTogetherTests
{
    /// <summary>Answers every read with nothing after a short wait, and records how many were in flight at once.</summary>
    private sealed class Recording(bool? inWriteUnit) : INeo4jTransactionRunner
    {
        private int _inFlight;
        public int MostAtOnce;
        public int Reads;

        public bool? InWriteUnit => inWriteUnit;

        public async Task<T> ReadAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Reads);
            var now = Interlocked.Increment(ref _inFlight);
            lock (this) MostAtOnce = Math.Max(MostAtOnce, now);
            await Task.Delay(40, cancellationToken);
            Interlocked.Decrement(ref _inFlight);
            return default!;
        }

        public Task ReadAsync(Func<IAsyncQueryRunner, Task> work, CancellationToken cancellationToken = default) =>
            ReadAsync<bool>(async _ => { await Task.CompletedTask; return true; }, cancellationToken);

        public Task<T> WriteAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default) =>
            Task.FromResult(default(T)!);

        public Task WriteAsync(Func<IAsyncQueryRunner, Task> work, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static Neo4jWorkingMemoryService Service(INeo4jTransactionRunner runner, bool readTogether = true)
    {
        var options = new MemoryOptions();
        options.WorkingMemory.Enabled = true;
        options.WorkingMemory.RecentTopicsDays = 7;
        options.WorkingMemory.ReadTogether = readTogether;
        return new Neo4jWorkingMemoryService(runner, TimeProvider.System, Substitute.For<IIdGenerator>(),
            Options.Create(options), NullLogger<Neo4jWorkingMemoryService>.Instance);
    }

    [Fact]
    public async Task Outside_a_write_unit_the_six_reads_start_together()
    {
        var runner = new Recording(inWriteUnit: false);

        await Service(runner).RebuildAsync("owner-1");

        runner.Reads.Should().Be(6, "facts, preferences, entities, recent topics, the boundary and the stored hash");
        runner.MostAtOnce.Should().Be(6);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public async Task Inside_a_write_unit_or_when_the_runner_cannot_say_they_run_one_after_another(bool? inWriteUnit)
    {
        var runner = new Recording(inWriteUnit);

        await Service(runner).RebuildAsync("owner-1");

        runner.Reads.Should().Be(6);
        runner.MostAtOnce.Should().Be(1);
    }

    [Fact]
    public async Task Off_is_the_previous_path_one_read_after_another()
    {
        var runner = new Recording(inWriteUnit: false);

        await Service(runner, readTogether: false).RebuildAsync("owner-1");

        runner.MostAtOnce.Should().Be(1);
    }
}
