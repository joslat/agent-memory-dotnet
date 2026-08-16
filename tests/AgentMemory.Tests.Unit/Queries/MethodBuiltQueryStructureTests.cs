using FluentAssertions;
using AgentMemory.Neo4j.Queries;

namespace AgentMemory.Tests.Unit.Queries;

/// <summary>
/// Structural regression net for the D5/D6/D7 query <b>builders</b> (owner-conditional methods, e.g.
/// <c>FactQueries.Supersede(bool)</c>, <c>TemporalQueries.SearchFactsAsOf(...)</c>). These are excluded
/// from <see cref="CypherQuerySnapshotTests"/> (which only reflects <c>const string</c> fields), so a
/// malformed clause in a method-built query would otherwise be caught only if a live integration test
/// happened to exercise that exact branch. This runs the same cheap invariants — balanced delimiters,
/// parameterized values, no stray string interpolation — across both the scoped and unscoped shapes.
/// </summary>
public sealed class MethodBuiltQueryStructureTests
{
    public static IEnumerable<object[]> AllMethodBuiltQueries()
    {
        foreach (var owner in new[] { false, true })
        {
            yield return new object[] { $"FactQueries.Supersede({owner})", FactQueries.Supersede(owner) };
            yield return new object[] { $"FactQueries.Invalidate({owner})", FactQueries.Invalidate(owner) };
            yield return new object[] { $"PreferenceQueries.Supersede({owner})", PreferenceQueries.Supersede(owner) };
            yield return new object[] { $"PreferenceQueries.Invalidate({owner})", PreferenceQueries.Invalidate(owner) };
            yield return new object[] { $"TemporalQueries.SearchFactsAsOf({owner})", TemporalQueries.SearchFactsAsOf(owner, includeShared: true, topK: 10) };
            yield return new object[] { $"TemporalQueries.SearchEntitiesAsOf({owner})", TemporalQueries.SearchEntitiesAsOf(owner, includeShared: true, topK: 10) };
            yield return new object[] { $"TemporalQueries.SearchPreferencesAsOf({owner})", TemporalQueries.SearchPreferencesAsOf(owner, includeShared: true, topK: 10) };
        }
    }

    [Theory]
    [MemberData(nameof(AllMethodBuiltQueries))]
    public void HasBalancedDelimiters(string name, string cypher)
    {
        Balance(cypher, '(', ')').Should().Be(0, $"{name} must have balanced parentheses");
        Balance(cypher, '[', ']').Should().Be(0, $"{name} must have balanced brackets");
        Balance(cypher, '{', '}').Should().Be(0, $"{name} must have balanced braces");
    }

    [Theory]
    [MemberData(nameof(AllMethodBuiltQueries))]
    public void IsNonEmptyAndParameterized(string name, string cypher)
    {
        cypher.Should().NotBeNullOrWhiteSpace();
        // Every one of these builders binds at least one $parameter (id/owner/clock/embedding).
        cypher.Should().Contain("$", $"{name} must use parameterized values, never inlined literals");
    }

    [Theory]
    [MemberData(nameof(AllMethodBuiltQueries))]
    public void ReferencesOnlyKnownNodeLabels(string name, string cypher)
    {
        // Matched in NODE position specifically -- "(" then an optional alias, then ":Label" -- rather
        // than by scanning for every ":Name" and excusing the ones that turn out to be relationship
        // types. The old form kept a hand-written skip list containing exactly one entry, so the first
        // builder to emit a second relationship type failed this test for a reason that had nothing to
        // do with node labels. A guard that enumerates its own exceptions by hand grades whatever it
        // was born knowing about.
        var known = new HashSet<string> { "Fact", "Entity", "Preference" };
        var matches = System.Text.RegularExpressions.Regex.Matches(
            cypher, @"\(\s*[A-Za-z_][A-Za-z0-9_]*\s*:(?<label>[A-Z][A-Za-z_]+)|\(\s*:(?<label2>[A-Z][A-Za-z_]+)");
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            var label = m.Groups["label"].Success ? m.Groups["label"].Value : m.Groups["label2"].Value;
            known.Should().Contain(label, $"{name} references an unknown node label :{label}");
        }
    }

    [Theory]
    [MemberData(nameof(AllMethodBuiltQueries))]
    public void ReferencesOnlyKnownRelationshipTypes(string name, string cypher)
    {
        // The other half, which the old single-test form could not express: a relationship type is
        // matched in RELATIONSHIP position, "[" then an optional alias, then ":TYPE". A typo here binds
        // nothing and fails silently -- the supersede cascade would simply never fire.
        var known = new HashSet<string> { "SUPERSEDED_BY", "DERIVED_FROM" };
        var matches = System.Text.RegularExpressions.Regex.Matches(
            cypher, @"\[\s*[A-Za-z_][A-Za-z0-9_]*\s*:(?<type>[A-Z][A-Z_]+)|\[\s*:(?<type2>[A-Z][A-Z_]+)");
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            var type = m.Groups["type"].Success ? m.Groups["type"].Value : m.Groups["type2"].Value;
            known.Should().Contain(type, $"{name} references an unknown relationship type :{type}");
        }
    }

    private static int Balance(string s, char open, char close)
    {
        int depth = 0;
        foreach (var ch in s)
        {
            if (ch == open) depth++;
            else if (ch == close) depth--;
        }
        return depth;
    }
}
