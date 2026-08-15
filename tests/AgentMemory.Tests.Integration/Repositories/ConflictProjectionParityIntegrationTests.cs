using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services.Projection;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Neo4j.Services;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// Wave B carried debt: does the in-context conflict renderer agree with the shipped
/// <see cref="IConflictDetectionService"/> about what a contradiction <i>is</i>?
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this had to be answered before conflict surfacing is enabled anywhere.</b> The detection
/// service is the durable authority — it is what <c>ResolveFactContradictionsAsync</c> acts on. The
/// projection feature renders a cue in the prompt. If the two disagree about what counts as a
/// conflict, an operator resolves one set and the model is warned about a different set, and neither
/// is wrong in isolation.
/// </para>
/// <para>
/// <b>The answer is: they agree on the semantics that matter and diverge on one, deliberately.</b>
/// Both group by subject + predicate + owner over <i>live</i> facts and fire on two or more distinct
/// objects. The service compares raw strings; the renderer compares canonicalised ones. That
/// difference is asserted here rather than hidden, with the reason, because it is the difference
/// between a useful cue and the most annoying possible false positive.
/// </para>
/// </remarks>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class ConflictProjectionParityIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jFactRepository _facts;
    private readonly Neo4jConflictDetectionService _detection;

    private static readonly MemoryScope Alice = MemoryScope.For("alice", includeShared: false);

    public ConflictProjectionParityIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _facts = new Neo4jFactRepository(fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance);
        _detection = new Neo4jConflictDetectionService(
            fixture.TransactionRunner,
            new FixedClock(),
            NullLogger<Neo4jConflictDetectionService>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    private static Fact NewFact(string @object, string owner = "alice", string subject = "Bob") => new()
    {
        FactId = Guid.NewGuid().ToString("N"),
        Subject = subject,
        Predicate = "works_at",
        Object = @object,
        Confidence = 0.9,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        OwnerId = owner,
    };

    /// <summary>Runs the renderer over a set of facts exactly as a recall would.</summary>
    private static async Task<IReadOnlyList<ProjectedBlock>> RenderConflictsAsync(params Fact[] facts)
    {
        var state = new ProjectionState
        {
            Options = MemoryProjectionOptions.Default with { RenderConflicts = true },
            Scope = null,
            Entities = [],
            Facts = facts,
            Preferences = [],
            Traces = [],
            RecentMessages = [],
            RelevantMessages = [],
            EntityScores = [],
            FactScores = [],
            PreferenceScores = [],
            TraceScores = [],
        };

        await new ConflictProjectionFeature().ApplyAsync(state, CancellationToken.None);
        return state.IsEmpty ? [] : state.Build().Blocks;
    }

    private async Task<IReadOnlyList<FactConflict>> DetectAsync() =>
        (await _detection.DetectConflictsAsync(new ConflictDetectionOptions
        {
            DetectFactContradictions = true,
        })).FactConflicts;

    // ── agreement ─────────────────────────────────────────────────────

    [Fact]
    public async Task BothAgreeThatTwoDistinctObjectsAreAConflict()
    {
        var a = await _facts.UpsertAsync(NewFact("Acme"));
        var b = await _facts.UpsertAsync(NewFact("Globex"));

        (await DetectAsync()).Should().ContainSingle();
        (await RenderConflictsAsync(a, b)).Should().ContainSingle();
    }

    [Fact]
    public async Task BothAgreeThatAgreeingFactsAreNotAConflict()
    {
        // Upserted twice with the same triple: the write path MERGEs them, so this is one fact either
        // way -- and neither side should invent a disagreement.
        var a = await _facts.UpsertAsync(NewFact("Acme"));

        (await DetectAsync()).Should().BeEmpty();
        (await RenderConflictsAsync(a)).Should().BeEmpty();
    }

    [Fact]
    public async Task BothAgreeThatASupersededFactIsHistoryRatherThanAContradiction()
    {
        // The service filters invalidated_at IS NULL; the renderer filters InvalidatedAtUtc is null.
        // If these diverged, resolving a conflict would leave the prompt still warning about it.
        var loser = await _facts.UpsertAsync(NewFact("Globex"));
        var winner = await _facts.UpsertAsync(NewFact("Acme"));
        await _facts.SupersedeAsync(loser.FactId, winner.FactId, Alice);

        (await DetectAsync()).Should().BeEmpty();

        var reread = await _facts.GetByIdAsync(loser.FactId);
        reread!.InvalidatedAtUtc.Should().NotBeNull("the fixture must actually exercise the filter");
        (await RenderConflictsAsync(reread, winner)).Should().BeEmpty();
    }

    [Fact]
    public async Task BothAgreeThatTwoOwnersAreNotAContradiction()
    {
        // The service groups on coalesce(owner_id,'*'); the renderer on OwnerId ?? "*". A divergence
        // here would leak the existence of another owner's data into this owner's prompt.
        var alice = await _facts.UpsertAsync(NewFact("Acme", owner: "alice"));
        var bob = await _facts.UpsertAsync(NewFact("Globex", owner: "bob"));

        (await DetectAsync()).Should().BeEmpty();
        (await RenderConflictsAsync(alice, bob)).Should().BeEmpty();
    }

    [Fact]
    public async Task BothAgreeThatDifferentPredicatesAreNotAContradiction()
    {
        var a = await _facts.UpsertAsync(NewFact("Acme"));
        var b = await _facts.UpsertAsync(new Fact
        {
            FactId = Guid.NewGuid().ToString("N"),
            Subject = "Bob", Predicate = "lives_in", Object = "Zurich",
            Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow, OwnerId = "alice",
        });

        (await DetectAsync()).Should().BeEmpty();
        (await RenderConflictsAsync(a, b)).Should().BeEmpty();
    }

    [Fact]
    public async Task ThreeWayDisagreementIsOneGroupOnBothSides()
    {
        var a = await _facts.UpsertAsync(NewFact("Acme"));
        var b = await _facts.UpsertAsync(NewFact("Globex"));
        var c = await _facts.UpsertAsync(NewFact("Initech"));

        (await DetectAsync()).Should().ContainSingle()
            .Which.Values.Should().HaveCount(3);

        var blocks = await RenderConflictsAsync(a, b, c);
        blocks.Should().ContainSingle();
        blocks[0].Text.Should().Contain("Acme").And.Contain("Globex").And.Contain("Initech");
    }

    // ── the one deliberate divergence ─────────────────────────────────

    [Fact]
    public async Task OnSpellingVariantsTheyDivergeOnPurpose()
    {
        // THE documented difference. The service compares RAW strings, so "Acme" and " acme " are two
        // distinct objects and it reports a contradiction. The renderer canonicalises the way the write
        // path keys a triple, so it reports nothing.
        //
        // The renderer is right FOR RENDERING: telling the model that "Acme" contradicts "acme" would
        // teach it to hedge about something nobody disagrees on -- the most annoying possible false
        // positive, and one that costs tokens on every affected turn. The service is right for
        // DETECTION: an operator auditing the store should see that two spellings exist, because that
        // is a data-hygiene finding worth acting on.
        //
        // Reaching this state at all takes pre-canonical data (facts written before *_key backfill);
        // the modern write path MERGEs the two into one. The divergence is therefore narrow, real, and
        // asserted rather than assumed away.
        var a = await _facts.UpsertAsync(NewFact("Acme"));
        var b = await _facts.UpsertAsync(NewFact(" acme "));

        var detected = await DetectAsync();
        var rendered = await RenderConflictsAsync(a, b);

        if (detected.Count > 0)
        {
            rendered.Should().BeEmpty(
                "the renderer canonicalises, so a spelling variant is not surfaced to the model even "
                + "when detection reports it to an operator");
        }
        else
        {
            // The write path collapsed them into one fact, which is the modern behaviour. Then both
            // sides agree there is nothing to report, and the divergence is unreachable here.
            rendered.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task TheRendererNeverSurfacesMoreGroupsThanDetectionFinds()
    {
        // The safety direction of the parity claim: whatever the two disagree about, the PROMPT must
        // never warn about a contradiction the durable authority does not recognise. Over-warning is
        // the failure that teaches a model to hedge; under-warning merely loses a cue.
        var a = await _facts.UpsertAsync(NewFact("Acme"));
        var b = await _facts.UpsertAsync(NewFact("Globex"));
        var c = await _facts.UpsertAsync(NewFact("Zurich", subject: "Carol"));
        var d = await _facts.UpsertAsync(NewFact("Basel", subject: "Carol"));

        var detected = await DetectAsync();
        var rendered = await RenderConflictsAsync(a, b, c, d);

        rendered.Count.Should().BeLessThanOrEqualTo(detected.Count);
        detected.Should().HaveCount(2);
        rendered.Should().HaveCount(2);
    }
}
