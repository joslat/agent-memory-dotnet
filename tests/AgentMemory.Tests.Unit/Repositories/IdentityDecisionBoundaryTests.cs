using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Unit.Resolution;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Repositories;

public sealed class IdentityDecisionBoundaryTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Disabled_or_cancelled_decisions_issue_no_queries(bool cancelled)
    {
        var runner = Substitute.For<INeo4jTransactionRunner>(); var options = new Neo4jOptions();
        if (cancelled) options.Extensions.Add("identity-evidence");
        var repository = new Neo4jIdentityDecisionRepository(runner, Options.Create(options));
        var save = () => repository.SaveAsync(IdentityDecisionPayloadTests.Input(), "pablito", new CancellationToken(cancelled));
        var read = () => repository.GetAsync("decision", "pablito", new CancellationToken(cancelled));
        if (cancelled) { await save.Should().ThrowAsync<OperationCanceledException>(); await read.Should().ThrowAsync<OperationCanceledException>(); }
        else { await save.Should().ThrowAsync<InvalidOperationException>(); await read.Should().ThrowAsync<InvalidOperationException>(); }
        runner.ReceivedCalls().Should().BeEmpty();
    }
    [Fact]
    public async Task Invalid_scope_fails_before_opening_the_write_transaction()
    {
        var runner = Substitute.For<INeo4jTransactionRunner>(); var options = new Neo4jOptions(); options.Extensions.Add("identity-evidence");
        var repository = new Neo4jIdentityDecisionRepository(runner, Options.Create(options));
        var save = () => repository.SaveAsync(IdentityDecisionPayloadTests.Input(), "other");
        await save.Should().ThrowAsync<ArgumentException>(); runner.ReceivedCalls().Should().BeEmpty();
    }
}
