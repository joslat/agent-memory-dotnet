using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Repositories;
using Neo4j.Driver;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Repositories;

/// <summary>
/// cycle-3 invariant: a node either has a real (Length &gt; 0) embedding or its <c>embedding</c> property
/// is NULL — never a non-null zero-length vector. A zero-length embedding would make
/// <c>embedding IS NULL</c> false and permanently strand the node out of the back-fill self-heal, leaving
/// it un-searchable forever. <c>UpdateEmbeddingAsync</c> must therefore skip the write when handed an
/// empty array (e.g. a back-fill run that itself hit a transient embedding failure).
/// </summary>
public sealed class Neo4jEmbeddingInvariantTests
{
    private static (INeo4jTransactionRunner Runner, List<(string Cypher, object? Parameters)> Calls) CreateWriteCapture()
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
        return (txRunner, calls);
    }

    [Fact]
    public async Task Entity_UpdateEmbeddingAsync_EmptyVector_WritesNothing()
    {
        var (runner, calls) = CreateWriteCapture();
        var repo = new Neo4jEntityRepository(runner, NullLogger<Neo4jEntityRepository>.Instance);

        await repo.UpdateEmbeddingAsync("e-1", Array.Empty<float>());

        calls.Should().BeEmpty("an empty embedding must leave the node's `embedding` NULL and re-queueable");
    }

    [Fact]
    public async Task Entity_UpdateEmbeddingAsync_RealVector_WritesEmbedding()
    {
        var (runner, calls) = CreateWriteCapture();
        var repo = new Neo4jEntityRepository(runner, NullLogger<Neo4jEntityRepository>.Instance);

        await repo.UpdateEmbeddingAsync("e-1", new float[] { 0.1f, 0.2f });

        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Contain("embedding");
    }

    [Fact]
    public async Task Fact_UpdateEmbeddingAsync_EmptyVector_WritesNothing()
    {
        var (runner, calls) = CreateWriteCapture();
        var repo = new Neo4jFactRepository(runner, NullLogger<Neo4jFactRepository>.Instance);

        await repo.UpdateEmbeddingAsync("f-1", Array.Empty<float>());

        calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Fact_UpdateEmbeddingAsync_RealVector_WritesEmbedding()
    {
        var (runner, calls) = CreateWriteCapture();
        var repo = new Neo4jFactRepository(runner, NullLogger<Neo4jFactRepository>.Instance);

        await repo.UpdateEmbeddingAsync("f-1", new float[] { 0.3f });

        calls.Should().ContainSingle();
    }

    [Fact]
    public async Task Preference_UpdateEmbeddingAsync_EmptyVector_WritesNothing()
    {
        var (runner, calls) = CreateWriteCapture();
        var repo = new Neo4jPreferenceRepository(runner, NullLogger<Neo4jPreferenceRepository>.Instance);

        await repo.UpdateEmbeddingAsync("p-1", Array.Empty<float>());

        calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Preference_UpdateEmbeddingAsync_RealVector_WritesEmbedding()
    {
        var (runner, calls) = CreateWriteCapture();
        var repo = new Neo4jPreferenceRepository(runner, NullLogger<Neo4jPreferenceRepository>.Instance);

        await repo.UpdateEmbeddingAsync("p-1", new float[] { 0.4f });

        calls.Should().ContainSingle();
    }
}
