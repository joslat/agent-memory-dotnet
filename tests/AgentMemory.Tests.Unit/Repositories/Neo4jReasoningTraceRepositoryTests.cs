using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Repositories;
using Neo4j.Driver;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Repositories;

public sealed class Neo4jReasoningTraceRepositoryTests
{
    private static (Neo4jReasoningTraceRepository Repo, List<(string Cypher, object? Parameters)> Calls)
        CreateWriteCapture()
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        txRunner
            .WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task>>();
                var runner = Substitute.For<IAsyncQueryRunner>();
                // Capture BOTH RunAsync overloads: relationship writers pass an anonymous object (object
                // overload); DeleteBySession passes a Dictionary<string,object> which binds the IDictionary
                // overload. Without the latter, a dictionary-param call is silently not captured.
                runner
                    .RunAsync(Arg.Any<string>(), Arg.Any<object>())
                    .Returns(ci =>
                    {
                        calls.Add((ci.Arg<string>(), ci.ArgAt<object>(1)));
                        return Task.FromResult(Substitute.For<IResultCursor>());
                    });
                runner
                    .RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object>>())
                    .Returns(ci =>
                    {
                        calls.Add((ci.Arg<string>(), ci.ArgAt<object>(1)));
                        return Task.FromResult(Substitute.For<IResultCursor>());
                    });
                return work(runner);
            });
        return (new Neo4jReasoningTraceRepository(txRunner, NullLogger<Neo4jReasoningTraceRepository>.Instance), calls);
    }

    // ── CreateInitiatedByRelationshipAsync ──

    [Fact]
    public async Task CreateInitiatedByRelationshipAsync_SendsCorrectCypher()
    {
        var (repo, calls) = CreateWriteCapture();

        await repo.CreateInitiatedByRelationshipAsync("trace-1", "msg-1");

        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Contain("MERGE (t)-[:INITIATED_BY]->(m)");
    }

    [Fact]
    public async Task CreateInitiatedByRelationshipAsync_PassesCorrectParameters()
    {
        var (repo, calls) = CreateWriteCapture();

        await repo.CreateInitiatedByRelationshipAsync("trace-5", "msg-9");

        var parameters = calls[0].Parameters!;
        parameters.GetType().GetProperty("traceId")!.GetValue(parameters).Should().Be("trace-5");
        parameters.GetType().GetProperty("messageId")!.GetValue(parameters).Should().Be("msg-9");
    }

    // ── CreateConversationTraceRelationshipsAsync ──

    [Fact]
    public async Task CreateConversationTraceRelationshipsAsync_SendsHasTraceCypher()
    {
        var (repo, calls) = CreateWriteCapture();

        await repo.CreateConversationTraceRelationshipsAsync("conv-1", "trace-1");

        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Contain("MERGE (c)-[:HAS_TRACE]->(t)");
    }

    [Fact]
    public async Task CreateConversationTraceRelationshipsAsync_SendsInSessionCypher()
    {
        var (repo, calls) = CreateWriteCapture();

        await repo.CreateConversationTraceRelationshipsAsync("conv-1", "trace-1");

        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Contain("MERGE (t)-[:IN_SESSION]->(c)");
    }

    [Fact]
    public async Task CreateConversationTraceRelationshipsAsync_PassesCorrectParameters()
    {
        var (repo, calls) = CreateWriteCapture();

        await repo.CreateConversationTraceRelationshipsAsync("conv-10", "trace-20");

        var parameters = calls[0].Parameters!;
        parameters.GetType().GetProperty("conversationId")!.GetValue(parameters).Should().Be("conv-10");
        parameters.GetType().GetProperty("traceId")!.GetValue(parameters).Should().Be("trace-20");
    }

    // ── DeleteBySessionAsync ──

    [Fact]
    public async Task DeleteBySessionAsync_NullOwner_DeletesSharedBucketOnly()
    {
        var (repo, calls) = CreateWriteCapture();

        await repo.DeleteBySessionAsync("session-42");

        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Contain("DETACH DELETE t, s");
        // R6-C: a null-owner clear confines to the shared bucket (owner_id IS NULL), never all owners.
        calls[0].Cypher.Should().Contain("t.owner_id IS NULL").And.NotContain("t.owner_id = $ownerId");
        var parameters = (IDictionary<string, object>)calls[0].Parameters!;
        parameters["sessionId"].Should().Be("session-42");
        parameters.Should().NotContainKey("ownerId");
    }

    [Fact]
    public async Task DeleteBySessionAsync_WithOwner_ScopesToThatOwner()
    {
        var (repo, calls) = CreateWriteCapture();

        await repo.DeleteBySessionAsync("session-42", ownerId: "alice");

        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Contain("DETACH DELETE t, s");
        // Confines to the owner's own bucket — never the shared-only or all-owners clause.
        calls[0].Cypher.Should().Contain("t.owner_id = $ownerId").And.NotContain("t.owner_id IS NULL");
        var parameters = (IDictionary<string, object>)calls[0].Parameters!;
        parameters["sessionId"].Should().Be("session-42");
        parameters["ownerId"].Should().Be("alice");
    }
}
