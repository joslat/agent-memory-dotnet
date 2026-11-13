using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Repositories;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Neo4j.Driver;

namespace AgentMemory.Tests.Unit.Repositories;

public sealed class EventDetailBoundaryTests
{
    private static (Neo4jFactRepository Repository, INeo4jTransactionRunner Runner) Create(bool enabled = true)
    {
        var tx = Substitute.For<INeo4jTransactionRunner>(); var options = new Neo4jOptions();
        if (enabled) options.Extensions.Add("identity-evidence");
        return (new(tx, NullLogger<Neo4jFactRepository>.Instance, neo4jOptions: Options.Create(options)), tx);
    }

    [Theory]
    [InlineData(-1)] [InlineData(65)]
    public async Task Out_of_range_caps_fail_before_a_query(int limit)
    {
        var (repository, tx) = Create();
        var action = () => repository.GetEventDetailsAsync(["seed"], [], limit, MemoryScope.For("owner", false));
        await action.Should().ThrowAsync<ArgumentOutOfRangeException>(); tx.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Empty_seeds_and_zero_cap_do_not_start_a_transaction(bool noSeeds)
    {
        var (repository, tx) = Create(false);
        (await repository.GetEventDetailsAsync(noSeeds ? [] : ["seed"], [], noSeeds ? 8 : 0, null)).Should().BeEmpty();
        tx.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_schema_is_explicit_instead_of_returning_silent_empty_success()
    {
        var (repository, tx) = Create(false);
        var action = () => repository.GetEventDetailsAsync(["seed"], [], 8, null);
        await action.Should().ThrowAsync<NotSupportedException>(); tx.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Oversized_seed_or_exclusion_sets_are_rejected_before_any_query()
    {
        var (repository, tx) = Create();
        var seeds = () => repository.GetEventDetailsAsync(Enumerable.Range(0, 11).Select(i => "s" + i).ToArray(), [], 8, null);
        var excluded = () => repository.GetEventDetailsAsync(["seed"], Enumerable.Range(0, 513).Select(i => "x" + i).ToArray(), 8, null);
        await seeds.Should().ThrowAsync<ArgumentOutOfRangeException>(); await excluded.Should().ThrowAsync<ArgumentOutOfRangeException>();
        tx.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Caller_cancellation_does_not_open_a_transaction()
    {
        var (repository, tx) = Create();
        var action = () => repository.GetEventDetailsAsync(["seed"], [], 8, null, new CancellationToken(true));
        await action.Should().ThrowAsync<OperationCanceledException>(); tx.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(null, false)] [InlineData("pablito", false)] [InlineData("pablito", true)] [InlineData("*", false)]
    public async Task Scope_parameters_preserve_global_and_tenant_read_conventions(string? owner, bool shared)
    {
        var (repository, tx) = Create(); IDictionary<string, object>? captured = null;
        tx.ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<IReadOnlyList<Fact>>>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var runner = Substitute.For<IAsyncQueryRunner>();
                runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object>>()).Returns(c =>
                {
                    captured = c.ArgAt<IDictionary<string, object>>(1);
                    var cursor = Substitute.For<IResultCursor>(); cursor.FetchAsync().Returns(false); return cursor;
                });
                return await call.Arg<Func<IAsyncQueryRunner, Task<IReadOnlyList<Fact>>>>()(runner);
            });
        await repository.GetEventDetailsAsync(["seed"], [], 8, owner is null ? null : MemoryScope.For(owner, shared));
        captured.Should().NotBeNull();
        captured!["hasOwner"].Should().Be(owner is not null);
        captured["ownerId"].Should().Be(owner);
        captured["includeShared"].Should().Be(owner is not null && shared);
    }

}
