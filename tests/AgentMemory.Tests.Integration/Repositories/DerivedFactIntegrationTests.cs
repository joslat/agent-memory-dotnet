using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// 30.6 steps 5 and 6, against a live graph: recompute-in-place identity, and the staleness cascade.
/// </summary>
/// <remarks>
/// <para>
/// <b>The cascade is the safety property of the whole feature.</b> A derived <c>750</c> whose input
/// <c>800</c> was superseded is a manufactured confident-wrong answer — stored, embedded, recallable,
/// and carrying inline provenance that makes it look verified. It is worse than having no aggregate at
/// all, which is why the design makes invalidation-with-inputs a same-statement cascade rather than an
/// eventually-consistent sweep.
/// </para>
/// <para>
/// The other half is identity. An aggregate's value changes on every recompute, so if the node were
/// keyed on its triple the graph would accumulate one dead aggregate per observation — invisible in any
/// unit test, and only visible here.
/// </para>
/// </remarks>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class DerivedFactIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jFactRepository _facts;

    private static readonly MemoryScope Alice = MemoryScope.For("alice", includeShared: false);

    // The CANONICAL keys, not the raw strings. MemoryTripleCanonicalizer.Canonical treats '_' as a word
    // separator, so the predicate "savings_balance" is stored under the key "savings balance" -- a test
    // that passed the raw spelling would query a group the graph does not have and see an empty result
    // it could easily read as correct isolation. This is exactly why the accountant canonicalizes before
    // asking, and why it uses the same canonicalizer the write path uses rather than its own.
    private const string SubjectKey = "user";
    private const string PredicateKey = "savings balance";

    public DerivedFactIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _facts = new Neo4jFactRepository(
            fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static Fact Input(string @object, string owner = "alice", int order = 0) => new()
    {
        FactId = Guid.NewGuid().ToString("N"),
        Subject = "user",
        Predicate = "savings_balance",
        Object = @object,
        Confidence = 0.9,
        CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(order),
        OwnerId = owner,
    };

    private static Fact Derived(
        string @object, IReadOnlyList<string> inputs, string owner = "alice",
        DerivationOperators op = DerivationOperators.Delta) => new()
    {
        FactId = Guid.NewGuid().ToString("N"),
        Subject = "user",
        Predicate = "delta_of:savings_balance",
        Object = @object,
        Confidence = 0.9,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        OwnerId = owner,
        Metadata = MemoryDerivationMetadataExtensions
            .CreateWithDerivation(op, $"computed from {inputs.Count} facts", inputs),
    };

    // ── identity: recompute updates in place ──────────────────────────

    [Fact]
    public async Task RecomputingWithAChangedValueUpdatesTheSameNode()
    {
        // If identity included the object, the graph would accumulate one dead aggregate per
        // observation. This is the test that would catch that, and only a live graph can.
        var a = await _facts.UpsertAsync(Input("800", order: 0));
        var b = await _facts.UpsertAsync(Input("50", order: 1));

        var first = await _facts.UpsertDerivedAsync(Derived("-750", [a.FactId, b.FactId]), [a.FactId, b.FactId]);
        var second = await _facts.UpsertDerivedAsync(Derived("-700", [a.FactId, b.FactId]), [a.FactId, b.FactId]);

        second.FactId.Should().Be(first.FactId, "the derivation key is the identity, not the value");
        second.Object.Should().Be("-700");
    }

    [Fact]
    public async Task TwoOperatorsOverOneGroupAreTwoDifferentNodes()
    {
        var a = await _facts.UpsertAsync(Input("800", order: 0));
        var b = await _facts.UpsertAsync(Input("50", order: 1));

        var delta = await _facts.UpsertDerivedAsync(
            Derived("-750", [a.FactId, b.FactId], op: DerivationOperators.Delta), [a.FactId, b.FactId]);
        var count = await _facts.UpsertDerivedAsync(
            Derived("2", [a.FactId, b.FactId], op: DerivationOperators.Count) with
            {
                Predicate = "count_of:savings_balance",
            },
            [a.FactId, b.FactId]);

        count.FactId.Should().NotBe(delta.FactId);
    }

    [Fact]
    public async Task TwoOwnersAggregatesNeverCollide()
    {
        var alice = await _facts.UpsertAsync(Input("800", owner: "alice"));
        var bob = await _facts.UpsertAsync(Input("800", owner: "bob"));

        var aliceDerived = await _facts.UpsertDerivedAsync(
            Derived("1", [alice.FactId], owner: "alice"), [alice.FactId]);
        var bobDerived = await _facts.UpsertDerivedAsync(
            Derived("1", [bob.FactId], owner: "bob"), [bob.FactId]);

        bobDerived.FactId.Should().NotBe(aliceDerived.FactId);
    }

    [Fact]
    public async Task RecomputingRepointsTheProvenanceEdges()
    {
        // A derived value whose stated inputs no longer include the fact it was computed from is worse
        // than one with no provenance at all.
        var a = await _facts.UpsertAsync(Input("800", order: 0));
        var b = await _facts.UpsertAsync(Input("50", order: 1));
        var c = await _facts.UpsertAsync(Input("25", order: 2));

        await _facts.UpsertDerivedAsync(Derived("-750", [a.FactId, b.FactId]), [a.FactId, b.FactId]);
        var derived = await _facts.UpsertDerivedAsync(
            Derived("-775", [a.FactId, c.FactId]), [a.FactId, c.FactId]);

        var edges = await CountEdgesAsync(derived.FactId);
        edges.Should().Be(2);
        (await HasEdgeAsync(derived.FactId, b.FactId)).Should().BeFalse("b left the derivation");
        (await HasEdgeAsync(derived.FactId, c.FactId)).Should().BeTrue();
    }

    // ── the cascade ───────────────────────────────────────────────────

    [Fact]
    public async Task SupersedingAnInputInvalidatesItsAggregateInTheSameCall()
    {
        // THE test. Not "eventually"; not "on the next accountant pass". In the same call.
        var a = await _facts.UpsertAsync(Input("800", order: 0));
        var b = await _facts.UpsertAsync(Input("50", order: 1));
        var derived = await _facts.UpsertDerivedAsync(
            Derived("-750", [a.FactId, b.FactId]), [a.FactId, b.FactId]);
        var replacement = await _facts.UpsertAsync(Input("900", order: 2));

        await _facts.SupersedeAsync(a.FactId, replacement.FactId, Alice);

        (await IsLiveAsync(derived.FactId)).Should().BeFalse(
            "an aggregate over a replaced input is stale the instant the replacement lands");
    }

    [Fact]
    public async Task InvalidatingAnInputCascadesIdentically()
    {
        var a = await _facts.UpsertAsync(Input("800", order: 0));
        var b = await _facts.UpsertAsync(Input("50", order: 1));
        var derived = await _facts.UpsertDerivedAsync(
            Derived("-750", [a.FactId, b.FactId]), [a.FactId, b.FactId]);

        await _facts.InvalidateAsync(b.FactId, Alice);

        (await IsLiveAsync(derived.FactId)).Should().BeFalse();
    }

    [Fact]
    public async Task SupersedingAnUnrelatedFactLeavesTheAggregateAlone()
    {
        // The cascade must be precise. A blunt one that invalidated on any write would make every
        // aggregate permanently dead in a busy graph.
        var a = await _facts.UpsertAsync(Input("800", order: 0));
        var b = await _facts.UpsertAsync(Input("50", order: 1));
        var derived = await _facts.UpsertDerivedAsync(
            Derived("-750", [a.FactId, b.FactId]), [a.FactId, b.FactId]);

        var unrelated = await _facts.UpsertAsync(Input("Acme", order: 3) with { Predicate = "employer" });
        var replacement = await _facts.UpsertAsync(Input("Initech", order: 4) with { Predicate = "employer" });
        await _facts.SupersedeAsync(unrelated.FactId, replacement.FactId, Alice);

        (await IsLiveAsync(derived.FactId)).Should().BeTrue();
    }

    [Fact]
    public async Task ARecomputeAfterACascadeReArmsTheAggregate()
    {
        // What lets the cascade afford to be blunt: it kills on any input change, and the next
        // accountant pass restores whatever still holds from the surviving inputs.
        var a = await _facts.UpsertAsync(Input("800", order: 0));
        var b = await _facts.UpsertAsync(Input("50", order: 1));
        await _facts.UpsertDerivedAsync(Derived("-750", [a.FactId, b.FactId]), [a.FactId, b.FactId]);
        var replacement = await _facts.UpsertAsync(Input("900", order: 2));
        await _facts.SupersedeAsync(a.FactId, replacement.FactId, Alice);

        var recomputed = await _facts.UpsertDerivedAsync(
            Derived("-850", [replacement.FactId, b.FactId]), [replacement.FactId, b.FactId]);

        (await IsLiveAsync(recomputed.FactId)).Should().BeTrue();
        recomputed.Object.Should().Be("-850");
    }

    [Fact]
    public async Task TheCascadeDoesNotChangeWhatSupersedeReports()
    {
        // Guard G1's observable consequence: Supersede's caller reads a row count. A fact with N
        // derived dependants must not multiply that row.
        var a = await _facts.UpsertAsync(Input("800", order: 0));
        var b = await _facts.UpsertAsync(Input("50", order: 1));
        await _facts.UpsertDerivedAsync(Derived("-750", [a.FactId, b.FactId]), [a.FactId, b.FactId]);
        await _facts.UpsertDerivedAsync(
            Derived("2", [a.FactId, b.FactId], op: DerivationOperators.Count) with
            {
                Predicate = "count_of:savings_balance",
            },
            [a.FactId, b.FactId]);
        var replacement = await _facts.UpsertAsync(Input("900", order: 2));

        var superseded = await _facts.SupersedeAsync(a.FactId, replacement.FactId, Alice);

        superseded.Should().BeTrue("two dependants must not turn one supersession into two rows");
    }

    [Fact]
    public async Task SupersedeStillWorksOnAStoreWithNoDerivedFactsAtAll()
    {
        // Guard G1's floor: the cascade is spliced into a statement every ordinary supersession runs
        // through, and OPTIONAL MATCH must bind nothing without dropping the row.
        var a = await _facts.UpsertAsync(Input("800", order: 0));
        var replacement = await _facts.UpsertAsync(Input("900", order: 1));

        var superseded = await _facts.SupersedeAsync(a.FactId, replacement.FactId, Alice);

        superseded.Should().BeTrue();
        (await IsLiveAsync(a.FactId)).Should().BeFalse();
    }

    // ── guard G2, against a live graph ────────────────────────────────

    [Fact]
    public async Task AnOrdinaryFactUpsertNeverMergesIntoADerivedNode()
    {
        // G2. A user restating a number must not land on an aggregate, overwriting its value while
        // leaving its DERIVED_FROM edges and derivation string in place.
        var a = await _facts.UpsertAsync(Input("800", order: 0));
        var b = await _facts.UpsertAsync(Input("50", order: 1));
        var derived = await _facts.UpsertDerivedAsync(
            Derived("-750", [a.FactId, b.FactId]), [a.FactId, b.FactId]);

        // The most hostile shape available: the exact same subject, predicate and object as the
        // aggregate, written through the ordinary path.
        var restated = await _facts.UpsertAsync(new Fact
        {
            FactId = Guid.NewGuid().ToString("N"),
            Subject = "user",
            Predicate = "delta_of:savings_balance",
            Object = "-750",
            Confidence = 0.9,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            OwnerId = "alice",
        });

        restated.FactId.Should().NotBe(derived.FactId,
            "a derived node carries no merge-key quadruple, so MERGE cannot reach it");
        (await CountEdgesAsync(restated.FactId)).Should().Be(0);
        (await CountEdgesAsync(derived.FactId)).Should().Be(2, "the aggregate kept its provenance");
    }

    [Fact]
    public async Task TheGroupReadNeverReturnsADerivedFact()
    {
        // Keeps the derivation DAG one level deep: aggregating aggregates would make the cascade
        // recursive.
        var a = await _facts.UpsertAsync(Input("800", order: 0));
        var b = await _facts.UpsertAsync(Input("50", order: 1));
        await _facts.UpsertDerivedAsync(Derived("-750", [a.FactId, b.FactId]), [a.FactId, b.FactId]);

        var group = await _facts.GetGroupFactsAsync(
            SubjectKey, PredicateKey, Alice, 20);

        group.Should().HaveCount(2);
        group.Should().OnlyContain(f => !f.Metadata.IsDerived());
    }

    [Fact]
    public async Task TheGroupReadOrdersOldestFirst()
    {
        var a = await _facts.UpsertAsync(Input("800", order: -10));
        var b = await _facts.UpsertAsync(Input("50", order: -5));

        var group = await _facts.GetGroupFactsAsync(SubjectKey, PredicateKey, Alice, 20);

        group.Select(f => f.FactId).Should().Equal(a.FactId, b.FactId);
    }

    [Fact]
    public async Task TheGroupReadIsOwnerIsolated()
    {
        await _facts.UpsertAsync(Input("800", owner: "alice"));
        await _facts.UpsertAsync(Input("50", owner: "alice"));
        await _facts.UpsertAsync(Input("999", owner: "bob"));

        var group = await _facts.GetGroupFactsAsync(SubjectKey, PredicateKey, Alice, 20);

        group.Should().HaveCount(2);
        group.Should().OnlyContain(f => f.OwnerId == "alice");
    }

    [Fact]
    public async Task TheGroupReadSkipsInvalidatedFacts()
    {
        var a = await _facts.UpsertAsync(Input("800", order: 0));
        await _facts.UpsertAsync(Input("50", order: 1));
        await _facts.InvalidateAsync(a.FactId, Alice);

        var group = await _facts.GetGroupFactsAsync(SubjectKey, PredicateKey, Alice, 20);

        group.Should().ContainSingle();
    }

    // ── helpers ───────────────────────────────────────────────────────

    private async Task<bool> IsLiveAsync(string factId) =>
        await _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                "MATCH (f:Fact {id: $id}) RETURN f.invalidated_at IS NULL AS live",
                new { id = factId });
            var records = await cursor.ToListAsync();
            return records.Count > 0 && records[0]["live"].As<bool>();
        }, CancellationToken.None);

    private async Task<int> CountEdgesAsync(string factId) =>
        await _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                "MATCH (f:Fact {id: $id})-[:DERIVED_FROM]->(:Fact) RETURN count(*) AS c",
                new { id = factId });
            var records = await cursor.ToListAsync();
            return records.Count == 0 ? 0 : (int)records[0]["c"].As<long>();
        }, CancellationToken.None);

    private async Task<bool> HasEdgeAsync(string derivedId, string inputId) =>
        await _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                "MATCH (f:Fact {id: $derived})-[:DERIVED_FROM]->(i:Fact {id: $input}) RETURN count(*) AS c",
                new { derived = derivedId, input = inputId });
            var records = await cursor.ToListAsync();
            return records.Count > 0 && records[0]["c"].As<long>() > 0;
        }, CancellationToken.None);
}
