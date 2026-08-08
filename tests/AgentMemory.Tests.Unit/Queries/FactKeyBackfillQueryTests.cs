using AgentMemory.Neo4j.Queries;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Queries;

/// <summary>
/// Phase 1.1. Fact identity moved to canonical keys, so facts written by an earlier version carry no
/// <c>*_key</c> properties: a re-extracted triple duplicates instead of merging, and predicate
/// expansion cannot see them at all. The backfill is C#-driven rather than a Cypher migration because
/// Cypher's <c>toLower()</c> and .NET's <c>ToLowerInvariant()</c> disagree on U+0130, so a Cypher
/// backfill would write keys the write path never matches.
/// </summary>
public sealed class FactKeyBackfillQueryTests
{
    [Fact]
    public void TheSelectorFindsOnlyFactsMissingCanonicalKeys()
    {
        // Idempotence depends on this: a re-run must select nothing once every fact is keyed.
        FactQueries.SelectFactsMissingCanonicalKeys.Should().Contain("predicate_key IS NULL");
    }

    [Fact]
    public void TheSelectorIsBoundedSoALargeStoreCanBeMigratedInBatches()
    {
        FactQueries.SelectFactsMissingCanonicalKeys.Should().Contain("LIMIT $limit");
    }

    [Fact]
    public void TheSelectorReturnsTheRawTripleTheKeysAreComputedFrom()
    {
        var cypher = FactQueries.SelectFactsMissingCanonicalKeys;

        cypher.Should().Contain("f.subject");
        cypher.Should().Contain("f.predicate");
        cypher.Should().Contain("f.object");
    }

    [Fact]
    public void TheBackfillWritesAllThreeKeys()
    {
        var cypher = FactQueries.ApplyCanonicalKeys;

        cypher.Should().Contain("f.subject_key");
        cypher.Should().Contain("f.predicate_key");
        cypher.Should().Contain("f.object_key");
    }

    [Fact]
    public void TheBackfillNeverComputesCanonicalFormsInCypher()
    {
        // The whole reason this is not a .cypher migration: toLower() diverges from
        // ToLowerInvariant() on U+0130, so keys computed here would not match the write path.
        var cypher = FactQueries.ApplyCanonicalKeys;

        cypher.Should().NotContain("toLower");
        cypher.Should().NotContain("replace(");
    }

    [Fact]
    public void TheBackfillTargetsFactsByIdSoItCannotTouchAnythingElse()
    {
        FactQueries.ApplyCanonicalKeys.Should().Contain("item.id");
    }
}
