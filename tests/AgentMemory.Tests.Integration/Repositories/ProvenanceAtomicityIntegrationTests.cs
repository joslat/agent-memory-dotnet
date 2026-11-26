using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;
using Neo4j.Driver;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// Threat TT-12 (provenance loss), on a real store: the Neo4j repositories write a memory and its EXTRACTED_FROM edge in
/// one transaction, so an edge that fails takes the record with it; a memory is never left stored without the link to
/// the message it came from. The failure is injected into the edge statement only, inside the real transaction.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class ProvenanceAtomicityIntegrationTests : IAsyncLifetime
{
    private static readonly DateTimeOffset At = new(2026, 11, 26, 0, 0, 0, TimeSpan.Zero);
    private readonly Neo4jIntegrationFixture _fixture;

    public ProvenanceAtomicityIntegrationTests(Neo4jIntegrationFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _fixture.CleanDatabaseAsync();
        await RunAsync("CREATE (:Message {id: 'm-source', conversation_id: 'c-source', session_id: 's-source', content: 'Alice works at Acme.'})");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_fact_whose_provenance_edge_fails_is_not_stored()
    {
        var failing = new FailingEdgeRunner(_fixture.TransactionRunner);
        Fact Alice() => new()
        {
            FactId = $"fact-{Guid.NewGuid():N}", Subject = "Alice", Predicate = "works_at", Object = "Acme",
            Confidence = 0.9, CreatedAtUtc = At, SourceMessageIds = ["m-source"],
        };

        var act = () => new Neo4jFactRepository(failing, NullLogger<Neo4jFactRepository>.Instance).UpsertAsync(Alice());

        await act.Should().ThrowAsync<Exception>();
        failing.Injected.Should().BeGreaterThan(0, "the edge statement was reached and failed");
        (await CountAsync("MATCH (f:Fact {subject: 'Alice'}) RETURN count(f)")).Should().Be(0, "the fact rolls back with its edge");

        await new Neo4jFactRepository(_fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance).UpsertAsync(Alice());
        (await CountAsync("MATCH (:Fact {subject: 'Alice'})-[:EXTRACTED_FROM]->(:Message {id: 'm-source'}) RETURN count(*)")).Should().Be(1);
    }

    [Fact]
    public async Task An_entity_whose_provenance_edge_fails_is_not_stored()
    {
        var failing = new FailingEdgeRunner(_fixture.TransactionRunner);
        Entity Acme() => new()
        {
            EntityId = $"entity-{Guid.NewGuid():N}", Name = "Acme", Type = "ORGANIZATION", Confidence = 0.9, CreatedAtUtc = At,
            SourceMessageIds = ["m-source"],
        };

        var act = () => new Neo4jEntityRepository(failing, NullLogger<Neo4jEntityRepository>.Instance).UpsertAsync(Acme());

        await act.Should().ThrowAsync<Exception>();
        failing.Injected.Should().BeGreaterThan(0, "the edge statement was reached and failed");
        (await CountAsync("MATCH (e:Entity {name: 'Acme'}) RETURN count(e)")).Should().Be(0, "the entity rolls back with its edge");

        await new Neo4jEntityRepository(_fixture.TransactionRunner, NullLogger<Neo4jEntityRepository>.Instance).UpsertAsync(Acme());
        (await CountAsync("MATCH (:Entity {name: 'Acme'})-[:EXTRACTED_FROM]->(:Message {id: 'm-source'}) RETURN count(*)")).Should().Be(1);
    }

    [Fact]
    public async Task A_preference_whose_provenance_edge_fails_is_not_stored()
    {
        var failing = new FailingEdgeRunner(_fixture.TransactionRunner);
        Preference Tea() => new()
        {
            PreferenceId = $"preference-{Guid.NewGuid():N}", Category = "drink", PreferenceText = "prefers green tea",
            Confidence = 0.9, CreatedAtUtc = At, SourceMessageIds = ["m-source"],
        };

        var act = () => new Neo4jPreferenceRepository(failing, NullLogger<Neo4jPreferenceRepository>.Instance).UpsertAsync(Tea());

        await act.Should().ThrowAsync<Exception>();
        failing.Injected.Should().BeGreaterThan(0, "the edge statement was reached and failed");
        (await CountAsync("MATCH (p:Preference {preference: 'prefers green tea'}) RETURN count(p)")).Should().Be(0,
            "the preference rolls back with its edge");

        await new Neo4jPreferenceRepository(_fixture.TransactionRunner, NullLogger<Neo4jPreferenceRepository>.Instance).UpsertAsync(Tea());
        (await CountAsync("MATCH (:Preference {preference: 'prefers green tea'})-[:EXTRACTED_FROM]->(:Message {id: 'm-source'}) RETURN count(*)"))
            .Should().Be(1);
    }

    private async Task RunAsync(string cypher)
    {
        await using var session = _fixture.Driver.AsyncSession();
        await (await session.RunAsync(cypher)).ConsumeAsync();
    }

    private async Task<long> CountAsync(string cypher)
    {
        await using var session = _fixture.Driver.AsyncSession();
        var cursor = await session.RunAsync(cypher);
        return ValueExtensions.As<long>((await cursor.SingleAsync())[0]);
    }

    /// <summary>The real runner, except that a statement writing an EXTRACTED_FROM edge fails, as a transient fault would.</summary>
    private sealed class FailingEdgeRunner(INeo4jTransactionRunner inner) : INeo4jTransactionRunner
    {
        private int _injected;

        public int Injected => Volatile.Read(ref _injected);

        public Task<T> ReadAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(runner => work(new Failing(runner, this)), cancellationToken);

        public Task ReadAsync(Func<IAsyncQueryRunner, Task> work, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(runner => work(new Failing(runner, this)), cancellationToken);

        public Task<T> WriteAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(runner => work(new Failing(runner, this)), cancellationToken);

        public Task WriteAsync(Func<IAsyncQueryRunner, Task> work, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(runner => work(new Failing(runner, this)), cancellationToken);

        private sealed class Failing(IAsyncQueryRunner inner, FailingEdgeRunner owner) : IAsyncQueryRunner
        {
            private Task<IResultCursor> Run(string query, Func<Task<IResultCursor>> run)
            {
                if (!query.Contains("[:EXTRACTED_FROM]", StringComparison.Ordinal)) return run();
                Interlocked.Increment(ref owner._injected);
                throw new InvalidOperationException("Injected: the provenance edge statement failed.");
            }

            public Task<IResultCursor> RunAsync(string query) => Run(query, () => inner.RunAsync(query));
            public Task<IResultCursor> RunAsync(string query, object parameters) => Run(query, () => inner.RunAsync(query, parameters));
            public Task<IResultCursor> RunAsync(string query, IDictionary<string, object> parameters) =>
                Run(query, () => inner.RunAsync(query, parameters));
            public Task<IResultCursor> RunAsync(Query query) => Run(query.Text, () => inner.RunAsync(query));
            public void Dispose() => inner.Dispose();
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
