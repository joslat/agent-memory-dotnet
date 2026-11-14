using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Repositories;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Repositories;

/// <summary>ILC-33: the person reads are bounded and refuse a store without identity storage before any query.</summary>
public sealed class PersonDetailBoundaryTests
{
    private static (Neo4jFactRepository Repository, INeo4jTransactionRunner Runner) Create(bool enabled = true)
    {
        var tx = Substitute.For<INeo4jTransactionRunner>(); var options = new Neo4jOptions();
        if (enabled) options.Extensions.Add("identity-evidence");
        return (new(tx, NullLogger<Neo4jFactRepository>.Instance, neo4jOptions: Options.Create(options)), tx);
    }

    [Fact]
    public async Task Out_of_range_inputs_fail_before_a_query()
    {
        var (repository, tx) = Create();
        await repository.Invoking(r => r.GetPersonIdentitiesAsync(1001, null)).Should().ThrowAsync<ArgumentOutOfRangeException>();
        await repository.Invoking(r => r.GetPersonDetailsAsync(["a", "b", "c", "d"], [], 8, null)).Should().ThrowAsync<ArgumentOutOfRangeException>();
        await repository.Invoking(r => r.GetPersonDetailsAsync(["a"], [], 65, null)).Should().ThrowAsync<ArgumentOutOfRangeException>();
        await repository.Invoking(r => r.GetPersonDetailsAsync(["a"], Enumerable.Range(0, 513).Select(i => "x" + i).ToArray(), 8, null))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
        await repository.Invoking(r => r.GetPersonDetailsAsync([" "], [], 8, null)).Should().ThrowAsync<ArgumentException>();
        tx.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Nothing_asked_starts_no_transaction_even_without_the_schema()
    {
        var (repository, tx) = Create(false);
        (await repository.GetPersonIdentitiesAsync(0, null)).Should().BeEmpty();
        (await repository.GetPersonDetailsAsync([], [], 8, null)).Should().BeEmpty();
        (await repository.GetPersonDetailsAsync(["a"], [], 0, null)).Should().BeEmpty();
        tx.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_schema_is_explicit_instead_of_returning_silent_empty_success()
    {
        var (repository, tx) = Create(false);
        await repository.Invoking(r => r.GetPersonIdentitiesAsync(1000, null)).Should().ThrowAsync<NotSupportedException>();
        await repository.Invoking(r => r.GetPersonDetailsAsync(["a"], [], 8, null)).Should().ThrowAsync<NotSupportedException>();
        tx.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Caller_cancellation_does_not_open_a_transaction()
    {
        var (repository, tx) = Create();
        await repository.Invoking(r => r.GetPersonIdentitiesAsync(1000, null, new CancellationToken(true))).Should().ThrowAsync<OperationCanceledException>();
        await repository.Invoking(r => r.GetPersonDetailsAsync(["a"], [], 8, null, new CancellationToken(true))).Should().ThrowAsync<OperationCanceledException>();
        tx.ReceivedCalls().Should().BeEmpty();
    }
}
