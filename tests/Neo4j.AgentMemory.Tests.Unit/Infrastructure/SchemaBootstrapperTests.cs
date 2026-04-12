using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Neo4j.Infrastructure;
using Neo4j.Driver;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.Infrastructure;

public class SchemaBootstrapperTests
{
    private static SchemaBootstrapper CreateBootstrapper(
        INeo4jTransactionRunner txRunner,
        int embeddingDimensions = 1536)
    {
        var options = Options.Create(new Neo4jOptions { EmbeddingDimensions = embeddingDimensions });
        return new SchemaBootstrapper(txRunner, options, NullLogger<SchemaBootstrapper>.Instance);
    }

    [Fact]
    public async Task BootstrapAsync_ExecutesExpectedTotalNumberOfStatements()
    {
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        var executedStatements = new List<string>();

        txRunner
            .WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task>>();
                var fakeRunner = Substitute.For<IAsyncQueryRunner>();
                fakeRunner
                    .RunAsync(Arg.Any<string>())
                    .Returns(ci =>
                    {
                        executedStatements.Add(ci.Arg<string>());
                        return Task.FromResult(Substitute.For<IResultCursor>());
                    });
                return work(fakeRunner);
            });

        var bootstrapper = CreateBootstrapper(txRunner);
        await bootstrapper.BootstrapAsync();

        // 9 constraints + 3 fulltext + 6 vector + 9 property = 27
        executedStatements.Should().HaveCount(27);
    }

    [Fact]
    public async Task BootstrapAsync_ExecutesAllConstraints()
    {
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        var executedStatements = new List<string>();

        txRunner
            .WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task>>();
                var fakeRunner = Substitute.For<IAsyncQueryRunner>();
                fakeRunner.RunAsync(Arg.Any<string>()).Returns(ci =>
                {
                    executedStatements.Add(ci.Arg<string>());
                    return Task.FromResult(Substitute.For<IResultCursor>());
                });
                return work(fakeRunner);
            });

        var bootstrapper = CreateBootstrapper(txRunner);
        await bootstrapper.BootstrapAsync();

        var constraints = executedStatements.Where(s => s.StartsWith("CREATE CONSTRAINT")).ToList();
        constraints.Should().HaveCount(9);
        constraints.Should().Contain(s => s.Contains("conversation_id"));
        constraints.Should().Contain(s => s.Contains("message_id"));
        constraints.Should().Contain(s => s.Contains("entity_id"));
        constraints.Should().Contain(s => s.Contains("fact_id"));
        constraints.Should().Contain(s => s.Contains("preference_id"));
        constraints.Should().Contain(s => s.Contains("relationship_id"));
        constraints.Should().Contain(s => s.Contains("reasoning_trace_id"));
        constraints.Should().Contain(s => s.Contains("reasoning_step_id"));
        constraints.Should().Contain(s => s.Contains("tool_call_id"));
    }

    [Fact]
    public async Task BootstrapAsync_ExecutesAllFulltextIndexes()
    {
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        var executedStatements = new List<string>();

        txRunner
            .WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task>>();
                var fakeRunner = Substitute.For<IAsyncQueryRunner>();
                fakeRunner.RunAsync(Arg.Any<string>()).Returns(ci =>
                {
                    executedStatements.Add(ci.Arg<string>());
                    return Task.FromResult(Substitute.For<IResultCursor>());
                });
                return work(fakeRunner);
            });

        var bootstrapper = CreateBootstrapper(txRunner);
        await bootstrapper.BootstrapAsync();

        var fulltextIndexes = executedStatements.Where(s => s.StartsWith("CREATE FULLTEXT INDEX")).ToList();
        fulltextIndexes.Should().HaveCount(3);
        fulltextIndexes.Should().Contain(s => s.Contains("message_content"));
        fulltextIndexes.Should().Contain(s => s.Contains("entity_name"));
        fulltextIndexes.Should().Contain(s => s.Contains("fact_content"));
    }

    [Fact]
    public async Task BootstrapAsync_ExecutesAllVectorIndexes()
    {
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        var executedStatements = new List<string>();

        txRunner
            .WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task>>();
                var fakeRunner = Substitute.For<IAsyncQueryRunner>();
                fakeRunner.RunAsync(Arg.Any<string>()).Returns(ci =>
                {
                    executedStatements.Add(ci.Arg<string>());
                    return Task.FromResult(Substitute.For<IResultCursor>());
                });
                return work(fakeRunner);
            });

        var bootstrapper = CreateBootstrapper(txRunner);
        await bootstrapper.BootstrapAsync();

        var vectorIndexes = executedStatements.Where(s => s.StartsWith("CREATE VECTOR INDEX")).ToList();
        vectorIndexes.Should().HaveCount(6);
        vectorIndexes.Should().Contain(s => s.Contains("message_embedding_idx"));
        vectorIndexes.Should().Contain(s => s.Contains("entity_embedding_idx"));
        vectorIndexes.Should().Contain(s => s.Contains("preference_embedding_idx"));
        vectorIndexes.Should().Contain(s => s.Contains("fact_embedding_idx"));
        vectorIndexes.Should().Contain(s => s.Contains("reasoning_step_embedding_idx"));
        vectorIndexes.Should().Contain(s => s.Contains("task_embedding_idx"));
    }

    [Fact]
    public async Task BootstrapAsync_ExecutesAllPropertyIndexes()
    {
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        var executedStatements = new List<string>();

        txRunner
            .WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task>>();
                var fakeRunner = Substitute.For<IAsyncQueryRunner>();
                fakeRunner.RunAsync(Arg.Any<string>()).Returns(ci =>
                {
                    executedStatements.Add(ci.Arg<string>());
                    return Task.FromResult(Substitute.For<IResultCursor>());
                });
                return work(fakeRunner);
            });

        var bootstrapper = CreateBootstrapper(txRunner);
        await bootstrapper.BootstrapAsync();

        var propertyIndexes = executedStatements
            .Where(s => s.StartsWith("CREATE INDEX"))
            .ToList();
        propertyIndexes.Should().HaveCount(9);
        propertyIndexes.Should().Contain(s => s.Contains("message_session_id"));
        propertyIndexes.Should().Contain(s => s.Contains("message_timestamp"));
        propertyIndexes.Should().Contain(s => s.Contains("entity_type"));
        propertyIndexes.Should().Contain(s => s.Contains("entity_name_prop"));
        propertyIndexes.Should().Contain(s => s.Contains("fact_category"));
        propertyIndexes.Should().Contain(s => s.Contains("preference_category"));
        propertyIndexes.Should().Contain(s => s.Contains("reasoning_trace_session_id"));
        propertyIndexes.Should().Contain(s => s.Contains("reasoning_step_timestamp"));
        propertyIndexes.Should().Contain(s => s.Contains("tool_call_status"));
    }

    [Theory]
    [InlineData(1536)]
    [InlineData(3072)]
    [InlineData(768)]
    public void BuildVectorIndexes_EmbeddingDimensionAppearsInAllIndexes(int dimensions)
    {
        var indexes = SchemaBootstrapper.BuildVectorIndexes(dimensions);

        indexes.Should().HaveCount(6);
        indexes.Should().AllSatisfy(idx =>
            idx.Should().Contain($"`vector.dimensions`: {dimensions}"));
    }

    [Fact]
    public void BuildVectorIndexes_AllIndexesUseCosineFunction()
    {
        var indexes = SchemaBootstrapper.BuildVectorIndexes(1536);

        indexes.Should().AllSatisfy(idx =>
            idx.Should().Contain("'cosine'"));
    }

    [Fact]
    public void BuildVectorIndexes_AllIndexesTargetEmbeddingProperty()
    {
        var indexes = SchemaBootstrapper.BuildVectorIndexes(1536);

        indexes.Should().AllSatisfy(idx =>
            idx.Should().MatchRegex(@"ON \(n\.(embedding|taskEmbedding)\)"));
    }

    [Fact]
    public void BuildVectorIndexes_AllIndexesAreIdempotent()
    {
        var indexes = SchemaBootstrapper.BuildVectorIndexes(1536);

        indexes.Should().AllSatisfy(idx =>
            idx.Should().Contain("IF NOT EXISTS"));
    }

    [Fact]
    public void Neo4jOptions_DefaultEmbeddingDimensionsIs1536()
    {
        var options = new Neo4jOptions();

        options.EmbeddingDimensions.Should().Be(1536);
    }

    [Fact]
    public async Task BootstrapAsync_VectorIndexesUseConfiguredDimensions()
    {
        const int customDimensions = 3072;
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        var executedStatements = new List<string>();

        txRunner
            .WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task>>();
                var fakeRunner = Substitute.For<IAsyncQueryRunner>();
                fakeRunner.RunAsync(Arg.Any<string>()).Returns(ci =>
                {
                    executedStatements.Add(ci.Arg<string>());
                    return Task.FromResult(Substitute.For<IResultCursor>());
                });
                return work(fakeRunner);
            });

        var bootstrapper = CreateBootstrapper(txRunner, embeddingDimensions: customDimensions);
        await bootstrapper.BootstrapAsync();

        var vectorIndexes = executedStatements.Where(s => s.StartsWith("CREATE VECTOR INDEX")).ToList();
        vectorIndexes.Should().HaveCount(6);
        vectorIndexes.Should().AllSatisfy(idx =>
            idx.Should().Contain($"`vector.dimensions`: {customDimensions}"));
    }
}
