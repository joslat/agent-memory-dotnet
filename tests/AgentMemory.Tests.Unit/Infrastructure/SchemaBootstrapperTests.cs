using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Queries;
using Neo4j.Driver;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Infrastructure;

public class SchemaBootstrapperTests
{
    private static SchemaBootstrapper CreateBootstrapper(
        INeo4jTransactionRunner txRunner,
        int embeddingDimensions = 1536,
        bool validateVectorIndexDimensions = true)
    {
        var options = Options.Create(new Neo4jOptions
        {
            EmbeddingDimensions = embeddingDimensions,
            ValidateVectorIndexDimensions = validateVectorIndexDimensions,
        });
        return new SchemaBootstrapper(txRunner, options, NullLogger<SchemaBootstrapper>.Instance);
    }

    private static void StubWriteRunner(INeo4jTransactionRunner txRunner)
    {
        txRunner
            .WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
    }

    /// <summary>
    /// Bootstrap now also backfills canonical fact keys, which reads a page of unkeyed facts. An
    /// already-migrated store returns none, which is the state every one of these DDL tests assumes.
    /// </summary>
    private static void StubEmptyCanonicalKeyBackfill(INeo4jTransactionRunner txRunner)
    {
        txRunner
            .ReadAsync(
                Arg.Any<Func<IAsyncQueryRunner, Task<List<CanonicalKeyBackfillRow>>>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<CanonicalKeyBackfillRow>()));
    }

    private static void StubVectorIndexRead(
        INeo4jTransactionRunner txRunner, params VectorIndexDimension[] indexes)
        => txRunner
            .ReadAsync(
                Arg.Any<Func<IAsyncQueryRunner, Task<IReadOnlyList<VectorIndexDimension>>>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<VectorIndexDimension>>(indexes));

    [Fact]
    public async Task BootstrapAsync_ExecutesExpectedTotalNumberOfStatements()
    {
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        StubEmptyCanonicalKeyBackfill(txRunner);
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

        // 12 constraints + 3 fulltext + 6 vector + 22 property = 43
        executedStatements.Should().HaveCount(43);
    }

    [Fact]
    public async Task BootstrapAsync_ExecutesAllConstraints()
    {
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        StubEmptyCanonicalKeyBackfill(txRunner);
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
        constraints.Should().HaveCount(12);
        constraints.Should().Contain(s => s.Contains("consolidation_run_id"));
        constraints.Should().Contain(s => s.Contains("conversation_id"));
        constraints.Should().Contain(s => s.Contains("message_id"));
        constraints.Should().Contain(s => s.Contains("entity_id"));
        constraints.Should().Contain(s => s.Contains("fact_id"));
        constraints.Should().Contain(s => s.Contains("preference_id"));
        constraints.Should().Contain(s => s.Contains("extractor_name"));
        constraints.Should().Contain(s => s.Contains("reasoning_trace_id"));
        constraints.Should().Contain(s => s.Contains("reasoning_step_id"));
        constraints.Should().Contain(s => s.Contains("tool_call_id"));
        constraints.Should().Contain(s => s.Contains("tool_name"));
        constraints.Should().Contain(s => s.Contains("memory_read_audit_id"));
    }

    [Fact]
    public async Task BootstrapAsync_ExecutesAllFulltextIndexes()
    {
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        StubEmptyCanonicalKeyBackfill(txRunner);
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
        StubEmptyCanonicalKeyBackfill(txRunner);
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
        StubEmptyCanonicalKeyBackfill(txRunner);
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
            .Where(s => s.StartsWith("CREATE INDEX") || s.StartsWith("CREATE POINT INDEX"))
            .ToList();
        propertyIndexes.Should().HaveCount(22);
        propertyIndexes.Should().Contain(s => s.Contains("conversation_session_idx"));
        propertyIndexes.Should().Contain(s => s.Contains("conversation_archived_idx"));
        propertyIndexes.Should().Contain(s => s.Contains("message_timestamp"));
        propertyIndexes.Should().Contain(s => s.Contains("message_role_idx"));
        propertyIndexes.Should().Contain(s => s.Contains("entity_type"));
        propertyIndexes.Should().Contain(s => s.Contains("entity_name"));
        propertyIndexes.Should().Contain(s => s.Contains("entity_canonical_idx"));
        propertyIndexes.Should().Contain(s => s.Contains("fact_category"));
        propertyIndexes.Should().Contain(s => s.Contains("preference_category"));
        propertyIndexes.Should().Contain(s => s.Contains("trace_session_idx"));
        propertyIndexes.Should().Contain(s => s.Contains("trace_success_idx"));
        propertyIndexes.Should().Contain(s => s.Contains("reasoning_step_timestamp"));
        propertyIndexes.Should().Contain(s => s.Contains("tool_call_status"));
        propertyIndexes.Should().Contain(s => s.Contains("schema_name_idx"));
        propertyIndexes.Should().Contain(s => s.Contains("schema_version_idx"));
        propertyIndexes.Should().Contain(s => s.Contains("entity_location_idx"));
        propertyIndexes.Should().Contain(s => s.Contains("fact_owner_idx"));
        propertyIndexes.Should().Contain(s => s.Contains("entity_owner_idx"));
        propertyIndexes.Should().Contain(s => s.Contains("preference_owner_idx"));
        propertyIndexes.Should().Contain(s => s.Contains("trace_owner_idx"));
        propertyIndexes.Should().Contain(s => s.Contains("rel_owner_idx"));
        propertyIndexes.Should().Contain(s => s.Contains("memory_read_audit_kind_idx"));
    }

    [Theory]
    [InlineData(1536)]
    [InlineData(3072)]
    [InlineData(768)]
    public void BuildVectorIndexes_EmbeddingDimensionAppearsInAllIndexes(int dimensions)
    {
        var indexes = SchemaQueries.BuildVectorIndexes(dimensions);

        indexes.Should().HaveCount(6);
        indexes.Should().AllSatisfy(idx =>
            idx.Should().Contain($"`vector.dimensions`: {dimensions}"));
    }

    [Fact]
    public void BuildVectorIndexes_AllIndexesUseCosineFunction()
    {
        var indexes = SchemaQueries.BuildVectorIndexes(1536);

        indexes.Should().AllSatisfy(idx =>
            idx.Should().Contain("'cosine'"));
    }

    [Fact]
    public void BuildVectorIndexes_AllIndexesTargetEmbeddingProperty()
    {
        var indexes = SchemaQueries.BuildVectorIndexes(1536);

        indexes.Should().AllSatisfy(idx =>
            idx.Should().MatchRegex(@"ON \(n\.(embedding|task_embedding)\)"));
    }

    [Fact]
    public void BuildVectorIndexes_AllIndexesAreIdempotent()
    {
        var indexes = SchemaQueries.BuildVectorIndexes(1536);

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
    public void Neo4jOptions_ValidateVectorIndexDimensions_DefaultsToTrue()
        => new Neo4jOptions().ValidateVectorIndexDimensions.Should().BeTrue();

    [Fact]
    public async Task BootstrapAsync_WhenExistingVectorIndexDimensionsMatch_DoesNotThrow()
    {
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        StubEmptyCanonicalKeyBackfill(txRunner);
        StubWriteRunner(txRunner);
        StubVectorIndexRead(txRunner,
            new VectorIndexDimension("fact_embedding_idx", 1536),
            new VectorIndexDimension("entity_embedding_idx", 1536));

        var bootstrapper = CreateBootstrapper(txRunner, embeddingDimensions: 1536);

        await bootstrapper.Invoking(b => b.BootstrapAsync()).Should().NotThrowAsync();
    }

    [Fact]
    public async Task BootstrapAsync_WhenExistingVectorIndexDimensionsMismatch_Throws()
    {
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        StubEmptyCanonicalKeyBackfill(txRunner);
        StubWriteRunner(txRunner);
        StubVectorIndexRead(txRunner,
            new VectorIndexDimension("fact_embedding_idx", 1536),
            new VectorIndexDimension("entity_embedding_idx", 3072)); // stale after model switch

        var bootstrapper = CreateBootstrapper(txRunner, embeddingDimensions: 1536);

        var ex = (await bootstrapper.Invoking(b => b.BootstrapAsync())
            .Should().ThrowAsync<EmbeddingDimensionMismatchException>()).Which;
        ex.ExpectedDimensions.Should().Be(1536);
        ex.Mismatches.Should().ContainSingle().Which.IndexName.Should().Be("entity_embedding_idx");
    }

    [Fact]
    public async Task BootstrapAsync_WhenValidationDisabled_SkipsTheReadAndDoesNotThrow()
    {
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        StubEmptyCanonicalKeyBackfill(txRunner);
        StubWriteRunner(txRunner);
        // Even with a mismatch staged, validation is off, so it must not be consulted.
        StubVectorIndexRead(txRunner, new VectorIndexDimension("fact_embedding_idx", 3072));

        var bootstrapper = CreateBootstrapper(
            txRunner, embeddingDimensions: 1536, validateVectorIndexDimensions: false);

        await bootstrapper.Invoking(b => b.BootstrapAsync()).Should().NotThrowAsync();

        await txRunner.DidNotReceive().ReadAsync(
            Arg.Any<Func<IAsyncQueryRunner, Task<IReadOnlyList<VectorIndexDimension>>>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BootstrapAsync_VectorIndexesUseConfiguredDimensions()
    {
        const int customDimensions = 3072;
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        StubEmptyCanonicalKeyBackfill(txRunner);
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
