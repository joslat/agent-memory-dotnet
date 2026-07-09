using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AgentMemory;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Stubs;
using AgentMemory.Tests.Integration.Fixtures;

namespace AgentMemory.Tests.Integration.Compatibility;

[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
[Trait("Compatibility", "TCK-Mirror")]
public sealed class TckMirroredBehaviorTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private ServiceProvider _provider = null!;

    private static readonly MemoryScope Alice = MemoryScope.For("alice", includeShared: false);
    private static readonly MemoryScope Bob = MemoryScope.For("bob", includeShared: false);
    private static readonly MemoryScope AliceWithShared = MemoryScope.For("alice");

    private static readonly float[] TargetEmbedding = [1.0f, 0.0f, 0.0f, 0.0f];
    private static readonly float[] DistractorEmbedding = [0.0f, 1.0f, 0.0f, 0.0f];

    public TckMirroredBehaviorTests(Neo4jIntegrationFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _fixture.CleanDatabaseAsync();

        var services = new ServiceCollection();
        services.AddLogging();
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
    public async Task NET_TCK_B_001_ShortTermConversation_PersistsSearchesAndClearsSession()
    {
        using var scope = _provider.CreateScope();
        var shortTerm = scope.ServiceProvider.GetRequiredService<IShortTermMemoryService>();

        var sessionId = Id("session");
        var conversationId = Id("conversation");
        var now = DateTimeOffset.UtcNow;

        await shortTerm.AddConversationAsync(
            conversationId,
            sessionId,
            userId: "alice",
            metadata: new Dictionary<string, object> { ["scenario"] = "NET-TCK-B-001" });
        await shortTerm.AddMessagesAsync(
        [
            new Message
            {
                MessageId = Id("message"),
                ConversationId = conversationId,
                SessionId = sessionId,
                Role = "user",
                Content = "Remember that the TCK target color is green.",
                TimestampUtc = now,
                Embedding = TargetEmbedding,
            },
            new Message
            {
                MessageId = Id("message"),
                ConversationId = conversationId,
                SessionId = sessionId,
                Role = "assistant",
                Content = "Stored the target color.",
                TimestampUtc = now.AddSeconds(1),
                Embedding = DistractorEmbedding,
            }
        ]);

        var conversationMessages = await shortTerm.GetConversationMessagesAsync(conversationId);
        conversationMessages.Select(m => m.Role).Should().Equal("user", "assistant");

        var recent = await shortTerm.GetRecentMessagesAsync(sessionId, limit: 1);
        recent.Should().ContainSingle();
        recent[0].Role.Should().Be("assistant", "recent messages are newest-first");

        var search = await shortTerm.SearchMessagesAsync(sessionId, TargetEmbedding, limit: 1, minScore: 0.0);
        search.Should().ContainSingle();
        search[0].Content.Should().Contain("target color");

        await shortTerm.ClearSessionAsync(sessionId, ownerId: "alice");
        (await shortTerm.GetAllSessionMessagesAsync(sessionId)).Should().BeEmpty();
    }
    [Fact]
    public async Task MQ001_LongTermRecords_RoundTripThroughIndexedLookupsWithOwnerScope()
    {
        using var scope = _provider.CreateScope();
        var longTerm = scope.ServiceProvider.GetRequiredService<ILongTermMemoryService>();

        var aliceEntity = await longTerm.AddEntityAsync(NewEntity("TCK Alice", "Person", "alice"));
        await longTerm.AddEntityAsync(NewEntity("TCK Bob Secret", "Person", "bob"));
        var sharedEntity = await longTerm.AddEntityAsync(NewEntity("TCK Shared Catalog", "Object", owner: null));

        var worksAt = await longTerm.AddFactAsync(NewFact("TCK Alice", "works_at", "Neo4j", "alice"));
        var uses = await longTerm.AddFactAsync(NewFact("TCK Alice", "uses", "graph memory", "alice"));
        await longTerm.AddFactAsync(NewFact("TCK Alice", "works_at", "Bob Private Lab", "bob"));

        var preference = await longTerm.AddPreferenceAsync(NewPreference(
            "communication",
            "Prefers direct summaries",
            "alice",
            context: "technical reviews"));
        await longTerm.AddPreferenceAsync(NewPreference("communication", "Bob private preference", "bob"));

        (await longTerm.GetEntitiesByNameAsync("TCK Alice", includeAliases: true, Alice))
            .Should().ContainSingle(e => e.EntityId == aliceEntity.EntityId);
        (await longTerm.GetEntitiesByNameAsync("TCK Bob Secret", includeAliases: true, Alice))
            .Should().BeEmpty("owner-scoped reads must not leak another owner");
        (await longTerm.GetEntitiesByNameAsync("TCK Shared Catalog", includeAliases: true, AliceWithShared))
            .Should().ContainSingle(e => e.EntityId == sharedEntity.EntityId);
        (await longTerm.GetEntitiesByNameAsync("TCK Shared Catalog", includeAliases: true, Alice))
            .Should().BeEmpty("includeShared=false excludes shared/global memory");

        var aliceFacts = await longTerm.GetFactsBySubjectAsync("TCK Alice", Alice);
        aliceFacts.Should().Contain(f => f.FactId == worksAt.FactId);
        aliceFacts.Should().Contain(f => f.FactId == uses.FactId);
        aliceFacts.Should().OnlyContain(f => f.OwnerId == "alice");

        var alicePreferences = await longTerm.GetPreferencesByCategoryAsync("communication", Alice);
        alicePreferences.Should().ContainSingle(p => p.PreferenceId == preference.PreferenceId);
        alicePreferences.Should().OnlyContain(p => p.OwnerId == "alice");
    }

    [Fact]
    public async Task MQ004_ReasoningMemory_RecordsTraceStepsToolCallsAndOwnerScopedLists()
    {
        using var scope = _provider.CreateScope();
        var reasoning = scope.ServiceProvider.GetRequiredService<IReasoningMemoryService>();
        var toolCalls = scope.ServiceProvider.GetRequiredService<IToolCallRepository>();

        var sessionId = Id("session");
        var trace = await reasoning.StartTraceAsync(
            sessionId,
            "Investigate long-term memory behavior",
            taskEmbedding: TargetEmbedding,
            ownerId: "alice");
        var step1 = await reasoning.AddStepAsync(
            trace.TraceId,
            1,
            thought: "Look up the relevant entity",
            action: "search_entities",
            observation: "Found TCK Alice");
        var step2 = await reasoning.AddStepAsync(
            trace.TraceId,
            2,
            thought: "Validate the retrieved fact",
            action: "get_facts",
            observation: "Fact matched");

        var success = await reasoning.RecordToolCallAsync(
            step1.StepId,
            "search_entities",
            "{\"query\":\"TCK Alice\"}",
            resultJson: "{\"count\":1}",
            status: ToolCallStatus.Success,
            durationMs: 17);
        var failure = await reasoning.RecordToolCallAsync(
            step1.StepId,
            "search_preferences",
            "{\"category\":\"missing\"}",
            status: ToolCallStatus.Error,
            error: "category not found");

        await reasoning.CompleteTraceAsync(trace.TraceId, outcome: "validated", success: true);
        await reasoning.StartTraceAsync(sessionId, "Bob private trace", ownerId: "bob");

        var (readTrace, steps) = await reasoning.GetTraceWithStepsAsync(trace.TraceId);
        readTrace.Success.Should().BeTrue();
        readTrace.Outcome.Should().Be("validated");
        steps.Select(s => s.StepId).Should().BeEquivalentTo([step1.StepId, step2.StepId]);

        var recordedToolCalls = await toolCalls.GetByStepAsync(step1.StepId);
        recordedToolCalls.Select(t => t.ToolCallId).Should().BeEquivalentTo([success.ToolCallId, failure.ToolCallId]);
        recordedToolCalls.Should().Contain(t => t.Status == ToolCallStatus.Success && t.DurationMs == 17);
        recordedToolCalls.Should().Contain(t => t.Status == ToolCallStatus.Error && t.Error == "category not found");

        var aliceTraces = await reasoning.ListTracesAsync(sessionId, limit: 10, Alice);
        aliceTraces.Should().ContainSingle(t => t.TraceId == trace.TraceId);
        aliceTraces.Should().OnlyContain(t => t.OwnerId == "alice");
    }

    [Fact]
    public async Task MQ003_RelationshipAndTouchedEntities_LinkLongTermMemoryToReasoning()
    {
        using var scope = _provider.CreateScope();
        var longTerm = scope.ServiceProvider.GetRequiredService<ILongTermMemoryService>();
        var reasoning = scope.ServiceProvider.GetRequiredService<IReasoningMemoryService>();

        var person = await longTerm.AddEntityAsync(NewEntity("TCK Person", "Person", "alice"));
        var organization = await longTerm.AddEntityAsync(NewEntity("TCK Organization", "Organization", "alice"));
        var relationship = await longTerm.AddRelationshipAsync(new Relationship
        {
            RelationshipId = Id("relationship"),
            SourceEntityId = person.EntityId,
            TargetEntityId = organization.EntityId,
            RelationshipType = "WORKS_FOR",
            Confidence = 0.95,
            OwnerId = "alice",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });

        var trace = await reasoning.StartTraceAsync(Id("session"), "Use related entities", ownerId: "alice");
        var step = await reasoning.AddStepAsync(trace.TraceId, 1, thought: "Inspect graph relationships");
        var touchedCount = await reasoning.RecordTouchedEntitiesAsync(step.StepId, [person.EntityId, organization.EntityId]);

        touchedCount.Should().Be(2);
        (await reasoning.GetTouchedEntitiesAsync(step.StepId))
            .Should().BeEquivalentTo([person.EntityId, organization.EntityId]);

        (await longTerm.GetEntityRelationshipsAsync(person.EntityId, Alice))
            .Should().ContainSingle(r => r.RelationshipId == relationship.RelationshipId);
        (await longTerm.GetEntityRelationshipsAsync(organization.EntityId, Alice))
            .Should().ContainSingle(r => r.RelationshipId == relationship.RelationshipId);
        (await longTerm.GetEntityRelationshipsAsync(person.EntityId, Bob))
            .Should().BeEmpty("relationship reads are owner-scoped");
    }

    [Fact]
    public async Task MQ005_TemporalHistory_ExposesSupersessionWithoutContextAssembly()
    {
        using var scope = _provider.CreateScope();
        var longTerm = scope.ServiceProvider.GetRequiredService<ILongTermMemoryService>();
        var history = scope.ServiceProvider.GetRequiredService<IMemoryHistoryService>();
        var decay = scope.ServiceProvider.GetRequiredService<IMemoryDecayService>();

        var loser = await longTerm.AddFactAsync(NewFact(
            "TCK Alice",
            "lives_in",
            "Paris",
            "alice",
            sourceMessageIds: ["source-old"]));
        var winner = await longTerm.AddFactAsync(NewFact(
            "TCK Alice",
            "lives_in",
            "London",
            "alice",
            sourceMessageIds: ["source-new"]));

        (await longTerm.SupersedeFactAsync(loser.FactId, winner.FactId, Alice))
            .Should().BeTrue();
        await decay.UpdateAccessTimestampAsync(winner.FactId, "Fact");

        var records = await history.GetHistoryAsync(new MemoryHistoryQuery
        {
            Kind = MemoryHistoryKind.Fact,
            OwnerId = "alice",
            IncludeShared = false,
            Limit = 10,
        });

        records.Should().Contain(r =>
            r.Id == loser.FactId &&
            r.Status == MemoryHistoryStatus.Invalidated &&
            r.ValidUntilUtc.HasValue &&
            r.SupersededByIds.Contains(winner.FactId) &&
            r.SourceMessageIds.Contains("source-old"));
        records.Should().Contain(r =>
            r.Id == winner.FactId &&
            r.Status == MemoryHistoryStatus.Live &&
            r.AccessCount == 1 &&
            r.ReadAuditCount == 1 &&
            r.LastAccessedAtUtc.HasValue &&
            r.LastReadAuditAtUtc.HasValue);

        var liveOnly = await history.GetHistoryAsync(new MemoryHistoryQuery
        {
            Kind = MemoryHistoryKind.Fact,
            OwnerId = "alice",
            IncludeShared = false,
            IncludeInvalidated = false,
            Limit = 10,
        });

        liveOnly.Should().NotContain(r => r.Id == loser.FactId);
        liveOnly.Should().Contain(r => r.Id == winner.FactId);
    }

    [Fact]
    public async Task MQ006_RetrievalFixture_ReturnsExpectedEntityAtTopForFixedEmbedding()
    {
        using var scope = _provider.CreateScope();
        var longTerm = scope.ServiceProvider.GetRequiredService<ILongTermMemoryService>();

        var target = await longTerm.AddEntityAsync(NewEntity(
            "TCK Vector Target",
            "Concept",
            "alice",
            embedding: TargetEmbedding));
        await longTerm.AddEntityAsync(NewEntity(
            "TCK Vector Distractor",
            "Concept",
            "alice",
            embedding: DistractorEmbedding));

        var results = await longTerm.SearchEntitiesAsync(
            TargetEmbedding,
            limit: 1,
            minScore: 0.0,
            scope: Alice);

        results.Should().ContainSingle();
        results[0].EntityId.Should().Be(target.EntityId);
    }

    private static string Id(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static Entity NewEntity(
        string name,
        string type,
        string? owner,
        float[]? embedding = null) => new()
    {
        EntityId = Id("entity"),
        Name = name,
        Type = type,
        Description = $"Fixture entity for {name}",
        Confidence = 0.95,
        OwnerId = owner,
        Embedding = embedding,
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    private static Fact NewFact(
        string subject,
        string predicate,
        string obj,
        string? owner,
        IReadOnlyList<string>? sourceMessageIds = null) => new()
    {
        FactId = Id("fact"),
        Subject = subject,
        Predicate = predicate,
        Object = obj,
        Confidence = 0.95,
        OwnerId = owner,
        SourceMessageIds = sourceMessageIds ?? Array.Empty<string>(),
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    private static Preference NewPreference(
        string category,
        string text,
        string? owner,
        string? context = null) => new()
    {
        PreferenceId = Id("preference"),
        Category = category,
        PreferenceText = text,
        Context = context,
        Confidence = 0.95,
        OwnerId = owner,
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };
}
