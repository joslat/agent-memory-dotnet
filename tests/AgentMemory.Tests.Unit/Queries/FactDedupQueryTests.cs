using AgentMemory.Neo4j.Queries;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Queries;

public sealed class FactDedupQueryTests
{
    [Fact]
    public void FindDuplicate_ScopesCandidatesBeforeExactCosineRanking()
    {
        var cypher = FactQueries.FindDuplicate();

        cypher.Should().Contain("MATCH (node:Fact)");
        cypher.Should().Contain("node.owner_key = $ownerKey");
        cypher.Should().Contain("toLower(node.subject) = toLower($subject)");
        cypher.Should().Contain("toLower(node.predicate) = toLower($predicate)");
        cypher.Should().Contain("vector.similarity.cosine(node.embedding, $embedding)");
        cypher.Should().NotContain("db.index.vector.queryNodes");

        var match = cypher.IndexOf("MATCH (node:Fact)", StringComparison.Ordinal);
        var cosine = cypher.IndexOf("vector.similarity.cosine", StringComparison.Ordinal);
        match.Should().BeLessThan(cosine,
            "same-owner subject/predicate scoping must precede similarity ranking");
    }
}
