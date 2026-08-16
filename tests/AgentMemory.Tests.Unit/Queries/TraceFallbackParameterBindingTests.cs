using System.Text.RegularExpressions;
using AgentMemory.Neo4j.Queries;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Queries;

/// <summary>
/// Every <c>$parameter</c> a generated trace query references must be one the repository binds.
/// </summary>
/// <remarks>
/// <para>
/// <b>The bug this exists to prevent, which shipped.</b>
/// <c>SearchByTaskVectorOwnerScopedFallback</c> emits <c>AND t.success = $successFilter</c> whenever a
/// success filter is requested, and the repository's scan built a parameter dictionary that never bound
/// it. Every owner-scoped trace search carrying a success filter threw
/// <i>"Expected parameter(s): successFilter"</i> the moment it reached that last-resort scan.
/// </para>
/// <para>
/// It is not a rare path for procedural recall. A <c>proceduresOnly</c> search returns zero from the
/// indexed pass <b>by construction</b> whenever the corpus holds no promoted procedures — which is
/// precisely the condition that escalates to the scan. The first real consumer of procedure retrieval
/// hit it on its first run.
/// </para>
/// <para>
/// Unit tests could not have caught it by exercising the repository, because the failure is the
/// database rejecting the statement. So the invariant is asserted where it can be: the parameter names
/// a query <i>mentions</i> must be a subset of the names its caller <i>binds</i>.
/// </para>
/// </remarks>
public sealed class TraceFallbackParameterBindingTests
{
    /// <summary>Parameter names the repository binds for the owner-scoped fallback scan.</summary>
    /// <remarks>
    /// Mirrors <c>Neo4jReasoningTraceRepository.OwnerScopedScanAsync</c>. If that method starts binding
    /// something else, this list is the thing to update — and the test below will say so.
    /// </remarks>
    private static readonly string[] Bound =
        ["embedding", "limit", "minScore", "ownerId", "successFilter"];

    [Theory]
    [InlineData(false, false, null)]
    [InlineData(true, false, null)]
    [InlineData(false, true, null)]
    [InlineData(true, true, null)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    public void TheFallbackScanReferencesOnlyParametersTheRepositoryBinds(
        bool hasSuccessFilter, bool includeShared, bool? proceduresOnly)
    {
        var cypher = ReasoningQueries.SearchByTaskVectorOwnerScopedFallback(
            hasSuccessFilter, includeShared, proceduresOnly);

        Referenced(cypher).Should().BeSubsetOf(Bound,
            "a generated query referencing an unbound parameter fails at the database, not at compile "
            + "time, and only on the code path that generates it");
    }

    [Fact]
    public void TheSuccessFilterIsReferencedExactlyWhenItIsRequested()
    {
        // Both directions. Referencing it when unrequested is the crash; omitting it when requested
        // would silently widen the search to include failed traces -- and a procedure recalled from a
        // FAILED trace is a method known not to work.
        Referenced(ReasoningQueries.SearchByTaskVectorOwnerScopedFallback(true, false, null))
            .Should().Contain("successFilter");

        Referenced(ReasoningQueries.SearchByTaskVectorOwnerScopedFallback(false, false, null))
            .Should().NotContain("successFilter");
    }

    [Theory]
    [InlineData(true, false, null)]
    [InlineData(true, true, true)]
    public void TheIndexedQueryHasTheSameProperty(
        bool hasSuccessFilter, bool includeShared, bool? proceduresOnly)
    {
        // The indexed path binds the same names plus nothing extra; it was already correct, and this
        // keeps the two from drifting apart the way the fallback drifted from its caller.
        var cypher = ReasoningQueries.SearchByTaskVector(
            hasSuccessFilter, hasOwnerFilter: true, includeShared, topK: 10, proceduresOnly);

        Referenced(cypher).Should().BeSubsetOf(Bound);
    }

    private static HashSet<string> Referenced(string cypher) =>
        Regex.Matches(cypher, @"\$(\w+)")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
}
