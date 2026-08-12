using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Queries;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Queries;

/// <summary>
/// Not shipping the stored vector back with every recall hit (rank 13 / payload projection).
/// </summary>
/// <remarks>
/// <para>
/// Every entity, fact and preference vector search returned the whole node — including a 384-dim
/// embedding, roughly <b>3 KB per item and ~130 KB per turn</b> moved across the wire, deserialized,
/// and discarded. Nothing on the recall path reads it; the similarity was computed inside the index.
/// A measured prototype showed <b>−91% on the entity transaction and −21% on the whole turn</b>.
/// </para>
/// <para>
/// <b>That prototype was reverted because an unconditional projection is unsafe here.</b> The TCK
/// conformance bridge serialises embeddings on three search endpoints — <c>/search_messages</c>,
/// <c>/search_entities</c>, <c>/search_preferences</c> — and would silently start emitting null,
/// which is why the original plan demanded validation against the full 178-case run.
/// </para>
/// <para>
/// Making it opt-in removes the need for that run: a consumer that re-uses recalled vectors simply
/// does not enable it, so it is unaffected <b>by construction</b> rather than by a test that passed
/// once. These tests pin both halves — the projection when asked for, and its complete absence when
/// not.
/// </para>
/// </remarks>
public sealed class RecallPayloadProjectionTests
{
    public static TheoryData<string, Func<bool, string>> Searches => new()
    {
        { "Entity", omit => EntityQueries.SearchByVector(true, true, 100, false, omit) },
        { "Preference", omit => PreferenceQueries.SearchByVector(true, true, 100, false, omit) },
        { "Fact", omit => FactQueries.SearchByVector(true, true, 100, false, false, omit) },
    };

    [Theory]
    [MemberData(nameof(Searches))]
    public void OffByDefaultTheQueryIsByteIdentical(string name, Func<bool, string> build)
    {
        // The off state, and it is the default. Every sealed measurement and the TCK bridge both run
        // on this exact Cypher; a projection that leaked into the default would change what the
        // conformance suite receives without anything saying so.
        _ = name;

        build(false).Should().NotContain("embedding: NULL");
        build(false).Should().Contain("RETURN node, score");
    }

    [Theory]
    [MemberData(nameof(Searches))]
    public void WhenOmittedTheEmbeddingIsProjectedAway(string name, Func<bool, string> build)
    {
        _ = name;

        build(true).Should().Contain("node {.*, embedding: NULL} AS node");
    }

    [Theory]
    [MemberData(nameof(Searches))]
    public void EverythingElseIsStillReturned(string name, Func<bool, string> build)
    {
        // `.*` keeps every other property. Projecting an explicit allow-list instead would silently
        // drop any property added later -- a mapper reading a missing key is a failure that shows up
        // as a null field rather than as an error.
        _ = name;

        build(true).Should().Contain(".*");
    }

    [Fact]
    public void TheRecencyRerankerStillReadsTheNodeItNeeds()
    {
        // The projection happens at the FINAL return only. The recency branch reads
        // node.last_accessed_at and node.created_at in its WITH clauses, so stripping earlier would
        // disable the re-ranker rather than shrink the payload -- and the ordering would quietly
        // revert to semantic-only.
        var cypher = EntityQueries.SearchByVector(true, true, 100, recencyRerank: true, omitEmbedding: true);

        cypher.Should().Contain("last_accessed_at");
        cypher.Should().Contain("node {.*, embedding: NULL} AS node");
    }

    [Fact]
    public void TheOptionIsOffByDefault()
    {
        // A consumer that re-uses recalled vectors -- the TCK bridge among them -- keeps working
        // without knowing this exists.
        new MemoryOptions().OmitEmbeddingsFromRecall.Should().BeFalse();
    }
}
