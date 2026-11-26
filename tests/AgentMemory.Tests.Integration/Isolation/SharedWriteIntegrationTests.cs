using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using AgentMemory;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Stubs;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;
using Neo4j.Driver;

namespace AgentMemory.Tests.Integration.Isolation;

/// <summary>
/// Threat TT-03 (accidental shared writes), on a real store: a write that names no owner is stored as shared
/// (<c>owner_id IS NULL</c>) and every owner recalls it, while an owner's write stays theirs. Shown at the API (the
/// extraction pipeline) and at the repository. The refusal of an unscoped write in strict mode is
/// <see cref="StrictMultiTenantIntegrationTests"/>; here strict mode's deliberate shared path (ShareWithEveryone).
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class SharedWriteIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly List<ServiceProvider> _providers = [];

    public SharedWriteIntegrationTests(Neo4jIntegrationFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();

    public async Task DisposeAsync()
    {
        foreach (var provider in _providers) await provider.DisposeAsync();
    }

    [Fact]
    public async Task A_write_with_no_owner_is_stored_as_shared_and_every_owner_recalls_it()
    {
        var provider = Build(MemoryIsolationMode.SingleTenant);

        await ExtractAsync(provider, owner: null, "The office is in Bilbao.");
        await ExtractAsync(provider, owner: "ana", "I live in Porto.");

        (await CountAsync("MATCH (f:Fact {object: 'Bilbao'}) WHERE f.owner_id IS NULL RETURN count(f)")).Should().Be(1,
            "a write that names no owner is stored as shared");
        (await CountAsync("MATCH (f:Fact {object: 'Porto', owner_id: 'ana'}) RETURN count(f)")).Should().Be(1,
            "an owner's write is theirs");
        (await RecalledAsync(provider, "bob")).Should().Equal(["Bilbao"], "every owner recalls shared memory, and no one else's");
        (await RecalledAsync(provider, "ana")).Should().Equal(["Bilbao", "Porto"]);
    }

    // RVW-REST-IDENTITY (the review's identity-3): a write with no owner reads shared memory only, so it never resolves
    // into (and writes its aliases and sources onto) another owner's private entity.
    [Fact]
    public async Task A_write_with_no_owner_does_not_resolve_into_another_owners_private_entity()
    {
        var provider = Build(MemoryIsolationMode.SingleTenant);

        await ExtractAsync(provider, owner: "bob", "Dana Whitfield joined Contoso.");
        await ExtractAsync(provider, owner: null, "Dana Whitfield joined Contoso.");

        (await CountAsync("MATCH (e:Entity {name: 'Dana Whitfield', owner_id: 'bob'}) RETURN count(e)")).Should().Be(1);
        (await CountAsync("MATCH (e:Entity {name: 'Dana Whitfield'}) WHERE e.owner_id IS NULL RETURN count(e)")).Should().Be(1,
            "the ownerless mention is its own shared entity");
        (await CountAsync("MATCH (e:Entity {name: 'Dana Whitfield', owner_id: 'bob'}) RETURN size(e.source_message_ids)")).Should().Be(1,
            "bob's entity carries only bob's message");
        (await CountAsync("MATCH (e:Entity {name: 'Dana Whitfield', owner_id: 'bob'})-[:EXTRACTED_FROM]->(m:Message) RETURN count(m)")).Should().BeLessThanOrEqualTo(1);
    }

    [Fact]
    public async Task Strict_isolation_still_stores_a_deliberately_shared_write_as_shared()
    {
        var provider = Build(MemoryIsolationMode.StrictMultiTenant);

        await ExtractAsync(provider, owner: null, "The office is in Bilbao.", shareWithEveryone: true);

        (await CountAsync("MATCH (f:Fact {object: 'Bilbao'}) WHERE f.owner_id IS NULL RETURN count(f)")).Should().Be(1);
        (await CountAsync("MATCH (f:Fact) WHERE f.owner_id IS NOT NULL RETURN count(f)")).Should().Be(0);
    }

    [Fact]
    public async Task At_the_repository_a_record_with_no_owner_is_stored_as_shared()
    {
        var at = new DateTimeOffset(2026, 11, 26, 0, 0, 0, TimeSpan.Zero);
        await new Neo4jFactRepository(_fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance).UpsertAsync(new Fact
        {
            FactId = "fact-shared", Subject = "office", Predicate = "located in", Object = "Bilbao", Confidence = 0.9, CreatedAtUtc = at,
        });
        await new Neo4jEntityRepository(_fixture.TransactionRunner, NullLogger<Neo4jEntityRepository>.Instance).UpsertAsync(new Entity
        {
            EntityId = "entity-shared", Name = "Acme", Type = "ORGANIZATION", Confidence = 0.9, CreatedAtUtc = at,
        });
        await new Neo4jPreferenceRepository(_fixture.TransactionRunner, NullLogger<Neo4jPreferenceRepository>.Instance).UpsertAsync(new Preference
        {
            PreferenceId = "preference-shared", Category = "style", PreferenceText = "short answers", Confidence = 0.9, CreatedAtUtc = at,
        });

        (await CountAsync("""
            MATCH (n) WHERE n.id IN ['fact-shared', 'entity-shared', 'preference-shared'] AND n.owner_id IS NULL
            RETURN count(n)
            """)).Should().Be(3, "a fact, an entity and a preference written with no owner are all shared");
    }

    private ServiceProvider Build(MemoryIsolationMode mode)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IFactExtractor, ScriptedFacts>();
        services.AddSingleton<IEntityExtractor, ScriptedEntities>();
        services.AddNeo4jAgentMemory(
            configureMemory: o => o.Isolation.Mode = mode,
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
        var provider = services.BuildServiceProvider(validateScopes: true);
        _providers.Add(provider);
        return provider;
    }

    private static async Task ExtractAsync(ServiceProvider provider, string? owner, string text, bool shareWithEveryone = false)
    {
        using var scope = provider.CreateScope();
        var session = $"s-{owner ?? "shared"}";
        await scope.ServiceProvider.GetRequiredService<IMemoryExtractionPipeline>().ExtractAsync(new ExtractionRequest
        {
            SessionId = session, UserId = owner, ShareWithEveryone = shareWithEveryone,
            Messages =
            [
                new Message
                {
                    MessageId = $"m-{Guid.NewGuid():N}", ConversationId = $"c-{owner ?? "shared"}", SessionId = session,
                    Role = "user", Content = text, TimestampUtc = DateTimeOffset.UtcNow,
                },
            ],
        });
    }

    private static async Task<IReadOnlyList<string>> RecalledAsync(ServiceProvider provider, string owner)
    {
        using var scope = provider.CreateScope();
        var context = (await scope.ServiceProvider.GetRequiredService<IMemoryService>().RecallAsync(new RecallRequest
        {
            SessionId = $"ask-{owner}", UserId = owner, Query = "Where is it?",
            Options = new RecallOptions
            {
                MaxRecentMessages = 0, MaxRelevantMessages = 0, MaxEntities = 0, MaxPreferences = 0, MaxTraces = 0,
                MaxFacts = 10, MinSimilarityScore = 0,
            },
        })).Context;
        return [.. context.RelevantFacts.Items.Select(f => f.Object).Order(StringComparer.Ordinal)];
    }

    private async Task<long> CountAsync(string cypher)
    {
        await using var session = _fixture.Driver.AsyncSession();
        var cursor = await session.RunAsync(cypher);
        return ValueExtensions.As<long>((await cursor.SingleAsync())[0]);
    }

    private sealed class ScriptedFacts : IFactExtractor
    {
        public Task<IReadOnlyList<ExtractedFact>> ExtractAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExtractedFact>>([.. messages.Select(m => m.Content switch
            {
                "The office is in Bilbao." => new ExtractedFact { Subject = "office", Predicate = "located in", Object = "Bilbao", Confidence = 0.95 },
                "I live in Porto." => new ExtractedFact { Subject = "user", Predicate = "lives in", Object = "Porto", Confidence = 0.95 },
                "Dana Whitfield joined Contoso." => new ExtractedFact { Subject = "Dana Whitfield", Predicate = "works at", Object = "Contoso", Confidence = 0.95 },
                _ => throw new InvalidOperationException($"No script for '{m.Content}'."),
            })]);
    }

    private sealed class ScriptedEntities : IEntityExtractor
    {
        public Task<IReadOnlyList<ExtractedEntity>> ExtractAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExtractedEntity>>([.. messages
                .Where(m => m.Content.Contains("Dana Whitfield", StringComparison.Ordinal))
                .Select(_ => new ExtractedEntity { Name = "Dana Whitfield", Type = "PERSON", Confidence = 0.95 })]);
    }
}
