using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// Procedural memory's two correctness falsifiers, against a live database (PLAN 7.5).
/// </summary>
/// <remarks>
/// <para>
/// Deterministic and model-free. Promotion is not a semantic property, so neither falsifier needs a
/// corpus, an embedding model or a judge — they answer the only two questions that decide whether the
/// capability exists at all: <b>procedure_recall_at_1</b> (can a procedure be retrieved <i>as</i> one)
/// and <b>promoted_survived_prune</b> (does it survive retention).
/// </para>
/// <para>
/// The second is the load-bearing one. <c>PruneSessionTracesAsync</c> orders by <c>started_at</c> with
/// age as its <b>only</b> criterion, and it fires on every trace creation once
/// <c>MaxTracesPerSession</c> is set. Without the exemption, promoting a trace is undone by the next
/// few turns: the feature would ship, read correctly in every unit test that never prunes, and be a
/// no-op in production.
/// </para>
/// <para>
/// These run against Neo4j rather than a substitute because both properties live entirely in Cypher —
/// the <c>trace_kind</c> predicate on the vector search and the <c>coalesce(...) &lt;&gt; 'procedure'</c>
/// clause in the prune. A mocked repository would assert that the arguments were passed, which is the
/// part that was never in doubt.
/// </para>
/// </remarks>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class ProcedurePromotionIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jReasoningTraceRepository _traceRepo;

    private static readonly float[] Embedding = [0.3f, 0.1f, 0.4f, 0.2f];

    public ProcedurePromotionIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _traceRepo = new Neo4jReasoningTraceRepository(
            fixture.TransactionRunner, NullLogger<Neo4jReasoningTraceRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static ReasoningTrace MakeTrace(
        string sessionId, int dayOffset, TraceKind kind, string? ownerId = "alice") => new()
    {
        TraceId = $"trace-{Guid.NewGuid():N}",
        SessionId = sessionId,
        OwnerId = ownerId,
        Task = $"Task day {dayOffset}",
        TaskEmbedding = Embedding,
        Kind = kind,
        StartedAtUtc = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(dayOffset)
    };

    // ---- procedure_recall_at_1 ------------------------------------------------

    [Fact]
    public async Task ProceduresOnlySearch_ReturnsThePromotedTrace_AndNotTheEpisode()
    {
        var sessionId = $"session-{Guid.NewGuid():N}";
        var procedure = MakeTrace(sessionId, 0, TraceKind.Procedure);
        var episode = MakeTrace(sessionId, 1, TraceKind.Episode);
        await _traceRepo.AddAsync(procedure);
        await _traceRepo.AddAsync(episode);

        var hits = await _traceRepo.SearchByTaskVectorAsync(
            Embedding, proceduresOnly: true, successFilter: null, limit: 10, minScore: 0.0,
            scope: MemoryScope.For("alice"));

        hits.Should().ContainSingle().Which.Trace.TraceId.Should().Be(procedure.TraceId);
        hits[0].Trace.Kind.Should().Be(TraceKind.Procedure);
    }

    [Fact]
    public async Task ExcludingProcedures_ReturnsOnlyEpisodes()
    {
        // The other direction of the same predicate. A filter that is silently ignored passes the test
        // above (the procedure is in the result set either way) and fails only here.
        var sessionId = $"session-{Guid.NewGuid():N}";
        var procedure = MakeTrace(sessionId, 0, TraceKind.Procedure);
        var episode = MakeTrace(sessionId, 1, TraceKind.Episode);
        await _traceRepo.AddAsync(procedure);
        await _traceRepo.AddAsync(episode);

        var hits = await _traceRepo.SearchByTaskVectorAsync(
            Embedding, proceduresOnly: false, successFilter: null, limit: 10, minScore: 0.0,
            scope: MemoryScope.For("alice"));

        hits.Should().ContainSingle().Which.Trace.TraceId.Should().Be(episode.TraceId);
    }

    [Fact]
    public async Task TheUnfilteredSearch_StillReturnsBothKinds()
    {
        // The byte-identical guarantee for every existing caller and for third-party implementers of
        // IReasoningTraceRepository: no filter means no filtering, not "episodes only".
        var sessionId = $"session-{Guid.NewGuid():N}";
        await _traceRepo.AddAsync(MakeTrace(sessionId, 0, TraceKind.Procedure));
        await _traceRepo.AddAsync(MakeTrace(sessionId, 1, TraceKind.Episode));

        var hits = await _traceRepo.SearchByTaskVectorAsync(
            Embedding, successFilter: null, limit: 10, minScore: 0.0, scope: MemoryScope.For("alice"));

        hits.Select(h => h.Trace.Kind).Should().BeEquivalentTo(
            new[] { TraceKind.Procedure, TraceKind.Episode });
    }

    [Fact]
    public async Task LegacyTracesWithNoStoredKind_AreTreatedAsEpisodes()
    {
        // Every trace written before 0011 has no trace_kind property at all. The queries coalesce it to
        // 'episode'; if that coalesce were dropped, a proceduresOnly:false search would silently stop
        // returning the entire pre-migration corpus -- a null-vs-missing bug that no new-data test sees.
        var sessionId = $"session-{Guid.NewGuid():N}";
        var legacy = MakeTrace(sessionId, 0, TraceKind.Episode);
        await _traceRepo.AddAsync(legacy);
        await _fixture.TransactionRunner.WriteAsync(async runner =>
        {
            await runner.RunAsync(
                "MATCH (t:ReasoningTrace {id: $id}) REMOVE t.trace_kind", new { id = legacy.TraceId });
            return true;
        });

        var episodes = await _traceRepo.SearchByTaskVectorAsync(
            Embedding, proceduresOnly: false, successFilter: null, limit: 10, minScore: 0.0,
            scope: MemoryScope.For("alice"));
        var procedures = await _traceRepo.SearchByTaskVectorAsync(
            Embedding, proceduresOnly: true, successFilter: null, limit: 10, minScore: 0.0,
            scope: MemoryScope.For("alice"));

        episodes.Should().ContainSingle().Which.Trace.TraceId.Should().Be(legacy.TraceId,
            "a trace stored before the migration is an episode, not an absence");
        procedures.Should().BeEmpty();
    }

    // ---- promoted_survived_prune ---------------------------------------------

    [Fact]
    public async Task APromotedProcedure_SurvivesRetentionPruning()
    {
        // THE falsifier. The procedure is the OLDEST trace in the session, so age alone deletes it.
        var sessionId = $"session-{Guid.NewGuid():N}";
        var procedure = MakeTrace(sessionId, 0, TraceKind.Procedure);
        await _traceRepo.AddAsync(procedure);
        for (var day = 1; day <= 5; day++)
            await _traceRepo.AddAsync(MakeTrace(sessionId, day, TraceKind.Episode));

        var pruned = await _traceRepo.PruneSessionTracesAsync(sessionId, maxToKeep: 2, ownerId: "alice");

        (await _traceRepo.GetByIdAsync(procedure.TraceId)).Should().NotBeNull(
            "a promoted procedure is exempt from recency pruning -- without the exemption, promotion is "
            + "undone by the next few traces and the capability does not exist");
        pruned.Should().Be(3, "the 3 oldest EPISODES are evicted; the procedure is not counted or deleted");
    }

    [Fact]
    public async Task OrdinaryEpisodes_AreStillPruned()
    {
        // The exemption must not disable retention. A cap that silently stops capping turns a bounded
        // store into an unbounded one, which is worse than having no cap, because nobody looks.
        var sessionId = $"session-{Guid.NewGuid():N}";
        var oldest = MakeTrace(sessionId, 0, TraceKind.Episode);
        await _traceRepo.AddAsync(oldest);
        for (var day = 1; day <= 5; day++)
            await _traceRepo.AddAsync(MakeTrace(sessionId, day, TraceKind.Episode));

        await _traceRepo.PruneSessionTracesAsync(sessionId, maxToKeep: 2, ownerId: "alice");

        (await _traceRepo.GetByIdAsync(oldest.TraceId)).Should().BeNull();
    }

    [Fact]
    public async Task ProceduresDoNotConsumeTheRetentionBudget()
    {
        // The subtle failure the exemption could still have: exempting a procedure from DELETION while
        // still COUNTING it toward maxToKeep would evict a live episode to make room for a trace that
        // was never at risk -- retention silently tightening as procedures accumulate.
        var sessionId = $"session-{Guid.NewGuid():N}";
        await _traceRepo.AddAsync(MakeTrace(sessionId, 0, TraceKind.Procedure));
        await _traceRepo.AddAsync(MakeTrace(sessionId, 1, TraceKind.Procedure));
        var keptEpisodes = new[]
        {
            MakeTrace(sessionId, 2, TraceKind.Episode),
            MakeTrace(sessionId, 3, TraceKind.Episode),
        };
        foreach (var e in keptEpisodes) await _traceRepo.AddAsync(e);

        var pruned = await _traceRepo.PruneSessionTracesAsync(sessionId, maxToKeep: 2, ownerId: "alice");

        pruned.Should().Be(0, "2 episodes with a cap of 2 is not over budget -- the procedures do not count");
        foreach (var e in keptEpisodes)
            (await _traceRepo.GetByIdAsync(e.TraceId)).Should().NotBeNull();
    }

    [Fact]
    public async Task TheExemption_DoesNotWeakenOwnerConfinement()
    {
        // The exemption clause was added to the WHERE of a destructive, session-keyed delete. That is
        // exactly the edit where an owner guard gets weakened by accident, so the #40 cross-eviction
        // property is re-asserted with procedures in the mix rather than assumed to still hold.
        var sessionId = $"session-{Guid.NewGuid():N}";
        var bobProcedure = MakeTrace(sessionId, 0, TraceKind.Procedure, ownerId: "bob");
        var bobEpisode = MakeTrace(sessionId, 1, TraceKind.Episode, ownerId: "bob");
        await _traceRepo.AddAsync(bobProcedure);
        await _traceRepo.AddAsync(bobEpisode);
        for (var day = 0; day < 4; day++)
            await _traceRepo.AddAsync(MakeTrace(sessionId, day, TraceKind.Episode, ownerId: "alice"));

        await _traceRepo.PruneSessionTracesAsync(sessionId, maxToKeep: 1, ownerId: "alice");

        (await _traceRepo.GetByIdAsync(bobProcedure.TraceId)).Should().NotBeNull();
        (await _traceRepo.GetByIdAsync(bobEpisode.TraceId)).Should().NotBeNull(
            "an owner-confined prune must never reach another owner's traces, whatever else it filters on");
    }
}
