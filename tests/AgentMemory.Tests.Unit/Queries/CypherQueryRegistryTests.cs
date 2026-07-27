using System.Reflection;
using FluentAssertions;
using AgentMemory.Neo4j.Queries;

namespace AgentMemory.Tests.Unit.Queries;

/// <summary>
/// Unit tests for <see cref="CypherQueryRegistry"/> — the reflection-based
/// registry that discovers all Cypher query constants from *Queries classes.
/// </summary>
public sealed class CypherQueryRegistryTests
{
    private static readonly IReadOnlyList<(string Name, string Cypher)> AllQueries =
        CypherQueryRegistry.GetAll();

    // ── Basic invariants ──

    [Fact]
    public void GetAll_ReturnsNonEmptyCollection()
    {
        AllQueries.Should().NotBeEmpty();
    }

    [Fact]
    public void GetAll_AllCypherStringsAreNonNullAndNonWhitespace()
    {
        foreach (var (name, cypher) in AllQueries)
        {
            cypher.Should().NotBeNullOrWhiteSpace(
                because: $"query '{name}' must contain valid Cypher text");
        }
    }

    [Fact]
    public void GetAll_AllNamesAreNonNullAndNonWhitespace()
    {
        foreach (var (name, _) in AllQueries)
        {
            name.Should().NotBeNullOrWhiteSpace();
        }
    }

    // ── Known constants are present ──

    [Theory]
    [InlineData("EntityQueries.Upsert")]
    [InlineData("EntityQueries.GetById")]
    [InlineData("EntityQueries.GetEntitiesFromMessage")]
    [InlineData("FactQueries.GetById")]
    [InlineData("FactQueries.Upsert")]
    [InlineData("MessageQueries.Add")]
    [InlineData("MessageQueries.DeleteCascade")]
    [InlineData("ConversationQueries.ListSessions")]
    [InlineData("ExtractorQueries.GetExtractionStats")]
    [InlineData("SchemaQueries.ConversationIdConstraint")]
    [InlineData("RelationshipQueries.Upsert")]
    [InlineData("PreferenceQueries.Upsert")]
    [InlineData("ReasoningQueries.AddTrace")]
    [InlineData("ToolCallQueries.Add")]
    public void GetAll_ContainsKnownQueryConstant(string expectedName)
    {
        AllQueries.Select(q => q.Name).Should().Contain(expectedName);
    }

    // ── Exact count matches reflection-based expected total ──

    [Fact]
    public void GetAll_CountMatchesAllConstStringFieldsAcrossQueriesClasses()
    {
        // Compute the expected count the same way the registry does,
        // but independently to catch drift.
        var expectedCount = typeof(CypherQueryRegistry).Assembly
            .GetTypes()
            .Where(t => !t.IsNested && t.IsAbstract && t.IsSealed // top-level static classes (public or internal)
                        && t.Name.EndsWith("Queries")
                        && t.Namespace == "AgentMemory.Neo4j.Queries")
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static))
            .Count(f => f.IsLiteral && f.FieldType == typeof(string)
                        && !string.IsNullOrWhiteSpace((string?)f.GetValue(null)));

        AllQueries.Should().HaveCount(expectedCount,
            because: "the registry should discover every const string in *Queries classes");
    }

    // ── SharedFragments exclusion ──

    [Fact]
    public void GetAll_ExcludesSharedFragments()
    {
        AllQueries.Select(q => q.Name)
            .Should().NotContain(n => n.StartsWith("SharedFragments."),
                because: "SharedFragments is not a *Queries class");
    }

    [Theory]
    [InlineData("SharedFragments.SetEntityEmbedding")]
    [InlineData("SharedFragments.SetFactEmbedding")]
    [InlineData("SharedFragments.LinkEntityExtractedFrom")]
    public void GetAll_DoesNotContainSpecificSharedFragment(string fragmentName)
    {
        AllQueries.Select(q => q.Name).Should().NotContain(fragmentName);
    }

    // ── No duplicate names ──

    [Fact]
    public void GetAll_NoDuplicateNames()
    {
        var names = AllQueries.Select(q => q.Name).ToList();
        names.Should().OnlyHaveUniqueItems(
            because: "each query constant should appear exactly once in the registry");
    }

    // ── Naming format validation ──

    [Fact]
    public void GetAll_AllNamesFollowClassDotFieldFormat()
    {
        foreach (var (name, _) in AllQueries)
        {
            name.Should().Contain(".",
                because: $"registry name '{name}' should be 'ClassName.FieldName'");
            name.Split('.').Should().HaveCount(2);
        }
    }

    [Fact]
    public void FingerprintFor_KnownConstant_ReturnsStableSourceName()
    {
        CypherQueryRegistry.FingerprintFor(EntityQueries.GetById)
            .Should().Be("EntityQueries.GetById");
    }

    [Fact]
    public void FingerprintFor_CentralizedMethodBuiltQueries_ReturnsStableSourceNames()
    {
        var cases = new (string Cypher, string Fingerprint)[]
        {
            (MessageQueries.SearchByVector(true, topK: 10),
                "MessageQueries.SearchByVector"),
            (EntityQueries.SearchByVector(true, true, 50),
                "EntityQueries.SearchByVector"),
            (FactQueries.SearchByVector(true, true, 50),
                "FactQueries.SearchByVector"),
            (PreferenceQueries.SearchByVector(true, true, 50),
                "PreferenceQueries.SearchByVector"),
            (ReasoningQueries.SearchByTaskVector(false, true, true, 50),
                "ReasoningQueries.SearchByTaskVector"),
            (DecayQueries.UpdateAccessTimestampBatch("Entity"),
                "DecayQueries.UpdateAccessTimestampBatch"),
            (FactQueries.FindDuplicate(10),
                "FactQueries.FindDuplicate"),
            (PreferenceQueries.FindDuplicate(10, ownerIsShared: false),
                "PreferenceQueries.FindDuplicate"),
            (TemporalQueries.SearchEntitiesAsOf(true, true, 50),
                "TemporalQueries.SearchEntitiesAsOf"),
        };

        foreach (var (cypher, fingerprint) in cases)
            CypherQueryRegistry.FingerprintFor(cypher).Should().Be(fingerprint);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("RETURN 'consumer supplied'")]
    public void FingerprintFor_UnregisteredText_ReturnsUnknown(string? cypher)
    {
        CypherQueryRegistry.FingerprintFor(cypher)
            .Should().Be(CypherQueryRegistry.UnknownFingerprint);
    }

    // ── All queries contain actual Cypher ──

    [Fact]
    public void GetAll_AllQueriesContainCypherKeywords()
    {
        // Every real Cypher query should contain at least one known keyword
        var cypherKeywords = new[]
        {
            "MATCH", "MERGE", "CREATE", "SET", "RETURN", "DELETE",
            "WITH", "CALL", "UNWIND", "FOR", "DROP", "INDEX",
            "CONSTRAINT", "COUNT", "ORDER", "WHERE", "OPTIONAL", "DETACH"
        };

        foreach (var (name, cypher) in AllQueries)
        {
            var upperCypher = cypher.ToUpperInvariant();
            cypherKeywords.Should().Contain(
                kw => upperCypher.Contains(kw),
                because: $"query '{name}' should contain at least one Cypher keyword");
        }
    }
}
