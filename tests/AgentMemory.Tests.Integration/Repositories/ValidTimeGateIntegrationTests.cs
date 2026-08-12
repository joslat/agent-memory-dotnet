using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// The valid-time gate's falsifier (PLAN 1.5): with the gate off and on, results must be identical
/// unless something actually wrote a validity bound.
/// </summary>
/// <remarks>
/// <para>
/// The gate filters on <c>valid_from</c>/<c>valid_until</c>. If turning it on changes results over a
/// corpus that nobody set validity bounds on, then an <b>unaudited writer of validity bounds exists</b>
/// and must be found before the option ships. The audit is only as good as the inventory, so these
/// tests pin the inventory rather than assert a vague "nothing changed".
/// </para>
/// <para>
/// <b>Two writers exist, both deliberate.</b> Temporal extraction sets bounds when
/// <c>TemporalValidityMode.Extract</c> asks the model for them, and <b>supersession</b> stamps
/// <c>valid_until</c> when a fact is closed. The second is the one worth testing, because write-time
/// supersession (9.1) made it reachable on the ingestion path rather than only from an offline
/// hygiene pass.
/// </para>
/// <para>
/// The interesting result is that supersession changes nothing here: it stamps <c>invalidated_at</c>
/// too, and the existing transaction-clock filter already removes the fact from live recall. So the
/// valid-time gate is <b>redundant</b> for superseded facts and load-bearing only for facts whose
/// real-world window was stated. Worth knowing before anyone reaches for it to hide superseded data.
/// </para>
/// </remarks>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class ValidTimeGateIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jFactRepository _facts;

    private static readonly float[] Embedding = [0.2f, 0.4f, 0.4f, 0.2f];

    public ValidTimeGateIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _facts = new Neo4jFactRepository(
            fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private Task<Fact> StoreAsync(
        string @object, DateTimeOffset? validFrom = null, DateTimeOffset? validUntil = null) =>
        _facts.UpsertAsync(new Fact
        {
            FactId = $"fact-{Guid.NewGuid():N}",
            Subject = "user",
            Predicate = "lives in",
            Object = @object,
            Confidence = 0.9,
            OwnerId = "alice",
            Embedding = Embedding,
            ValidFrom = validFrom,
            ValidUntil = validUntil,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });

    private Task<IReadOnlyList<(Fact Fact, double Score)>> SearchAsync(ValidTimeMode mode) =>
        _facts.SearchByVectorAsync(
            Embedding, limit: 20, minScore: 0.0,
            scope: MemoryScope.For("alice", includeShared: false), validTime: mode);

    private static string[] Objects(IReadOnlyList<(Fact Fact, double Score)> results) =>
        [.. results.Select(r => r.Fact.Object).OrderBy(o => o, StringComparer.Ordinal)];

    [Fact]
    public async Task WithNoValidityBoundsAnywhereBothModesReturnTheSameSet()
    {
        // THE falsifier. If these differ, something wrote a bound nobody audited.
        await StoreAsync("Zurich");
        await StoreAsync("Basel");
        await StoreAsync("Bern");

        Objects(await SearchAsync(ValidTimeMode.Ignore))
            .Should().Equal(Objects(await SearchAsync(ValidTimeMode.Current)));
    }

    [Fact]
    public async Task AnExpiredFactIsExcludedOnlyWhenTheGateIsOn()
    {
        // The gate doing its job, and the proof that the test above is not passing vacuously: if the
        // gate never filtered anything, both assertions would hold for the wrong reason.
        await StoreAsync("Zurich");
        await StoreAsync("Basel", validUntil: DateTimeOffset.UtcNow.AddDays(-1));

        Objects(await SearchAsync(ValidTimeMode.Ignore)).Should().Equal("Basel", "Zurich");
        Objects(await SearchAsync(ValidTimeMode.Current)).Should().Equal("Zurich");
    }

    [Fact]
    public async Task AFactNotYetValidIsExcludedOnlyWhenTheGateIsOn()
    {
        // Both bounds, not just the upper one: a valid_from in the future is the case an
        // implementation that only checked valid_until would return as current.
        await StoreAsync("Zurich");
        await StoreAsync("Geneva", validFrom: DateTimeOffset.UtcNow.AddDays(1));

        Objects(await SearchAsync(ValidTimeMode.Ignore)).Should().Equal("Geneva", "Zurich");
        Objects(await SearchAsync(ValidTimeMode.Current)).Should().Equal("Zurich");
    }

    [Fact]
    public async Task AnOpenEndedFactIsCurrentUnderBothModes()
    {
        // Null must mean "unbounded", never "not yet valid". An implementation comparing nulls with
        // ordinary operators would drop every fact that never stated a window -- which is nearly all
        // of them.
        await StoreAsync("Zurich", validFrom: DateTimeOffset.UtcNow.AddYears(-1), validUntil: null);

        Objects(await SearchAsync(ValidTimeMode.Current)).Should().Equal("Zurich");
    }

    // ── the second writer: supersession ───────────────────────────────────

    [Fact]
    public async Task ASupersededFactIsAlreadyGoneBeforeTheGateIsConsulted()
    {
        // Supersession stamps valid_until AND invalidated_at, and the transaction-clock filter runs
        // regardless of this option. So the gate is redundant for superseded facts and load-bearing
        // only for stated real-world windows. Anyone reaching for valid-time to hide superseded data
        // is solving a problem that is already solved.
        var loser = await StoreAsync("Basel");
        var winner = await StoreAsync("Zurich");
        await _facts.SupersedeAsync(
            loser.FactId, winner.FactId, MemoryScope.For("alice", includeShared: false));

        Objects(await SearchAsync(ValidTimeMode.Ignore)).Should().Equal("Zurich");
        Objects(await SearchAsync(ValidTimeMode.Current)).Should().Equal("Zurich");
    }

    [Fact]
    public async Task SupersessionIsTheOnlyWriterThatFiresWithoutTemporalExtraction()
    {
        // The inventory, asserted. Ordinary ingestion writes null bounds; only an explicitly supplied
        // window (temporal extraction) or a supersession sets one. If a third writer appears, the
        // first test in this class starts failing and this one says where to look.
        var untouched = await StoreAsync("Zurich");

        var stored = await _facts.GetByIdAsync(untouched.FactId);

        stored.Should().NotBeNull();
        stored!.ValidFrom.Should().BeNull("ordinary ingestion states no window");
        stored.ValidUntil.Should().BeNull();
    }
}
