using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using FluentAssertions;
using AgentMemory;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Stubs;
using AgentMemory.Tests.Integration.Fixtures;
using Neo4j.Driver;

namespace AgentMemory.Tests.Integration.Extraction;

/// <summary>
/// J-6, on a real store: a value said again after it was replaced starts a second period. Found by review: "Copenhagen,
/// Oslo, back to Copenhagen" re-opened the first Copenhagen fact (live again, but still ended at the Oslo move), and its
/// first period was lost, so "as of the Oslo months" believed both cities. Scripted extraction, supersession on.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class SecondPeriodIntegrationTests : IAsyncLifetime
{
    private const string Owner = "periods-probe";
    private readonly Neo4jIntegrationFixture _fixture;
    private ServiceProvider _provider = null!;

    public SecondPeriodIntegrationTests(Neo4jIntegrationFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _fixture.CleanDatabaseAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IFactExtractor, ScriptedFacts>();
        services.AddNeo4jAgentMemory(
            new MemoryOptions { Extraction = { SupersedeReplacedFacts = true } },
            configureNeo4j: o =>
            {
                o.Uri = _fixture.ConnectionString;
                o.Username = _fixture.User;
                o.Password = _fixture.Password;
                o.Database = "neo4j";
                o.EmbeddingDimensions = Neo4jIntegrationFixture.TestEmbeddingDimensions;
            });
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
            new StubEmbeddingGenerator(sp.GetRequiredService<ILogger<StubEmbeddingGenerator>>(), Neo4jIntegrationFixture.TestEmbeddingDimensions));
        _provider = services.BuildServiceProvider(validateScopes: true);
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }

    [Fact]
    public async Task Moving_back_starts_a_second_period_and_the_first_one_keeps_its_end()
    {
        await SayAsync("I live in Copenhagen.");
        await Task.Delay(50);
        await SayAsync("I moved to Oslo.");
        await Task.Delay(50);
        var duringOslo = DateTimeOffset.UtcNow;
        await Task.Delay(50);
        await SayAsync("I'm back in Copenhagen.");

        var cities = await ReadAsync(
            "MATCH (f:Fact {owner_id: $owner}) WHERE f.predicate = 'lives in' " +
            "RETURN f.object AS city, f.invalidated_at IS NULL AS live, f.period_key AS period ORDER BY f.created_at",
            new { owner = Owner });
        cities.Select(r => (ValueExtensions.As<string>(r["city"]), ValueExtensions.As<bool>(r["live"]))).Should().Equal(
            [("Copenhagen", false), ("Oslo", false), ("Copenhagen", true)],
            "two Copenhagen periods, the first closed by Oslo, the second live; Oslo closed by the return");
        ValueExtensions.As<string>(cities[2]["period"]).Should().Be("", "the live period is the open one");

        var believedDuringOslo = await ReadAsync(
            "MATCH (f:Fact {owner_id: $owner}) WHERE f.predicate = 'lives in' AND f.created_at <= datetime($at) " +
            "AND (f.invalidated_at IS NULL OR f.invalidated_at > datetime($at)) RETURN f.object AS city",
            new { owner = Owner, at = duringOslo.ToString("O") });
        believedDuringOslo.Select(r => ValueExtensions.As<string>(r["city"])).Should().Equal(["Oslo"], "as of the Oslo months, only Oslo");

        using var scope = _provider.CreateScope();
        var live = await scope.ServiceProvider.GetRequiredService<IFactRepository>()
            .FindByTripleAsync("user", "lives in", "Copenhagen", MemoryScope.For(Owner, includeShared: false));
        live!.InvalidatedAtUtc.Should().BeNull("the triple's lookup finds its live period");
    }

    [Fact]
    public async Task Saying_the_same_thing_again_while_it_holds_is_one_fact()
    {
        await SayAsync("I live in Copenhagen.");
        await SayAsync("I live in Copenhagen.");

        var rows = await ReadAsync("MATCH (f:Fact {owner_id: $owner}) RETURN count(f) AS n, max(f.mention_count) AS mentions", new { owner = Owner });
        ValueExtensions.As<long>(rows[0]["n"]).Should().Be(1);
        ValueExtensions.As<long>(rows[0]["mentions"]).Should().Be(2, "a restatement of a live fact still counts on the same node");
    }

    [Fact]
    public async Task An_existing_store_gets_its_period_keys_at_bootstrap_and_keeps_one_fact_per_live_triple()
    {
        // Facts as a store written before periods holds them: no period_key; one live, one superseded.
        await using (var session = _fixture.Driver.AsyncSession())
        {
            await session.RunAsync(
                "CREATE (old:Fact {id: 'old', subject: 'user', predicate: 'lives in', object: 'Hamburg', owner_id: $owner, owner_key: $owner, " +
                "subject_key: 'user', predicate_key: 'lives in', object_key: 'hamburg', confidence: 0.9, created_at: datetime(), invalidated_at: datetime()}) " +
                "CREATE (now:Fact {id: 'now', subject: 'user', predicate: 'lives in', object: 'Copenhagen', owner_id: $owner, owner_key: $owner, " +
                "subject_key: 'user', predicate_key: 'lives in', object_key: 'copenhagen', confidence: 0.9, created_at: datetime(), mention_count: 1}) " +
                "CREATE (old)-[:SUPERSEDED_BY]->(now)", new { owner = Owner });
        }
        var bootstrapper = new AgentMemory.Neo4j.Infrastructure.SchemaBootstrapper(_fixture.TransactionRunner,
            Microsoft.Extensions.Options.Options.Create(new AgentMemory.Neo4j.Infrastructure.Neo4jOptions { EmbeddingDimensions = Neo4jIntegrationFixture.TestEmbeddingDimensions }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentMemory.Neo4j.Infrastructure.SchemaBootstrapper>.Instance);

        (await bootstrapper.BackfillFactPeriodKeysAsync(1)).Should().Be(2, "batches of one reach every fact");
        var keys = await ReadAsync("MATCH (f:Fact {owner_id: $owner}) RETURN f.id AS id, f.period_key AS period ORDER BY f.id", new { owner = Owner });
        keys.Select(r => (ValueExtensions.As<string>(r["id"]), ValueExtensions.As<string>(r["period"])))
            .Should().Equal([("now", ""), ("old", "old")], "open for the live fact, closed (its id) for the superseded one");

        await SayAsync("I live in Copenhagen.");
        var copenhagen = await ReadAsync("MATCH (f:Fact {owner_id: $owner, object: 'Copenhagen'}) RETURN count(f) AS n", new { owner = Owner });
        ValueExtensions.As<long>(copenhagen[0]["n"]).Should().Be(1, "the restatement lands on the existing live fact, not a duplicate");
    }

    private async Task<List<IRecord>> ReadAsync(string cypher, object parameters)
    {
        await using var session = _fixture.Driver.AsyncSession();
        var cursor = await session.RunAsync(cypher, parameters);
        return await cursor.ToListAsync();
    }

    private async Task SayAsync(string text)
    {
        using var scope = _provider.CreateScope();
        var shortTerm = scope.ServiceProvider.GetRequiredService<IShortTermMemoryService>();
        var pipeline = scope.ServiceProvider.GetRequiredService<IMemoryExtractionPipeline>();
        await shortTerm.AddConversationAsync("conv-periods", "session-periods", userId: Owner);
        var message = await shortTerm.AddMessageAsync(new Message
        {
            MessageId = $"m-{Guid.NewGuid():N}", ConversationId = "conv-periods", SessionId = "session-periods",
            Role = "user", Content = text, TimestampUtc = DateTimeOffset.UtcNow,
        });
        await pipeline.ExtractAsync(new ExtractionRequest { SessionId = "session-periods", UserId = Owner, Messages = [message] });
    }

    private sealed class ScriptedFacts : IFactExtractor
    {
        public Task<IReadOnlyList<ExtractedFact>> ExtractAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExtractedFact>>([.. messages.Select(m => new ExtractedFact
            {
                Subject = "user", Predicate = "lives in", Confidence = 0.95,
                Object = m.Content.Contains("Oslo", StringComparison.Ordinal) ? "Oslo" : "Copenhagen",
            })]);
    }
}
