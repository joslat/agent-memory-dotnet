#pragma warning disable AMEXT001
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j.Storage;
using AgentMemory.Extensibility.Reading;
using AgentMemory.Neo4j.Infrastructure;
using FluentAssertions;
using Neo4j.Driver;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// Review 7, W3: the walk's time budget reaches a Neo4j statement in progress. Each traversal read carries the time the walk has
/// left, the store sets it as the transaction's server-side timeout, and the server stopping a statement there is a stop by time.
/// </summary>
public sealed class TraversalDeadlineTests
{
    private const string Code = """
        {"format":"agentmemory.module/v1alpha1","id":"com.example.code","version":"1.0.0","sdk":"[0.1,0.2)",
         "ontology":{"labelPrefix":"Code","types":[{"name":"CodeFile","scope":"shared","render":"{path}","properties":{"path":{"type":"string","required":true}}}],
          "relations":[{"name":"CODE_USES","from":"CodeFile","to":"CodeFile"}]},
         "traversal":{"anchors":[{"type":"CodeFile","property":"path","match":"prefix"}],"relations":[{"name":"CODE_USES","direction":"out","weight":0.5,"fanOut":3}],"maxItems":3}}
        """;

    private static readonly ModuleReadContext Context = new() { Scope = MemoryScope.For("pack") };

    private static Neo4jModuleTraversalReader Reader(INeo4jTransactionRunner runner)
    {
        var resolution = ModuleResolver.Resolve([ModuleManifest.Parse(Code)], ModuleSet.SdkVersion, ModuleHostFeatures.Known);
        var policy = Substitute.For<IMemoryIsolationPolicy>();
        policy.ResolveReadScope(Arg.Any<MemoryScope?>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<MemoryOperationAccess>())
            .Returns(call => call.ArgAt<MemoryScope?>(0) ?? MemoryScope.For("pack"));
        return new Neo4jModuleTraversalReader(runner, resolution, policy);
    }

    private static ModuleAnchorQuery Match(TimeSpan? budget) => new()
    {
        ModuleId = "com.example.code", Type = "CodeFile", Property = "path", Match = ModuleAnchorMatch.Prefix, Values = ["src/a.cs"], Budget = budget,
    };

    [Fact]
    public async Task A_read_with_a_budget_runs_under_that_server_deadline_and_one_without_runs_as_before()
    {
        var runner = new Recording();

        await Reader(runner).MatchAsync(Match(TimeSpan.FromMilliseconds(1500)), Context);
        await Reader(runner).ExpandAsync(new ModuleExpandQuery
        {
            ModuleId = "com.example.code", Relation = "CODE_USES", Direction = ModuleTraversalDirection.Out, FromIds = ["x"], Budget = TimeSpan.FromSeconds(2),
        }, Context);
        await Reader(runner).MatchAsync(Match(null), Context);

        runner.Timeouts.Should().Equal(TimeSpan.FromMilliseconds(1500), TimeSpan.FromSeconds(2), null);
    }

    [Fact]
    public async Task The_server_stopping_a_statement_at_the_deadline_is_a_stop_by_time()
    {
        var runner = new Recording { Throw = new ClientException("Neo.ClientError.Transaction.TransactionTimedOutClientConfiguration", "timed out") };

        await FluentActions.Awaiting(() => Reader(runner).MatchAsync(Match(TimeSpan.FromMilliseconds(5)), Context))
            .Should().ThrowAsync<OperationCanceledException>().WithMessage("*time budget ended in the store*");
    }

    // Records which read each call took, and with what deadline; runs no work.
    private sealed class Recording : INeo4jTransactionRunner
    {
        public List<TimeSpan?> Timeouts { get; } = [];
        public Exception? Throw { get; init; }

        public Task<T> ReadAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default)
        {
            Timeouts.Add(null);
            return Throw is null ? Task.FromResult(default(T)!) : Task.FromException<T>(Throw);
        }

        public Task<T> ReadAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Timeouts.Add(timeout);
            return Throw is null ? Task.FromResult(default(T)!) : Task.FromException<T>(Throw);
        }

        public Task ReadAsync(Func<IAsyncQueryRunner, Task> work, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<T> WriteAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default) => Task.FromResult(default(T)!);
        public Task WriteAsync(Func<IAsyncQueryRunner, Task> work, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
