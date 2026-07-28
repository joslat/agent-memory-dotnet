using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using AgentMemory;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Stubs;
using AgentMemory.Neo4j.Infrastructure;
using Neo4j.Driver;
using Testcontainers.Neo4j;

namespace AgentMemory.Tests.Integration.Extraction;

/// <summary>
/// Destructive live-Neo4j characterization for a connection loss in the middle of one logical
/// extraction persist. It owns its container so stopping Neo4j cannot disturb the shared fixture.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TornWriteRollbackIntegrationTests : IAsyncLifetime
{
    private const string Username = "neo4j";
    private const string Password = "testpassword";
    private const string OwnerId = "m31-owner";
    private const int Dimensions = 4;

    private Neo4jContainer _container = null!;
    private ServiceProvider _provider = null!;
    private DropAfterFirstWriteRunner _faultRunner = null!;
    private bool _connectionObservedDown;

    public async Task InitializeAsync()
    {
        _container = new Neo4jBuilder("neo4j:5.26")
            .WithEnvironment("NEO4J_AUTH", $"{Username}/{Password}")
            .Build();
        await _container.StartAsync();

        _provider = BuildProvider();
        await _provider.GetRequiredService<ISchemaBootstrapper>().BootstrapAsync();
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null)
            await _provider.DisposeAsync();
        if (_container is not null)
            await _container.DisposeAsync();
    }

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IEntityExtractor, DeterministicEntityExtractor>();
        services.AddSingleton<IFactExtractor, DeterministicFactExtractor>();
        services.AddSingleton<IPreferenceExtractor, DeterministicPreferenceExtractor>();
        services.AddSingleton<IRelationshipExtractor, DeterministicRelationshipExtractor>();
        services.AddNeo4jAgentMemory(
            configureMemory: options =>
                options.Extraction.FailureMode = IngestionFailureMode.FailFast,
            configureNeo4j: options =>
            {
                options.Uri = _container.GetConnectionString();
                options.Username = Username;
                options.Password = Password;
                options.Database = "neo4j";
                options.EmbeddingDimensions = Dimensions;
            });
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
            new StubEmbeddingGenerator(sp.GetRequiredService<ILogger<StubEmbeddingGenerator>>(), Dimensions));
        services.Replace(ServiceDescriptor.Singleton<INeo4jTransactionRunner>(sp =>
        {
            var inner = new Neo4jTransactionRunner(
                sp.GetRequiredService<INeo4jSessionFactory>(),
                sp.GetRequiredService<ILogger<Neo4jTransactionRunner>>());
            _faultRunner = new DropAfterFirstWriteRunner(inner, KillContainerProcessAsync);
            return _faultRunner;
        }));
        return services.BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public async Task ConnectionDropMidPersist_RollsBackWholeTurn_ThenExactRetryCreatesNoDuplicates()
    {
        var transactionServiceType = typeof(StubEmbeddingGenerator).Assembly.GetType(
            "AgentMemory.Core.Extraction.IMemoryPersistenceTransaction", throwOnError: true)!;
        var transactionService = _provider.GetRequiredService(transactionServiceType);
        transactionService.GetType().FullName.Should().Be(
            "AgentMemory.Neo4j.Infrastructure.Neo4jMemoryPersistenceTransaction",
            "Neo4j registration must replace the portable pass-through coordinator");

        Message sourceMessage;
        var runnerField = transactionService.GetType()
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Single(field => typeof(INeo4jAtomicTransactionRunner).IsAssignableFrom(field.FieldType));
        var coordinatorRunner = runnerField.GetValue(transactionService);
        coordinatorRunner.Should().BeSameAs(_faultRunner,
            "the persistence coordinator and repositories must share the same transaction runner instance");

        var injectedRunner = _faultRunner;
        using (var seedScope = _provider.CreateScope())
        {
            var shortTerm = seedScope.ServiceProvider.GetRequiredService<IShortTermMemoryService>();
            await shortTerm.AddConversationAsync("m31-conversation", "m31-session", userId: OwnerId);
            sourceMessage = await shortTerm.AddMessageAsync(new Message
            {
                MessageId = "m31-source-message",
                ConversationId = "m31-conversation",
                SessionId = "m31-session",
                Role = "user",
                Content = "Ada works at Acme and prefers dark mode.",
                TimestampUtc = DateTimeOffset.UtcNow,
            });
        }

        var before = await ReadSnapshotAsync();
        before.Should().Be(GraphSnapshot.Empty,
            "the isolated owner must start with no long-term memory state");

        var request = new ExtractionRequest
        {
            SessionId = "m31-session",
            UserId = OwnerId,
            Messages = [sourceMessage],
        };

        injectedRunner.Arm();
        Exception? failure = null;
        try
        {
            using var failureScope = _provider.CreateScope();
            var pipeline = failureScope.ServiceProvider.GetRequiredService<IMemoryExtractionPipeline>();
            failure = await Record.ExceptionAsync(() => pipeline.ExtractAsync(request));
        }
        finally
        {
            injectedRunner.Disarm();
            if (injectedRunner.Triggered)
            {
                await _provider.DisposeAsync();
                await _container.StopAsync();
                await _container.StartAsync();
                await WaitForNeo4jAsync();
                _provider = BuildProvider();
                await _provider.GetRequiredService<ISchemaBootstrapper>().BootstrapAsync();
            }
        }
        injectedRunner.AtomicEntered.Should().BeTrue("the product must open one logical persistence transaction");

        _connectionObservedDown.Should().BeTrue("a fresh driver must observe Neo4j as unreachable before persistence continues");
        failure.Should().NotBeNull("the injected connection loss must fail the logical turn");
        failure.Should().BeAssignableTo<MemoryIngestionException>();
        injectedRunner.Triggered.Should().BeTrue("the test must prove the real container was stopped mid-persist");

        var afterFailure = await ReadSnapshotAsync();
        afterFailure.Should().Be(before,
            "a failed logical turn must leave no entities, facts, preferences, relationships, provenance, invalidation, valid-time, or supersession state");

        using (var retryScope = _provider.CreateScope())
        {
            var pipeline = retryScope.ServiceProvider.GetRequiredService<IMemoryExtractionPipeline>();
            var result = await pipeline.ExtractAsync(request);
            result.Metadata["entityCount"].Should().Be(2);
            result.Metadata["factCount"].Should().Be(1);
            result.Metadata["preferenceCount"].Should().Be(1);
            result.Metadata["relationshipCount"].Should().Be(1);
        }

        var afterRetry = await ReadSnapshotAsync();
        afterRetry.Should().Be(new GraphSnapshot(
            TotalNodes: 4,
            Entities: 2,
            Facts: 1,
            Preferences: 1,
            OwnerRelationships: 1,
            ProvenanceRelationships: 4,
            InvalidatedNodes: 0,
            ValidUntilNodes: 0,
            SupersessionRelationships: 0),
            "one exact retry must create each deterministic memory once without destructive or duplicate state");
    }

    private async Task<GraphSnapshot> ReadSnapshotAsync()
    {
        await using var driver = GraphDatabase.Driver(
            _container.GetConnectionString(), AuthTokens.Basic(Username, Password));
        await using var session = driver.AsyncSession();
        var cursor = await session.RunAsync(
            """
            MATCH (n)
            WHERE n.owner_id = $ownerId
            OPTIONAL MATCH (n)-[r]->()
            RETURN count(DISTINCT n) AS totalNodes,
                   count(DISTINCT CASE WHEN n:Entity THEN n END) AS entities,
                   count(DISTINCT CASE WHEN n:Fact THEN n END) AS facts,
                   count(DISTINCT CASE WHEN n:Preference THEN n END) AS preferences,
                   count(DISTINCT CASE WHEN r.owner_id = $ownerId THEN r END) AS ownerRelationships,
                   count(DISTINCT CASE WHEN type(r) = 'EXTRACTED_FROM' THEN r END) AS provenanceRelationships,
                   count(DISTINCT CASE WHEN n.invalidated = true OR n.invalidated_at IS NOT NULL THEN n END) AS invalidatedNodes,
                   count(DISTINCT CASE WHEN n.valid_until IS NOT NULL THEN n END) AS validUntilNodes,
                   count(DISTINCT CASE WHEN type(r) = 'SUPERSEDED_BY' THEN r END) AS supersessionRelationships
            """,
            new Dictionary<string, object> { ["ownerId"] = OwnerId });
        var record = await cursor.SingleAsync();
        return new GraphSnapshot(
            ValueExtensions.As<long>(record["totalNodes"]),
            ValueExtensions.As<long>(record["entities"]),
            ValueExtensions.As<long>(record["facts"]),
            ValueExtensions.As<long>(record["preferences"]),
            ValueExtensions.As<long>(record["ownerRelationships"]),
            ValueExtensions.As<long>(record["provenanceRelationships"]),
            ValueExtensions.As<long>(record["invalidatedNodes"]),
            ValueExtensions.As<long>(record["validUntilNodes"]),
            ValueExtensions.As<long>(record["supersessionRelationships"]));
    }

    private async Task KillContainerProcessAsync()
    {
        try
        {
            // SIGKILL the Neo4j JVM: no graceful-shutdown window in which the active transaction can
            // finish. The exec transport is expected to disappear with the process, so its exception
            // is intentionally swallowed; the following repository operation proves the socket died.
            await _container.ExecAsync(["/bin/sh", "-c", "pkill -9 java"]);
        }
        catch
        {
            // Expected when killing the JVM tears down the exec channel before it returns a status.
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                await using var probe = GraphDatabase.Driver(
                    _container.GetConnectionString(), AuthTokens.Basic(Username, Password));
                await probe.VerifyConnectivityAsync();
            }
            catch
            {
                _connectionObservedDown = true;
                return;
            }

            await Task.Delay(100, timeout.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        throw new InvalidOperationException("Fault injection failed: Neo4j remained reachable after SIGKILL.");
    }

    private async Task WaitForNeo4jAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Exception? lastFailure = null;
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                await using var driver = GraphDatabase.Driver(
                    _container.GetConnectionString(), AuthTokens.Basic(Username, Password));
                await driver.VerifyConnectivityAsync();
                return;
            }
            catch (Exception ex)
            {
                lastFailure = ex;
                await Task.Delay(250, timeout.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }

        throw new TimeoutException("Neo4j did not become reachable after the injected restart.", lastFailure);
    }

    private sealed record GraphSnapshot(
        long TotalNodes,
        long Entities,
        long Facts,
        long Preferences,
        long OwnerRelationships,
        long ProvenanceRelationships,
        long InvalidatedNodes,
        long ValidUntilNodes,
        long SupersessionRelationships)
    {
        public static GraphSnapshot Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    private sealed class DropAfterFirstWriteRunner(
        INeo4jTransactionRunner inner,
        Func<Task> dropConnection) : INeo4jTransactionRunner, INeo4jAtomicTransactionRunner
    {
        private readonly INeo4jAtomicTransactionRunner _atomicInner = inner as INeo4jAtomicTransactionRunner
            ?? throw new InvalidOperationException("The injected runner must support atomic write units.");
        private int _writeCount;
        private int _insideAtomicUnit;
        private int _armed;

        public bool AtomicEntered { get; private set; }

        public bool Triggered { get; private set; }

        public void Arm()
        {
            AtomicEntered = false;
            _writeCount = 0;
            Triggered = false;
            Volatile.Write(ref _armed, 1);
        }

        public void Disarm() => Volatile.Write(ref _armed, 0);

        public Task<T> ReadAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(work, cancellationToken);

        public Task ReadAsync(Func<IAsyncQueryRunner, Task> work, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(work, cancellationToken);

        public async Task<T> WriteAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default)
        {
            var result = await inner.WriteAsync(work, cancellationToken);
            await DropIfFirstArmedWriteAsync();
            return result;
        }

        public async Task WriteAsync(Func<IAsyncQueryRunner, Task> work, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(work, cancellationToken);
            await DropIfFirstArmedWriteAsync();
        }

        public async Task<T> ExecuteAtomicWriteAsync<T>(
            Func<CancellationToken, Task<T>> work,
            CancellationToken cancellationToken = default)
        {
            AtomicEntered = true;
            return await _atomicInner.ExecuteAtomicWriteAsync(async token =>
            {
                Volatile.Write(ref _insideAtomicUnit, 1);
                try
                {
                    return await work(token);
                }
                finally
                {
                    Volatile.Write(ref _insideAtomicUnit, 0);
                }
            }, cancellationToken);
        }

        private async Task DropIfFirstArmedWriteAsync()
        {
            if (Volatile.Read(ref _armed) == 0 || Volatile.Read(ref _insideAtomicUnit) == 0
                || Interlocked.Increment(ref _writeCount) != 1)
                return;

            Triggered = true;
            await dropConnection();
        }
    }

    private sealed class DeterministicEntityExtractor : IEntityExtractor
    {
        public Task<IReadOnlyList<ExtractedEntity>> ExtractAsync(
            IReadOnlyList<Message> messages, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExtractedEntity>>
            ([
                new ExtractedEntity { Name = "Ada", Type = "Person", Confidence = 0.95 },
                new ExtractedEntity { Name = "Acme", Type = "Organization", Confidence = 0.95 },
            ]);
    }

    private sealed class DeterministicFactExtractor : IFactExtractor
    {
        public Task<IReadOnlyList<ExtractedFact>> ExtractAsync(
            IReadOnlyList<Message> messages, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExtractedFact>>
            ([new ExtractedFact { Subject = "Ada", Predicate = "works_at", Object = "Acme", Confidence = 0.95 }]);
    }

    private sealed class DeterministicPreferenceExtractor : IPreferenceExtractor
    {
        public Task<IReadOnlyList<ExtractedPreference>> ExtractAsync(
            IReadOnlyList<Message> messages, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExtractedPreference>>
            ([new ExtractedPreference { Category = "style", PreferenceText = "prefers dark mode", Confidence = 0.95 }]);
    }

    private sealed class DeterministicRelationshipExtractor : IRelationshipExtractor
    {
        public Task<IReadOnlyList<ExtractedRelationship>> ExtractAsync(
            IReadOnlyList<Message> messages, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExtractedRelationship>>
            ([new ExtractedRelationship { SourceEntity = "Ada", TargetEntity = "Acme", RelationshipType = "WORKS_AT", Confidence = 0.95 }]);
    }
}
