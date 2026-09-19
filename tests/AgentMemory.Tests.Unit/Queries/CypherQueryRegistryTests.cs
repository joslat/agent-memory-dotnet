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

    // ── Prospective firing: method-built, and previously unattributed ──

    /// <summary>
    /// Every firing shape is attributed, in every owner-scoping variant.
    /// </summary>
    /// <remarks>
    /// These four are built by method rather than declared as constants, so the reflection map never
    /// sees them and they fell through to <c>unknown</c>. An unattributed query is invisible to the
    /// per-scenario query counts the hermetic perf gate compares against baseline — the firing path
    /// could double its query load and the table would show nothing moved.
    /// </remarks>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FingerprintFor_AttributesEveryProspectiveFiringShape(bool hasOwner, bool includeShared)
    {
        CypherQueryRegistry.FingerprintFor(FactQueries.GetDueFacts(hasOwner, includeShared))
            .Should().Be("FactQueries.GetDueFacts");
        CypherQueryRegistry.FingerprintFor(FactQueries.GetExpiringFacts(hasOwner, includeShared))
            .Should().Be("FactQueries.GetExpiringFacts");
        CypherQueryRegistry.FingerprintFor(TemporalQueries.GetDueFactsAsOf(hasOwner, includeShared))
            .Should().Be("TemporalQueries.GetDueFactsAsOf");
        CypherQueryRegistry.FingerprintFor(TemporalQueries.GetExpiringFactsAsOf(hasOwner, includeShared))
            .Should().Be("TemporalQueries.GetExpiringFactsAsOf");
    }

    /// <summary>
    /// The live and as-of twins are told apart, and neither is confused with delta recall.
    /// </summary>
    /// <remarks>
    /// Delta recall shares both of the obvious markers — <c>DeltaExpiredValidity</c> orders by the
    /// same key and <c>DeltaNewValidity</c> bounds on the same <c>$since</c> — so a fingerprint
    /// keyed on either alone would collapse three different queries into one name and report
    /// confident nonsense instead of an honest <c>unknown</c>.
    /// </remarks>
    [Fact]
    public void FingerprintFor_DoesNotConfuseFiringWithDeltaRecall()
    {
        var firing = new[]
        {
            CypherQueryRegistry.FingerprintFor(FactQueries.GetDueFacts(true, false)),
            CypherQueryRegistry.FingerprintFor(FactQueries.GetExpiringFacts(true, false)),
            CypherQueryRegistry.FingerprintFor(TemporalQueries.GetDueFactsAsOf(true, false)),
            CypherQueryRegistry.FingerprintFor(TemporalQueries.GetExpiringFactsAsOf(true, false)),
        };

        firing.Should().OnlyHaveUniqueItems("the four shapes are four different queries");
        firing.Should().NotContain(CypherQueryRegistry.UnknownFingerprint);

        CypherQueryRegistry.FingerprintFor(FactQueries.DeltaExpiredValidity(true, false))
            .Should().NotBe("FactQueries.GetExpiringFacts",
                "delta recall orders by the same key but is a different query");
        CypherQueryRegistry.FingerprintFor(FactQueries.DeltaNewlyDueProspective(true, false))
            .Should().NotBe("FactQueries.GetDueFacts",
                "delta recall bounds on the same $since but is a different query");
    }

    // ── E-1 identity expansion: method-built, therefore unattributed unless registered ──

    /// <summary>
    /// The identity hop is attributed, in every owner-scoping variant.
    /// </summary>
    /// <remarks>
    /// Added because the firing family two commits earlier had exactly this hole, and a new
    /// method-built query inherits it by default. The hop runs once per recall when the feature is
    /// on, so an unattributed one would leave the hermetic gate's per-scenario query counts unable to
    /// see a whole extra query.
    /// </remarks>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FingerprintFor_AttributesTheIdentityExpansionHop(bool hasOwner, bool includeShared) =>
        CypherQueryRegistry
            .FingerprintFor(FactQueries.GetFactsSharingAliasedEntities(hasOwner, includeShared))
            .Should().Be("FactQueries.GetFactsSharingAliasedEntities");

    /// <summary>
    /// The live hop and its bitemporal twin are told apart.
    /// </summary>
    /// <remarks>
    /// They share the traversal and differ only in the clocks, so one fingerprint covering both would
    /// report a point-in-time read and a live one as the same query — erasing the distinction the
    /// twin exists to make, in the telemetry that would be used to check it.
    /// </remarks>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void FingerprintFor_TellsTheLiveAliasHopFromItsAsOfTwin(bool hasOwner, bool includeShared)
    {
        CypherQueryRegistry
            .FingerprintFor(FactQueries.GetFactsSharingAliasedEntitiesAsOf(hasOwner, includeShared))
            .Should().Be("FactQueries.GetFactsSharingAliasedEntitiesAsOf");

        CypherQueryRegistry
            .FingerprintFor(FactQueries.GetFactsSharingAliasedEntities(hasOwner, includeShared))
            .Should().NotBe("FactQueries.GetFactsSharingAliasedEntitiesAsOf");
    }

    /// <summary>
    /// It is not confused with the only other <c>:ABOUT</c> consumer.
    /// </summary>
    /// <remarks>
    /// <c>NodeDistanceQueries</c> walks the same edge type, and is the traversal this feature
    /// deliberately is NOT — collapsing the two into one fingerprint would report the harmful
    /// adjacency hop and the identity hop as the same query.
    /// </remarks>
    [Fact]
    public void FingerprintFor_DoesNotConfuseTheIdentityHopWithNodeDistance() =>
        CypherQueryRegistry.FingerprintFor(NodeDistanceQueries.DistanceToCandidates)
            .Should().NotBe("FactQueries.GetFactsSharingAliasedEntities");

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
            (FactQueries.FindDuplicate(),
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
