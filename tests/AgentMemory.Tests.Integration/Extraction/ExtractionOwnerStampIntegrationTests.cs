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
/// Live-Neo4j proof that the full extraction pipeline -- real DI registration
/// (<c>AddNeo4jAgentMemory</c>), real repositories, real Neo4j -- stamps the supplied owner on every
/// persisted memory type and wires provenance. <c>MemoryExtractionPipelineTests</c> verifies the same
/// contract at the pipeline-unit level (mocked stages), which can't catch a DI-wiring, persistence-
/// mapping, or repository regression that silently drops or mis-stamps the owner end to end. Closes
/// tests/README.md's "End-to-end owner-stamp on extraction" gap.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class ExtractionOwnerStampIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private ServiceProvider _provider = null!;

    public ExtractionOwnerStampIntegrationTests(Neo4jIntegrationFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _fixture.CleanDatabaseAsync();

        var services = new ServiceCollection();
        services.AddLogging();

        // Deterministic extractors registered BEFORE AddNeo4jAgentMemory: AddAgentMemoryCore registers
        // the no-op stub extractors via TryAddScoped, so registering these first makes them win -- the
        // same override mechanism a real host uses to plug in a production extractor.
        services.AddSingleton<IEntityExtractor, DeterministicEntityExtractor>();
        services.AddSingleton<IFactExtractor, DeterministicFactExtractor>();
        services.AddSingleton<IPreferenceExtractor, DeterministicPreferenceExtractor>();
        services.AddSingleton<IRelationshipExtractor, DeterministicRelationshipExtractor>();

        services.AddNeo4jAgentMemory(
            configureMemory: _ => { },
            configureNeo4j: o =>
            {
                o.Uri = _fixture.ConnectionString;
                o.Username = _fixture.User;
                o.Password = _fixture.Password;
                o.Database = "neo4j";
                o.EmbeddingDimensions = Neo4jIntegrationFixture.TestEmbeddingDimensions;
            });

        // Real, deterministic embeddings at the fixture's dimensionality (same text -> same vector) --
        // matches ShakedownEndToEndTests' convention.
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
            new StubEmbeddingGenerator(
                sp.GetRequiredService<ILogger<StubEmbeddingGenerator>>(),
                Neo4jIntegrationFixture.TestEmbeddingDimensions));

        _provider = services.BuildServiceProvider(validateScopes: true);
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }

    [Fact]
    public async Task FullExtractionPipeline_StampsSuppliedOwner_OnEveryPersistedType_AndWiresProvenance()
    {
        using var scope = _provider.CreateScope();
        var sp = scope.ServiceProvider;

        var shortTerm = sp.GetRequiredService<IShortTermMemoryService>();
        var pipeline = sp.GetRequiredService<IMemoryExtractionPipeline>();

        const string sessionId = "session-owner-stamp";
        const string conversationId = "conv-owner-stamp";

        await shortTerm.AddConversationAsync(conversationId, sessionId, userId: "alice");
        var message = await shortTerm.AddMessageAsync(new Message
        {
            MessageId = $"m-{Guid.NewGuid():N}",
            ConversationId = conversationId,
            SessionId = sessionId,
            Role = "user",
            Content = "Ada works at Acme and prefers dark mode.",
            TimestampUtc = DateTimeOffset.UtcNow,
        });

        var result = await pipeline.ExtractAsync(new ExtractionRequest
        {
            SessionId = sessionId,
            UserId = "alice",
            Messages = [message],
        });

        // The deterministic extractors always return exactly these -- confirm the pipeline actually ran
        // them (a regression that no-ops extraction would otherwise pass every assertion below vacuously).
        result.Metadata["entityCount"].Should().Be(2);
        result.Metadata["factCount"].Should().Be(1);
        result.Metadata["preferenceCount"].Should().Be(1);
        result.Metadata["relationshipCount"].Should().Be(1);

        var entities = sp.GetRequiredService<IEntityRepository>();
        var facts = sp.GetRequiredService<IFactRepository>();
        var preferences = sp.GetRequiredService<IPreferenceRepository>();
        var relationships = sp.GetRequiredService<IRelationshipRepository>();

        var aliceScope = MemoryScope.For("alice");
        var bobScope = MemoryScope.For("bob");

        // Entities: owner-stamped; Alice can retrieve; Bob cannot.
        var adaMatches = await entities.GetByNameAsync("Ada", scope: aliceScope);
        adaMatches.Should().ContainSingle();
        var ada = adaMatches[0];
        ada.OwnerId.Should().Be("alice");
        (await entities.GetByNameAsync("Ada", scope: bobScope)).Should().BeEmpty("Bob must not see Alice's private entity");

        var acmeMatches = await entities.GetByNameAsync("Acme", scope: aliceScope);
        acmeMatches.Should().ContainSingle();
        acmeMatches[0].OwnerId.Should().Be("alice");

        // Facts: owner-stamped; Alice can retrieve; Bob cannot.
        var fact = await facts.FindByTripleAsync("Ada", "works_at", "Acme", scope: aliceScope);
        fact.Should().NotBeNull();
        fact!.OwnerId.Should().Be("alice");
        (await facts.FindByTripleAsync("Ada", "works_at", "Acme", scope: bobScope)).Should().BeNull("Bob must not see Alice's private fact");

        // Preferences: owner-stamped; Alice can retrieve; Bob cannot.
        var alicePrefs = await preferences.GetByCategoryAsync("style", scope: aliceScope);
        alicePrefs.Should().ContainSingle(p => p.PreferenceText == "prefers dark mode" && p.OwnerId == "alice");
        (await preferences.GetByCategoryAsync("style", scope: bobScope)).Should().BeEmpty("Bob must not see Alice's private preference");

        // Relationships: owner-stamped, using the same owner-isolation semantics as every other type.
        var aliceRels = await relationships.GetBySourceEntityAsync(ada.EntityId, scope: aliceScope);
        aliceRels.Should().ContainSingle(r => r.RelationshipType == "WORKS_AT" && r.OwnerId == "alice");
        (await relationships.GetBySourceEntityAsync(ada.EntityId, scope: bobScope)).Should().BeEmpty("Bob must not see Alice's private relationship");

        // Provenance: EXTRACTED_FROM edges exist from every persisted item back to the source message.
        // PersistenceStage wires this for entities, facts, and preferences (not relationships):
        // 2 entities + 1 fact + 1 preference = 4.
        var provenanceCount = await _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                "MATCH (m:Message {id: $messageId})<-[:EXTRACTED_FROM]-(n) RETURN count(n) AS c",
                new Dictionary<string, object?> { ["messageId"] = message.MessageId });
            var record = await cursor.SingleAsync();
            return global::Neo4j.Driver.ValueExtensions.As<long>(record["c"]);
        });
        provenanceCount.Should().Be(4, "the 2 entities, the fact, and the preference each get an EXTRACTED_FROM edge to the source message");

        // Not silently persisted as shared: no owner-less copy exists (an unscoped read still finds it
        // under alice's owner_id, not under a null owner_id -- the whole point of stamping).
        (await entities.GetByNameAsync("Ada", scope: null)).Should().ContainSingle(e => e.OwnerId == "alice");
    }

    // ── Deterministic test extractors ──
    // The default Stub*Extractor registrations produce no output (Phase-1 no-ops), so a live test needs
    // real-ish extractors that deterministically produce one of each type plus a relationship between two
    // of the entities, per the issue's acceptance criteria.

    private sealed class DeterministicEntityExtractor : IEntityExtractor
    {
        public Task<IReadOnlyList<ExtractedEntity>> ExtractAsync(
            IReadOnlyList<Message> messages, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExtractedEntity>>(
            [
                new ExtractedEntity { Name = "Ada", Type = "Person", Confidence = 0.95 },
                new ExtractedEntity { Name = "Acme", Type = "Organization", Confidence = 0.95 },
            ]);
    }

    private sealed class DeterministicFactExtractor : IFactExtractor
    {
        public Task<IReadOnlyList<ExtractedFact>> ExtractAsync(
            IReadOnlyList<Message> messages, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExtractedFact>>(
            [
                new ExtractedFact { Subject = "Ada", Predicate = "works_at", Object = "Acme", Confidence = 0.95 },
            ]);
    }

    private sealed class DeterministicPreferenceExtractor : IPreferenceExtractor
    {
        public Task<IReadOnlyList<ExtractedPreference>> ExtractAsync(
            IReadOnlyList<Message> messages, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExtractedPreference>>(
            [
                new ExtractedPreference { Category = "style", PreferenceText = "prefers dark mode", Confidence = 0.95 },
            ]);
    }

    private sealed class DeterministicRelationshipExtractor : IRelationshipExtractor
    {
        public Task<IReadOnlyList<ExtractedRelationship>> ExtractAsync(
            IReadOnlyList<Message> messages, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExtractedRelationship>>(
            [
                new ExtractedRelationship { SourceEntity = "Ada", TargetEntity = "Acme", RelationshipType = "WORKS_AT", Confidence = 0.95 },
            ]);
    }
}
