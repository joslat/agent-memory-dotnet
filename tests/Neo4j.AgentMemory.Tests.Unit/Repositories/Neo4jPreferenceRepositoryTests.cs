using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.AgentMemory.Neo4j.Infrastructure;
using Neo4j.AgentMemory.Neo4j.Repositories;
using Neo4j.Driver;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.Repositories;

public sealed class Neo4jPreferenceRepositoryTests
{
    private static (Neo4jPreferenceRepository Repo, List<(string Cypher, object? Parameters)> Calls)
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
                runner
                    .RunAsync(Arg.Any<string>(), Arg.Any<object>())
                    .Returns(ci =>
                    {
                        calls.Add((ci.Arg<string>(), ci.ArgAt<object>(1)));
                        return Task.FromResult(Substitute.For<IResultCursor>());
                    });
                return work(runner);
            });
        return (new Neo4jPreferenceRepository(txRunner, NullLogger<Neo4jPreferenceRepository>.Instance), calls);
    }

    // ── DeleteAsync ──

    [Fact]
    public async Task DeleteAsync_SendsDetachDeleteCypher()
    {
        var (repo, calls) = CreateWriteCapture();

        await repo.DeleteAsync("pref-1");

        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Contain("DETACH DELETE p");
    }

    [Fact]
    public async Task DeleteAsync_PassesCorrectPreferenceId()
    {
        var (repo, calls) = CreateWriteCapture();

        await repo.DeleteAsync("pref-42");

        calls.Should().ContainSingle();
        var parameters = calls[0].Parameters;
        parameters.Should().NotBeNull();
        parameters!.GetType().GetProperty("id")!.GetValue(parameters).Should().Be("pref-42");
    }

    // ── CreateAboutRelationshipAsync ──

    [Fact]
    public async Task CreateAboutRelationshipAsync_SendsCorrectCypher()
    {
        var (repo, calls) = CreateWriteCapture();

        await repo.CreateAboutRelationshipAsync("pref-1", "ent-1");

        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Contain("MERGE (p)-[:ABOUT]->(e)");
    }

    [Fact]
    public async Task CreateAboutRelationshipAsync_PassesCorrectParameters()
    {
        var (repo, calls) = CreateWriteCapture();

        await repo.CreateAboutRelationshipAsync("pref-10", "ent-20");

        var parameters = calls[0].Parameters!;
        parameters.GetType().GetProperty("preferenceId")!.GetValue(parameters).Should().Be("pref-10");
        parameters.GetType().GetProperty("entityId")!.GetValue(parameters).Should().Be("ent-20");
    }

    // ── CreateExtractedFromRelationshipAsync ──

    [Fact]
    public async Task CreateExtractedFromRelationshipAsync_SendsCorrectCypher()
    {
        var (repo, calls) = CreateWriteCapture();

        await repo.CreateExtractedFromRelationshipAsync("pref-1", "msg-1");

        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Contain("MERGE (p)-[:EXTRACTED_FROM]->(m)");
    }

    [Fact]
    public async Task CreateExtractedFromRelationshipAsync_PassesCorrectParameters()
    {
        var (repo, calls) = CreateWriteCapture();

        await repo.CreateExtractedFromRelationshipAsync("pref-5", "msg-7");

        var parameters = calls[0].Parameters!;
        parameters.GetType().GetProperty("preferenceId")!.GetValue(parameters).Should().Be("pref-5");
        parameters.GetType().GetProperty("messageId")!.GetValue(parameters).Should().Be("msg-7");
    }
}
