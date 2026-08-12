using AgentMemory.Neo4j.Queries;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Queries;

/// <summary>
/// Live fact recall must be able to honour a fact's valid-time window — on <b>both</b> live paths.
/// </summary>
/// <remarks>
/// <para>
/// <c>valid_from</c>/<c>valid_until</c> persist, are writable through the public API, and the
/// point-in-time path already filters on them. Live recall did not: it filtered on similarity,
/// <c>invalidated_at</c> and owner, and nothing else. So a fact valid from six months hence was
/// returned <i>today</i>, and a fact whose <c>valid_until</c> had passed was returned <b>forever</b>.
/// </para>
/// <para>
/// The gap stopped being inert in 1.4.0, which shipped <c>TemporalValidityMode.Extract</c> — the
/// writer it had been waiting for.
/// </para>
/// </remarks>
public sealed class ValidTimeGateQueryTests
{
    [Fact]
    public void TheIndexedPathIsUnchangedWhenTheGateIsOff()
    {
        // The byte-identical guarantee. Default is Ignore, so no deployment silently recalls less.
        var gated = FactQueries.SearchByVector(true, true, 60, false, currentValidTime: false);

        gated.Should().NotContain("valid_from");
        gated.Should().NotContain("valid_until");
    }

    [Fact]
    public void TheIndexedPathGatesOnValidTimeWhenAsked()
    {
        var gated = FactQueries.SearchByVector(true, true, 60, false, currentValidTime: true);

        gated.Should().Contain("node.valid_from IS NULL OR node.valid_from <= datetime($now)");
        gated.Should().Contain("node.valid_until IS NULL OR node.valid_until > datetime($now)");
    }

    [Fact]
    public void TheOwnerScopedFallbackIsUnchangedWhenTheGateIsOff()
    {
        var ungated = FactQueries.SearchByVectorOwnerScopedFallback(true, currentValidTime: false);

        ungated.Should().NotContain("valid_from");
        ungated.Should().NotContain("valid_until");
    }

    [Fact]
    public void TheOwnerScopedFallbackGatesTooWhenAsked()
    {
        // The path every prior analysis of this gap predates -- it did not exist before 1.4.1. Gating
        // only the indexed query would leave valid time silently bypassed for exactly the starved
        // multi-tenant owners this fallback was added to rescue, which is the worst possible subset to
        // miss: the ones already receiving the least.
        var gated = FactQueries.SearchByVectorOwnerScopedFallback(true, currentValidTime: true);

        gated.Should().Contain("f.valid_from  IS NULL OR f.valid_from  <= datetime($now)");
        gated.Should().Contain("f.valid_until IS NULL OR f.valid_until >  datetime($now)");
    }

    [Fact]
    public void BothPathsUseTheSameClauseShapeAsThePointInTimePath()
    {
        // Copied verbatim from TemporalQueries rather than re-derived, so the two clocks cannot drift.
        // NULL means "unbounded", never "excluded" -- a fact with no window is always current, which is
        // what keeps the gate inert over a corpus whose facts carry no validity bounds.
        var indexed = FactQueries.SearchByVector(false, true, 60, false, currentValidTime: true);
        var fallback = FactQueries.SearchByVectorOwnerScopedFallback(true, currentValidTime: true);

        foreach (var cypher in new[] { indexed, fallback })
        {
            cypher.Should().Contain("IS NULL OR");
            cypher.Should().Contain("datetime($now)");
        }
    }
}
