using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Queries;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Unit.Resolution;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.Driver;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Repositories;

public sealed class IdentityRelationshipBoundaryTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Disabled_and_cancelled_original_relationship_calls_do_not_open_storage(bool cancelled)
    {
        var tx = Substitute.For<INeo4jTransactionRunner>(); var options = new Neo4jOptions();
        if(cancelled) options.Extensions.Add("identity-evidence");
        var repository = new Neo4jIdentityDecisionRepository(tx,Options.Create(options));
        var token = new CancellationToken(cancelled);
        Func<Task> save = () => repository.SaveRelationshipObservationAsync(IdentityRelationshipPayloadTests.Input(),"pablito",cancellationToken:token);
        Func<Task> read = () => repository.GetRelationshipObservationAsync("id","pablito",token);
        if(cancelled)
        { await save.Should().ThrowAsync<OperationCanceledException>(); await read.Should().ThrowAsync<OperationCanceledException>(); }
        else
        { await save.Should().ThrowAsync<InvalidOperationException>(); await read.Should().ThrowAsync<InvalidOperationException>(); }
        tx.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Invalid_original_relationship_owner_fails_before_any_storage_call()
    {
        var tx = Substitute.For<INeo4jTransactionRunner>(); var options = new Neo4jOptions(); options.Extensions.Add("identity-evidence");
        var repository = new Neo4jIdentityDecisionRepository(tx,Options.Create(options));
        var save = () => repository.SaveRelationshipObservationAsync(IdentityRelationshipPayloadTests.Input(),"other");
        await save.Should().ThrowAsync<ArgumentException>(); tx.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Off_read_path_uses_the_exact_legacy_query_even_with_the_identity_schema_enabled(bool configure)
    {
        var tx = Substitute.For<INeo4jTransactionRunner>(); var calls = new List<string>();
        tx.ReadAsync(Arg.Any<Func<IAsyncQueryRunner,Task<List<Relationship>>>>(),Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var runner = Substitute.For<IAsyncQueryRunner>();
            runner.RunAsync(Arg.Any<string>(),Arg.Any<IDictionary<string,object>>()).Returns(info =>
            { calls.Add(info.Arg<string>()); var cursor = Substitute.For<IResultCursor>(); cursor.FetchAsync().Returns(false); return cursor; });
            return await call.Arg<Func<IAsyncQueryRunner,Task<List<Relationship>>>>()(runner);
        });
        var schema = new Neo4jOptions(); schema.Extensions.Add("identity-evidence");
        var repository = new Neo4jRelationshipRepository(tx,NullLogger<Neo4jRelationshipRepository>.Instance,
            identity:configure?Options.Create(new IdentityResolutionOptions { Enabled=false }):null,neo4j:Options.Create(schema));
        await repository.GetBySourceEntityAsync("source",MemoryScope.For("pablito",false));
        calls.Should().ContainSingle().Which.Should().Be(RelationshipQueries.GetBySourceEntity(true,false));
    }

    [Fact]
    public void Enabled_projection_requires_its_explicit_schema_without_connecting_to_storage()
    {
        var tx = Substitute.For<INeo4jTransactionRunner>();
        Action create = () => new Neo4jRelationshipRepository(tx,NullLogger<Neo4jRelationshipRepository>.Instance,
            identity:Options.Create(new IdentityResolutionOptions { Enabled=true }),neo4j:Options.Create(new Neo4jOptions()));
        create.Should().Throw<InvalidOperationException>(); tx.ReceivedCalls().Should().BeEmpty();
    }
}
