using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AgentMemory;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Schema;
using AgentMemory.Core.Stubs;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Tests.Integration.Fixtures;

namespace AgentMemory.Tests.Integration;

/// <summary>
/// Full-circle "shakedown": builds the ENTIRE stack through the real meta <c>AddNeo4jAgentMemory</c> DI
/// container (so resolution itself is under test — this is how the meta-DI <c>IClock</c>/<c>IIdGenerator</c>
/// gap would have been caught), pointed at a live Neo4j, and exercises a realistic multi-user flow across
/// every subsystem: short-term, long-term (with owner isolation), reasoning + :TOUCHED, entity feedback,
/// consolidation, and conflict detection. Catches cross-feature and wiring errors that component tests miss.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class ShakedownEndToEndTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private ServiceProvider _provider = null!;

    private static readonly MemoryScope Alice = MemoryScope.For("alice");
    private static readonly MemoryScope Bob = MemoryScope.For("bob");

    public ShakedownEndToEndTests(Neo4jIntegrationFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _fixture.CleanDatabaseAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNeo4jAgentMemory(
            // Defaults already enable entity/fact/preference embeddings.
            configureMemory: _ => { },
            configureNeo4j: o =>
            {
                o.Uri = _fixture.ConnectionString;
                o.Username = _fixture.User;
                o.Password = _fixture.Password;
                o.Database = "neo4j";
                o.EmbeddingDimensions = Neo4jIntegrationFixture.TestEmbeddingDimensions;
            });
        // Real, deterministic embeddings at the fixture's dimensionality (same text → same vector).
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

    private static Entity NewEntity(string name, string? owner) => new()
    {
        EntityId = $"e-{Guid.NewGuid():N}",
        Name = name,
        Type = "Organization",
        Confidence = 0.7,
        OwnerId = owner,
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void AllTopLevelServices_ResolveFromTheMetaContainer()
    {
        // The DI-gap catch: build every top-level service from the real container in a scope.
        using var scope = _provider.CreateScope();
        var sp = scope.ServiceProvider;

        sp.GetRequiredService<IMemoryService>().Should().NotBeNull();
        sp.GetRequiredService<IMemoryRecall>().Should().NotBeNull();
        sp.GetRequiredService<IMemoryIngestion>().Should().NotBeNull();
        sp.GetRequiredService<IMemoryMaintenance>().Should().NotBeNull();
        sp.GetRequiredService<IShortTermMemoryService>().Should().NotBeNull();
        sp.GetRequiredService<ILongTermMemoryService>().Should().NotBeNull();
        sp.GetRequiredService<IReasoningMemoryService>().Should().NotBeNull();
        sp.GetRequiredService<IMemoryContextAssembler>().Should().NotBeNull();
        sp.GetRequiredService<IMemoryQueryFacade>().Should().NotBeNull();
        sp.GetRequiredService<IConsolidationService>().Should().NotBeNull();
        sp.GetRequiredService<IConflictDetectionService>().Should().NotBeNull();
        // The Neo4j DI must Replace the Core portable no-op with the server-side Cypher decay impl.
        sp.GetRequiredService<IMemoryDecayService>().Should().BeOfType<AgentMemory.Neo4j.Services.Neo4jMemoryDecayService>();
        sp.GetRequiredService<IMigrationRunner>().Should().NotBeNull();
        sp.GetRequiredService<ISchemaBootstrapper>().Should().NotBeNull();
        sp.GetRequiredService<IMemoryExtractionPipeline>().Should().NotBeNull();
        // G4: custom entity-schema persistence is wired through the meta container too.
        sp.GetRequiredService<ISchemaManager>().Should().NotBeNull();
    }

    [Fact]
    public async Task FullCircle_ShortTerm_LongTermIsolation_Reasoning_Feedback_Maintenance()
    {
        using var scope = _provider.CreateScope();
        var sp = scope.ServiceProvider;
        var memory = sp.GetRequiredService<IMemoryService>();
        var longTerm = sp.GetRequiredService<ILongTermMemoryService>();
        var reasoning = sp.GetRequiredService<IReasoningMemoryService>();
        var consolidation = sp.GetRequiredService<IConsolidationService>();
        var conflicts = sp.GetRequiredService<IConflictDetectionService>();

        // ── Short-term: messages round-trip via the assembled recall context ──
        var session = $"s-{Guid.NewGuid():N}";
        await memory.AddMessageAsync(session, session, "user", "Hello shakedown");
        await memory.AddMessageAsync(session, session, "assistant", "Hi there");
        var recall = await memory.RecallAsync(new RecallRequest { SessionId = session, Query = "hello" });
        recall.Context.RecentMessages.Items.Should().HaveCountGreaterThanOrEqualTo(2);

        // ── Long-term: owner isolation through the full DI stack ──
        var alice = await longTerm.AddEntityAsync(NewEntity("AliceCorp", "alice"));
        await longTerm.AddEntityAsync(NewEntity("BobCorp", "bob"));
        await longTerm.AddEntityAsync(NewEntity("SharedCorp", owner: null));

        (await longTerm.GetEntitiesByNameAsync("AliceCorp", true, Alice)).Should().ContainSingle();
        (await longTerm.GetEntitiesByNameAsync("BobCorp", true, Alice)).Should().BeEmpty("bob's private entity is invisible to alice");
        (await longTerm.GetEntitiesByNameAsync("SharedCorp", true, Alice)).Should().ContainSingle("shared entities are visible to everyone");
        (await longTerm.GetEntitiesByNameAsync("AliceCorp", true, Bob)).Should().BeEmpty("alice's private entity is invisible to bob");

        // ── Entity feedback: owner-scoped write ──
        var bumped = await longTerm.RecordEntityFeedbackAsync(alice.EntityId, positive: true, scope: Alice);
        bumped.Should().NotBeNull();
        bumped!.Confidence.Should().BeGreaterThan(alice.Confidence);
        (await longTerm.RecordEntityFeedbackAsync(alice.EntityId, positive: true, scope: Bob))
            .Should().BeNull("bob cannot apply feedback to alice's private entity");

        // ── Reasoning: trace + step + :TOUCHED + complete + read-back ──
        var trace = await reasoning.StartTraceAsync(session, "do the shakedown", ownerId: "alice");
        var step = await reasoning.AddStepAsync(trace.TraceId, 1, thought: "look up AliceCorp");
        var linked = await reasoning.RecordTouchedEntitiesAsync(step.StepId, new[] { alice.EntityId });
        linked.Should().Be(1);
        await reasoning.CompleteTraceAsync(trace.TraceId, outcome: "done", success: true);
        var (readTrace, steps) = await reasoning.GetTraceWithStepsAsync(trace.TraceId);
        readTrace.Success.Should().BeTrue();
        steps.Should().ContainSingle();
        (await reasoning.GetTouchedEntitiesAsync(step.StepId)).Should().Contain(alice.EntityId);

        // ── Maintenance: consolidation (dry-run) + conflict detection both run end-to-end ──
        var report = await consolidation.ConsolidateAsync(new ConsolidationOptions { DryRun = true });
        report.DryRun.Should().BeTrue();

        // Two contradicting facts written via the repository (distinct nodes regardless of dedup),
        // then detected end-to-end through the resolved conflict service.
        var facts = sp.GetRequiredService<IFactRepository>();
        await facts.UpsertAsync(NewFact("Alice", "works_at", "Acme"));
        await facts.UpsertAsync(NewFact("Alice", "works_at", "Globex"));
        var conflictReport = await conflicts.DetectConflictsAsync();
        conflictReport.FactConflicts.Should().Contain(c => c.Subject == "Alice" && c.Predicate == "works_at");
    }

    [Fact]
    public async Task CustomSchema_SavesAndLoadsActiveVersion_ThroughTheMetaContainer()
    {
        // G4 end-to-end through the real meta DI: persist a custom entity schema and read the active
        // version back, exercising Neo4jSchemaManager + SchemaPersistenceQueries against the live DB.
        using var scope = _provider.CreateScope();
        var schemas = scope.ServiceProvider.GetRequiredService<ISchemaManager>();

        var schema = SchemaLoader.CreateForTypes(["PATIENT", "DRUG"]) with
        {
            Name = "shakedown-medical",
            Version = "1.0",
            Description = "Shakedown custom schema"
        };
        await schemas.SaveSchemaAsync(schema);

        (await schemas.SchemaExistsAsync("shakedown-medical")).Should().BeTrue();
        var loaded = await schemas.LoadSchemaAsync("shakedown-medical");
        loaded.Should().NotBeNull();
        loaded!.Version.Should().Be("1.0");
        loaded.GetEntityTypeNames().Should().BeEquivalentTo(["PATIENT", "DRUG"]);
    }

    private static Fact NewFact(string subject, string predicate, string obj) => new()
    {
        FactId = $"f-{Guid.NewGuid():N}",
        Subject = subject,
        Predicate = predicate,
        Object = obj,
        Confidence = 0.9,
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };
}
