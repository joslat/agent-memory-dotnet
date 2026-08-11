using AgentMemory.Abstractions.Domain;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// L2. Did the context receive EVERY fact under the relation the question names?
/// </summary>
/// <remarks>
/// The metric Phase L exists to build. Message coverage could not answer this — it saturated at 6/7
/// on 30 of 50 questions — and an accuracy score cannot either, because at sd 9.3 cold-build it
/// cannot see a moderate extraction regression at all.
/// <para>
/// The two numbers do different jobs and must never be collapsed into the ratio alone.
/// <b>D</b> counts the graph and is a deterministic extraction-quality signal: if a change stops
/// learning a needed relation, D drops with no judge involved. <b>N</b> counts the context and is a
/// retrieval signal. So <c>D = 0</c> with a non-empty key set means "the relation was never
/// extracted", while <c>N &lt; D</c> means "it was extracted and retrieval left some behind" — an
/// extraction bug and a retrieval bug that a single ratio would render identical.
/// </para>
/// </remarks>
public sealed class RelationCompletenessTests
{
    [Fact]
    public void EverythingRetrievedIsComplete()
    {
        var r = Compute(["serviced"], graph: new() { ["serviced"] = 3 }, retrieved: 3);

        r.Denominator.Should().Be(3);
        r.Numerator.Should().Be(3);
        r.Ratio.Should().Be(1d);
        r.Complete.Should().BeTrue();
    }

    [Fact]
    public void RetrievalLeavingSomeBehindIsIncomplete()
    {
        // The retrieval defect: the relation exists in the graph, the context has only part of it.
        var r = Compute(["serviced"], graph: new() { ["serviced"] = 10 }, retrieved: 4);

        r.Ratio.Should().Be(0.4);
        r.Complete.Should().BeFalse();
    }

    [Fact]
    public void ARelationAbsentFromTheGraphIsNullNotZero()
    {
        // The load-bearing distinction. D = 0 means the relation was never extracted -- an
        // extraction or vocabulary miss. Reporting it as 0.0 completeness would file it as a
        // retrieval failure and send the next effort to entirely the wrong place.
        var r = Compute(["serviced"], graph: new(), retrieved: 0);

        r.Denominator.Should().Be(0);
        r.Ratio.Should().BeNull();
        r.Complete.Should().BeNull();
        r.RelationAbsentFromGraph.Should().BeTrue();
    }

    [Fact]
    public void NoResolvedRelationsIsNullThroughout()
    {
        // Expansion had nothing to expand; there is no completeness question to answer.
        var r = Compute([], graph: new() { ["serviced"] = 5 }, retrieved: 0);

        r.Ratio.Should().BeNull();
        r.RelationAbsentFromGraph.Should().BeFalse("there was no relation to be absent");
    }

    [Fact]
    public void AnUnmeasuredProbeIsNullRatherThanComplete()
    {
        // A probe that could not answer must not report completeness it never checked.
        var r = AgentMemoryLongMemEvalAdapter.ComputeRelationCompleteness(
            ["serviced"], graphCounts: null, retrievedFacts: []);

        r.Ratio.Should().BeNull();
        r.Denominator.Should().BeNull();
    }

    [Fact]
    public void TheBudgetIsBindingWhenTHE_UNION_ExceedsIt_NotJustThisRelation()
    {
        // L13a. The budget is a SINGLE shared LIMIT over the whole predicate union - the question's
        // relations PLUS the canonical predicate of every top-K vector hit - ordered globally by
        // confidence. So it binds when the union exceeds it, which is the common case, not when this
        // one relation does.
        //
        // The original definition was `D > ExpansionLimit`, i.e. "does this relation alone exceed the
        // budget". It reported false on every real question while the budget was in fact exhausted on
        // all of them: a9f6b44c had D=49 against a limit of 60 and received 22, because 38 slots went
        // to unrelated higher-confidence predicates. Testing the wrong quantity made a live defect
        // look like a clean run.
        var r = AgentMemoryLongMemEvalAdapter.ComputeRelationCompleteness(
            ["serviced"],
            new Dictionary<string, int> { ["serviced"] = 40 },
            [Fact("serviced")],
            expansionLimit: 60,
            unionGraphTotal: 140);

        r.LimitBinding.Should().BeTrue("the union of 140 exceeds the 60-row budget");
        r.Complete.Should().BeFalse();
    }

    [Fact]
    public void TheBudgetIsNotBindingWhenTheWholeUnionFits()
    {
        // The control: an incomplete relation with room to spare is a genuine retrieval defect, and
        // must not be excused as a budget limitation.
        var r = AgentMemoryLongMemEvalAdapter.ComputeRelationCompleteness(
            ["serviced"],
            new Dictionary<string, int> { ["serviced"] = 40 },
            [Fact("serviced")],
            expansionLimit: 60,
            unionGraphTotal: 45);

        r.LimitBinding.Should().BeFalse();
    }

    [Fact]
    public void AnUnknownUnionTotalDoesNotClaimTheBudgetWasFine()
    {
        // Not measured must not read as "not binding" - that is the failure mode being fixed.
        var r = AgentMemoryLongMemEvalAdapter.ComputeRelationCompleteness(
            ["serviced"],
            new Dictionary<string, int> { ["serviced"] = 40 },
            [Fact("serviced")],
            expansionLimit: 60);

        r.LimitBinding.Should().BeNull();
    }

    [Fact]
    public void FactsUnderOtherRelationsDoNotCountTowardsTheNumerator()
    {
        // Retrieving plenty of facts is not the same as retrieving the right ones.
        var facts = new[] { Fact("serviced"), Fact("bought"), Fact("bought"), Fact("bought") };
        var r = AgentMemoryLongMemEvalAdapter.ComputeRelationCompleteness(
            ["serviced"], new Dictionary<string, int> { ["serviced"] = 2 }, facts);

        r.Numerator.Should().Be(1);
        r.Complete.Should().BeFalse();
    }

    private static LongMemEvalRelationCompleteness Compute(
        string[] keys, Dictionary<string, int> graph, int retrieved, int expansionLimit = 100)
    {
        var facts = Enumerable.Range(0, retrieved)
            .Select(i => Fact(keys.Length > 0 ? keys[0] : "other", i))
            .ToArray();
        return AgentMemoryLongMemEvalAdapter.ComputeRelationCompleteness(
            keys, graph, facts, expansionLimit);
    }

    private static Fact Fact(string predicate, int i = 0) => new()
    {
        FactId = $"{predicate}-{i}",
        Subject = "Alice",
        Predicate = predicate,
        Object = "bike",
        Confidence = 1,
        CreatedAtUtc = DateTimeOffset.UnixEpoch,
    };
}
