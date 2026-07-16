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

    /// <summary>
    /// Live-Neo4j proof that <c>MemoryTrustLevel</c> (#92 Phase 3) survives a real write + read: stamped by
    /// <c>PersistenceStage</c> into <c>Metadata</c>, serialized to a JSON string property by
    /// <c>Neo4jRecordMapper</c>, and read back correctly via <c>GetTrustLevel()</c> -- which must handle the
    /// <c>JsonElement</c> shape a round-tripped value actually arrives as, not just the original CLR string
    /// a purely in-process unit test would see.
    /// </summary>
    [Fact]
    public async Task FullExtractionPipeline_RequestTrustLevelOverride_RoundTripsThroughNeo4j()
    {
        using var scope = _provider.CreateScope();
        var sp = scope.ServiceProvider;

        var shortTerm = sp.GetRequiredService<IShortTermMemoryService>();
        var pipeline = sp.GetRequiredService<IMemoryExtractionPipeline>();

        const string sessionId = "session-trust-level";
        const string conversationId = "conv-trust-level";

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

        await pipeline.ExtractAsync(new ExtractionRequest
        {
            SessionId = sessionId,
            UserId = "alice",
            Messages = [message],
            TrustLevel = MemoryTrustLevel.ApplicationTrusted
        });

        var entities = sp.GetRequiredService<IEntityRepository>();
        var facts = sp.GetRequiredService<IFactRepository>();
        var preferences = sp.GetRequiredService<IPreferenceRepository>();
        var aliceScope = MemoryScope.For("alice");

        var ada = (await entities.GetByNameAsync("Ada", scope: aliceScope)).Should().ContainSingle().Subject;
        ada.Metadata.GetTrustLevel().Should().Be(MemoryTrustLevel.ApplicationTrusted);

        var fact = await facts.FindByTripleAsync("Ada", "works_at", "Acme", scope: aliceScope);
        fact!.Metadata.GetTrustLevel().Should().Be(MemoryTrustLevel.ApplicationTrusted);

        var preference = (await preferences.GetByCategoryAsync("style", scope: aliceScope))
            .Should().ContainSingle(p => p.PreferenceText == "prefers dark mode").Subject;
        preference.Metadata.GetTrustLevel().Should().Be(MemoryTrustLevel.ApplicationTrusted);
    }

    /// <summary>
    /// Live-Neo4j proof of #92 Phase 5's monotonic-trust fix for facts: <c>Neo4jFactRepository.UpsertAsync</c>
    /// MERGEs on the exact {subject,predicate,object,owner} triple and its Cypher <c>ON MATCH SET</c>
    /// unconditionally overwrites <c>metadata</c> -- a purely in-process unit test with a mocked repository
    /// cannot prove this actually holds against real MERGE semantics, only that <c>PersistenceStage</c> calls
    /// the right methods. Re-extracts the identical "Ada works_at Acme" triple twice, first at
    /// <c>ApplicationTrusted</c> then at a lower <c>UserProvided</c>, and confirms the surviving node's trust
    /// level is never downgraded -- and that it is still exactly one node, not two.
    /// </summary>
    [Fact]
    public async Task FullExtractionPipeline_FactReExtractedAtLowerTrust_DoesNotDowngradeExistingHigherTrust()
    {
        using var scope = _provider.CreateScope();
        var sp = scope.ServiceProvider;

        var shortTerm = sp.GetRequiredService<IShortTermMemoryService>();
        var pipeline = sp.GetRequiredService<IMemoryExtractionPipeline>();
        var facts = sp.GetRequiredService<IFactRepository>();
        var aliceScope = MemoryScope.For("alice");

        const string sessionId = "session-trust-monotonic";
        const string conversationId = "conv-trust-monotonic";
        await shortTerm.AddConversationAsync(conversationId, sessionId, userId: "alice");

        // First extraction: a curated/verified import, stamped ApplicationTrusted.
        var firstMessage = await shortTerm.AddMessageAsync(new Message
        {
            MessageId = $"m-{Guid.NewGuid():N}",
            ConversationId = conversationId,
            SessionId = sessionId,
            Role = "user",
            Content = "Ada works at Acme and prefers dark mode.",
            TimestampUtc = DateTimeOffset.UtcNow,
        });
        await pipeline.ExtractAsync(new ExtractionRequest
        {
            SessionId = sessionId,
            UserId = "alice",
            Messages = [firstMessage],
            TrustLevel = MemoryTrustLevel.ApplicationTrusted
        });

        var factAfterFirst = await facts.FindByTripleAsync("Ada", "works_at", "Acme", scope: aliceScope);
        factAfterFirst!.Metadata.GetTrustLevel().Should().Be(MemoryTrustLevel.ApplicationTrusted);

        // Second extraction: an ordinary later turn re-stating the identical triple, at a lower trust level.
        var secondMessage = await shortTerm.AddMessageAsync(new Message
        {
            MessageId = $"m-{Guid.NewGuid():N}",
            ConversationId = conversationId,
            SessionId = sessionId,
            Role = "user",
            Content = "Ada works at Acme and prefers dark mode.",
            TimestampUtc = DateTimeOffset.UtcNow,
        });
        await pipeline.ExtractAsync(new ExtractionRequest
        {
            SessionId = sessionId,
            UserId = "alice",
            Messages = [secondMessage],
            TrustLevel = MemoryTrustLevel.UserProvided
        });

        var factAfterSecond = await facts.FindByTripleAsync("Ada", "works_at", "Acme", scope: aliceScope);
        factAfterSecond.Should().NotBeNull();
        factAfterSecond!.Metadata.GetTrustLevel().Should().Be(MemoryTrustLevel.ApplicationTrusted,
            "a later, ordinary, lower-trust re-extraction of the identical triple must never silently downgrade an earlier deliberate elevation");
        factAfterSecond.FactId.Should().Be(factAfterFirst.FactId, "the Cypher MERGE must still collapse re-extraction onto the SAME node, not create a second one");
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
