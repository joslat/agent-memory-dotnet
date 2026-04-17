using System.Reflection;
using FluentAssertions;
using Neo4j.AgentMemory.Neo4j.Queries;

namespace Neo4j.AgentMemory.Tests.Unit.Queries;

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
    [InlineData("EntityQueries.FindSimilarByEmbedding")]
    [InlineData("FactQueries.GetById")]
    [InlineData("FactQueries.Upsert")]
    [InlineData("MessageQueries.Add")]
    [InlineData("MessageQueries.DeleteCascade")]
    [InlineData("ConversationQueries.ListSessions")]
    [InlineData("ExtractorQueries.GetEntityProvenance")]
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
            .Where(t => t.IsPublic && t.IsAbstract && t.IsSealed
                        && t.Name.EndsWith("Queries")
                        && t.Namespace == "Neo4j.AgentMemory.Neo4j.Queries")
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
