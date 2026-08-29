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

namespace AgentMemory.Tests.Integration.Services;

/// <summary>
/// Localises why the 30.9d render arm produced <b>zero</b> supersession notes in 60 of 60 prompts.
/// </summary>
/// <remarks>
/// <para>
/// The arm ran both levers on. Its store-state gate PASSED — facts placed per question fell to 7.65
/// against the off-state's 8.25, so supersession demonstrably invalidated facts and removed them from
/// recall. Its render-state gate FAILED absolutely: <c>0/60</c> prompts carried a chain. An absolute
/// zero is a disconnected wire, not a weak effect, so this walks the chain one link at a time and
/// asserts each link separately. Whichever stage fails names the break.
/// </para>
/// <para>
/// <b>Everything here is deterministic.</b> Supersession is invoked through the repository rather than
/// through extraction, so no model decides whether a contradiction occurred — a diagnostic whose setup
/// depends on an LLM agreeing to contradict itself can fail for reasons that have nothing to do with
/// the thing under test.
/// </para>
/// <para>
/// <b>The suspected link is the last one.</b> Bitemporal questions are timestamped, so the benchmark
/// calls <c>RecallAsOfAsync</c> for every question, while the renderer's own docstring reasons about
/// the live path's <c>invalidated_at IS NULL</c> filter. No test anywhere covers supersession
/// projection on the as-of path — which is exactly the shape that has now cost this arc three dark
/// mechanisms.
/// </para>
/// </remarks>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class SupersessionRenderingPathIntegrationTests : IAsyncLifetime
{
    private const string Owner = "supersession-render-probe";
    private const string Session = "supersession-render-session";

    private readonly Neo4jIntegrationFixture _fixture;
    private ServiceProvider _provider = null!;
    private string _winnerId = null!;
    private string _loserId = null!;

    public SupersessionRenderingPathIntegrationTests(Neo4jIntegrationFixture fixture) =>
        _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _fixture.CleanDatabaseAsync();
        _provider = BuildProvider();

        var scope = _provider.CreateScope();
        var facts = scope.ServiceProvider.GetRequiredService<IFactRepository>();
        var embedder = _provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
        var now = DateTimeOffset.UtcNow;

        // Facts are retrieved by VECTOR search, so a fact stored without an embedding is invisible to
        // recall no matter how correct everything downstream is. Embedding them here is what makes
        // this a test of the render path rather than a test of an empty context.
        async Task<float[]> Embed(string text) =>
            (await embedder.GenerateAsync([text], cancellationToken: CancellationToken.None))
            [0].Vector.ToArray();

        // The corpus shape this arc has been chasing all week, reduced to two rows: a value that was
        // recorded, and the correction that replaced it.
        var loser = await facts.UpsertAsync(
            new Fact
            {
                FactId = Guid.NewGuid().ToString("n"),
                Subject = "Colm Whitaker",
                Predicate = "works_at",
                Object = "Marchmont",
                Confidence = 0.9,
                CreatedAtUtc = now.AddMinutes(-10),
                OwnerId = Owner,
                Embedding = await Embed("Colm Whitaker works_at Marchmont"),
            },
            CancellationToken.None);

        var winner = await facts.UpsertAsync(
            new Fact
            {
                FactId = Guid.NewGuid().ToString("n"),
                Subject = "Colm Whitaker",
                Predicate = "works_at",
                Object = "Lowick",
                Confidence = 0.9,
                CreatedAtUtc = now,
                OwnerId = Owner,
                Embedding = await Embed("Colm Whitaker works_at Lowick"),
            },
            CancellationToken.None);

        _loserId = loser.FactId;
        _winnerId = winner.FactId;

        await facts.SupersedeAsync(
            _loserId, _winnerId, MemoryScope.For(Owner, includeShared: false), CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }

    /// <summary>LINK 1 — the write half: the edge and the stamp both exist.</summary>
    [Fact]
    public async Task Stage1_SupersedeWritesTheEdgeAndInvalidatesTheLoser()
    {
        using var scope = _provider.CreateScope();
        var facts = scope.ServiceProvider.GetRequiredService<IFactRepository>();

        var loser = await facts.GetByIdAsync(_loserId, CancellationToken.None);
        loser.Should().NotBeNull();
        loser!.InvalidatedAtUtc.Should().NotBeNull("Supersede stamps invalidated_at on the loser");

        var predecessors = await facts.GetSupersessionPredecessorsAsync(
            [_winnerId], 3, CancellationToken.None);

        predecessors.Should().ContainKey(_winnerId,
            "the renderer walks (prev)-[:SUPERSEDED_BY]->(cur) and the winner is `cur`");
        predecessors[_winnerId].Should().NotBeEmpty();
        predecessors[_winnerId][0].Object.Should().Be("Marchmont");
    }

    /// <summary>LINK 2 — the LIVE path renders the note the arm expected to see.</summary>
    [Fact]
    public async Task Stage2_LiveRecallRendersTheSupersessionNote()
    {
        var context = await RecallAsync(asOf: null);

        context.RelevantFacts.Items.Should().Contain(f => f.FactId == _winnerId,
            "live recall keeps the winner; the loser is filtered by invalidated_at IS NULL");
        Note(context).Should().NotBeNullOrWhiteSpace(
            "this is the path SupersessionProjectionFeature was written against");
    }

    /// <summary>
    /// LINK 3 — the AS-OF path, which is the ONLY path the bitemporal benchmark uses.
    /// </summary>
    /// <remarks>
    /// Every bitemporal question carries a QueryTime, so the harness calls <c>RecallAsOfAsync</c> 60
    /// times out of 60 and <c>RecallAsync</c> zero times. If this fails while Stage 2 passes, the
    /// render arm's absolute zero is explained and the defect is in the as-of assembly path.
    /// </remarks>
    [Fact]
    public async Task Stage3_AsOfRecallRendersTheSupersessionNote()
    {
        var context = await RecallAsync(asOf: DateTimeOffset.UtcNow);

        context.RelevantFacts.Items.Should().Contain(f => f.FactId == _winnerId,
            "the as-of clocks admit the winner: it is uninvalidated and its window is open");
        Note(context).Should().NotBeNullOrWhiteSpace(
            "the benchmark reaches the renderer ONLY through this path");
    }

    private static string? Note(MemoryContext context) =>
        context.Projection?.Annotations.Values
            .Select(annotation => annotation.SupersessionNote)
            .FirstOrDefault(note => !string.IsNullOrWhiteSpace(note));

    private async Task<MemoryContext> RecallAsync(DateTimeOffset? asOf)
    {
        // IMemoryService is SCOPED, and the projection features are scoped alongside it. Resolving
        // through an explicit scope is what a hosted caller does; the provider is built with
        // validateScopes so a root resolve fails loudly here instead of quietly handing back a
        // root-scoped graph whose feature set may differ from production's.
        using var scope = _provider.CreateScope();
        var memory = scope.ServiceProvider.GetRequiredService<IMemoryService>();
        var request = new RecallRequest
        {
            SessionId = Session,
            UserId = Owner,
            Query = "Which department was Colm Whitaker at?",
            Options = new RecallOptions
            {
                MaxRecentMessages = 0,
                MaxRelevantMessages = 0,
                MaxEntities = 0,
                MaxPreferences = 0,
                MaxFacts = 10,
                MaxTraces = 0,
                // Zero floor: the stub embedder's vectors carry no semantics, and a similarity gate
                // would make this test measure the embedder rather than the render path.
                MinSimilarityScore = 0,
            },
        };

        var result = asOf is { } instant
            ? await memory.RecallAsOfAsync(request, instant, DateTimeOffset.UtcNow, CancellationToken.None)
            : await memory.RecallAsync(request, CancellationToken.None);
        return result.Context;
    }

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNeo4jAgentMemory(
            new MemoryOptions
            {
                // The lever the render arm set, wired the same way the eval profile wires it.
                Projection = MemoryProjectionOptions.Default with { ResolveSupersessions = true },
                Extraction = { SupersedeReplacedFacts = true },
            },
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
        return services.BuildServiceProvider(validateScopes: true);
    }
}
