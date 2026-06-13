using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Neo4j.Services;
using AgentMemory.Tests.Integration.Fixtures;
using Neo4j.Driver;
using NSubstitute;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// End-to-end memory-hygiene / consolidation (PR #113): dry-run reports counts without mutating;
/// apply archives expired conversations, removes duplicate preferences, and writes a
/// <c>:ConsolidationRun</c> audit node.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class ConsolidationIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jConsolidationService _svc;
    private readonly Neo4jConversationRepository _conversations;
    private readonly Neo4jEntityRepository _entities;
    private readonly Neo4jPreferenceRepository _prefs;
    private readonly Neo4jReasoningTraceRepository _traces;

    private static readonly DateTimeOffset Now = new(2026, 6, 6, 0, 0, 0, TimeSpan.Zero);

    public ConsolidationIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(Now);
        var idGen = Substitute.For<IIdGenerator>();
        idGen.GenerateId().Returns(_ => $"run-{Guid.NewGuid():N}");

        _svc = new Neo4jConsolidationService(fixture.TransactionRunner, clock, idGen, NullLogger<Neo4jConsolidationService>.Instance);
        _conversations = new Neo4jConversationRepository(fixture.TransactionRunner, NullLogger<Neo4jConversationRepository>.Instance);
        _entities = new Neo4jEntityRepository(fixture.TransactionRunner, NullLogger<Neo4jEntityRepository>.Instance);
        _prefs = new Neo4jPreferenceRepository(fixture.TransactionRunner, NullLogger<Neo4jPreferenceRepository>.Instance);
        _traces = new Neo4jReasoningTraceRepository(fixture.TransactionRunner, NullLogger<Neo4jReasoningTraceRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly ConsolidationOptions Opts = new()
    {
        ConversationExpiry = TimeSpan.FromDays(90),
        LongTraceStepThreshold = 2,
    };

    private async Task SeedAsync()
    {
        // Conversations: one expired (updated 200d ago), one recent.
        await _conversations.UpsertAsync(new Conversation { ConversationId = "c-old", SessionId = "s1", CreatedAtUtc = Now.AddDays(-200), UpdatedAtUtc = Now.AddDays(-200) });
        await _conversations.UpsertAsync(new Conversation { ConversationId = "c-new", SessionId = "s2", CreatedAtUtc = Now.AddDays(-1), UpdatedAtUtc = Now.AddDays(-1) });

        // Preferences: two exact duplicates (same owner+category+text) + one distinct.
        await _prefs.UpsertAsync(new Preference { PreferenceId = "p-1", Category = "style", PreferenceText = "dark mode", OwnerId = "alice", Confidence = 0.8, CreatedAtUtc = Now.AddDays(-2) });
        await _prefs.UpsertAsync(new Preference { PreferenceId = "p-2", Category = "style", PreferenceText = "dark mode", OwnerId = "alice", Confidence = 0.8, CreatedAtUtc = Now.AddDays(-1) });
        await _prefs.UpsertAsync(new Preference { PreferenceId = "p-3", Category = "style", PreferenceText = "concise answers", OwnerId = "alice", Confidence = 0.8, CreatedAtUtc = Now });

        // Entities: two duplicates (same owner+name+type) + one distinct.
        await _entities.UpsertAsync(new Entity { EntityId = "e-1", Name = "Acme", Type = "Organization", OwnerId = "alice", Confidence = 0.8, CreatedAtUtc = Now });
        await _entities.UpsertAsync(new Entity { EntityId = "e-2", Name = "acme", Type = "Organization", OwnerId = "alice", Confidence = 0.8, CreatedAtUtc = Now });
        await _entities.UpsertAsync(new Entity { EntityId = "e-3", Name = "Globex", Type = "Organization", OwnerId = "alice", Confidence = 0.8, CreatedAtUtc = Now });

        // A long reasoning trace (3 steps > threshold 2).
        await _traces.AddAsync(new ReasoningTrace { TraceId = "t-long", SessionId = "s1", Task = "do a big thing", StartedAtUtc = Now });
        await using var session = _fixture.Driver.AsyncSession();
        await session.RunAsync(
            "MATCH (t:ReasoningTrace {id:$tid}) UNWIND range(1,3) AS i CREATE (s:ReasoningStep {id: $tid + '-' + toString(i)}) CREATE (t)-[:HAS_STEP]->(s)",
            new { tid = "t-long" });
    }

    private async Task<long> CountAsync(string cypher)
    {
        await using var session = _fixture.Driver.AsyncSession();
        var cursor = await session.RunAsync(cypher);
        var record = await cursor.SingleAsync();
        return global::Neo4j.Driver.ValueExtensions.As<long>(record["c"]);
    }

    [Fact]
    public async Task DryRun_ReportsCounts_WithoutMutating()
    {
        await SeedAsync();

        var report = await _svc.ConsolidateAsync(Opts with { DryRun = true });

        report.DryRun.Should().BeTrue();
        report.ConversationsArchived.Should().Be(1);
        report.DuplicatePreferencesRemoved.Should().Be(1);
        report.DuplicateEntitiesDetected.Should().Be(1);
        report.LongTraceCandidates.Should().Be(1);

        // Nothing mutated.
        (await CountAsync("MATCH (c:Conversation) WHERE c.archived = true RETURN count(c) AS c")).Should().Be(0);
        (await CountAsync("MATCH (p:Preference) RETURN count(p) AS c")).Should().Be(3);
        (await CountAsync("MATCH (r:ConsolidationRun) RETURN count(r) AS c")).Should().Be(0);
    }

    [Fact]
    public async Task Apply_Mutates_AndWritesAuditNode()
    {
        await SeedAsync();

        var report = await _svc.ConsolidateAsync(Opts with { DryRun = false });

        report.DryRun.Should().BeFalse();
        report.ConversationsArchived.Should().Be(1);
        report.DuplicatePreferencesRemoved.Should().Be(1);

        (await CountAsync("MATCH (c:Conversation {id:'c-old'}) WHERE c.archived = true RETURN count(c) AS c")).Should().Be(1);
        (await CountAsync("MATCH (c:Conversation {id:'c-new'}) WHERE coalesce(c.archived,false) = false RETURN count(c) AS c")).Should().Be(1);
        // D7: duplicate collapse is non-destructive — all three nodes are KEPT; the older duplicate is
        // soft-invalidated (dropped from live recall) and linked to the survivor, not deleted.
        (await CountAsync("MATCH (p:Preference) RETURN count(p) AS c")).Should().Be(3); // nothing deleted
        (await CountAsync("MATCH (p:Preference) WHERE p.invalidated_at IS NULL RETURN count(p) AS c")).Should().Be(2); // one duplicate closed → 2 live
        (await CountAsync("MATCH (:Preference)-[:SUPERSEDED_BY]->(:Preference) RETURN count(*) AS c")).Should().Be(1); // dup linked to survivor
        (await CountAsync("MATCH (p:Preference {id:'p-3'}) RETURN count(p) AS c")).Should().Be(1); // distinct kept
        (await CountAsync($"MATCH (r:ConsolidationRun {{id:'{report.RunId}'}}) RETURN count(r) AS c")).Should().Be(1);
    }

    [Fact]
    public async Task Apply_ArchivedConversation_IsReadBackViaRepository()
    {
        await SeedAsync();

        await _svc.ConsolidateAsync(Opts with { DryRun = false });

        // Conversation.Archived now round-trips: the repository reads c.archived back into the model.
        var archived = await _conversations.GetByIdAsync("c-old");
        var active = await _conversations.GetByIdAsync("c-new");

        archived.Should().NotBeNull();
        archived!.Archived.Should().BeTrue("the expired conversation was archived by consolidation");
        active.Should().NotBeNull();
        active!.Archived.Should().BeFalse("the recent conversation was not archived");
    }
}
