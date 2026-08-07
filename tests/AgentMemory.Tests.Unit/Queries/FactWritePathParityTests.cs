using AgentMemory.Neo4j.Queries;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Queries;

/// <summary>
/// Facts have two write paths: the single/batch <see cref="FactQueries"/> upserts and the fused
/// batch writer. Extraction uses the <b>fused</b> one, so canonical identity applied to only the
/// first shipped a change that never reached a real cold build — the live graph came back with
/// <c>predicate_key</c> null on all 650 facts while every unit test passed.
/// </summary>
public sealed class FactWritePathParityTests
{
    public static TheoryData<string, string> FactMergeQueries() => new()
    {
        { nameof(FactQueries.Upsert), FactQueries.Upsert },
        { nameof(FactQueries.UpsertBatch), FactQueries.UpsertBatch },
        { "FusedPersistence", FusedPersistenceQueries.FactUpsertBatch }
    };

    [Theory]
    [MemberData(nameof(FactMergeQueries))]
    public void EveryFactWritePathMergesOnCanonicalIdentity(string name, string cypher)
    {
        _ = name;
        cypher.Should().Contain("MERGE (f:Fact {subject_key:");
        cypher.Should().Contain("predicate_key:");
        cypher.Should().Contain("object_key:");
    }

    [Theory]
    [MemberData(nameof(FactMergeQueries))]
    public void EveryFactWritePathStillPersistsTheRawTriple(string name, string cypher)
    {
        // Canonical keys are for identity only; the original text must survive for display and audit.
        _ = name;
        cypher.Should().MatchRegex(@"f\.subject\s+=");
        cypher.Should().MatchRegex(@"f\.predicate\s+=");
        cypher.Should().MatchRegex(@"f\.object\s+=");
    }

    [Theory]
    [MemberData(nameof(FactMergeQueries))]
    public void NoFactWritePathMergesOnRawText(string name, string cypher)
    {
        // The regression this pins: a MERGE keyed on raw strings silently reintroduces one node per
        // spelling variant.
        _ = name;
        cypher.Should().NotContain("MERGE (f:Fact {subject:");
    }
}
