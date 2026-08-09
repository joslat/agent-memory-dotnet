using System.Reflection;
using AgentMemory.Cli.Commands;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Cli;

/// <summary>
/// D1 pool-curve contract: the perf command must resolve an explicit, fingerprinted pool-size
/// override for the integrated cold-build lab only, and must retain the existing 16/100 defaults
/// byte-for-byte when no override is supplied.
/// </summary>
public sealed class PerfPoolSizeTests
{
    private static MethodInfo Resolver()
    {
        var method = typeof(PerfCommand).GetMethod(
            "ResolveMaxConnectionPoolSize",
            BindingFlags.Static | BindingFlags.NonPublic);
        method.Should().NotBeNull(
            "the D1 pool curve requires an explicit pool-size resolution seam on PerfCommand");
        return method!;
    }

    private static object Invoke(string? value, bool unified, string[] ids)
    {
        try
        {
            return Resolver().Invoke(null, [value, unified, ids])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    [Fact]
    public void NoOverride_RetainsUnifiedLabDefault16()
        => Invoke(null, true, ["PERF-W-12-X10"]).Should().Be(16);

    [Fact]
    public void NoOverride_RetainsDefaultCatalog100()
        => Invoke(null, false, ["PERF-R-04", "PERF-W-02"]).Should().Be(100);

    [Fact]
    public void Override_AppliesToIntegratedColdBuildScenariosOnly()
        => Invoke("24", true, ["PERF-W-12-X10"]).Should().Be(24);

    [Fact]
    public void Override_AppliesAcrossAllIntegratedArms()
        => Invoke("32", true, ["PERF-W-12-X01", "PERF-W-12-X05", "PERF-W-12-X10"]).Should().Be(32);

    [Fact]
    public void Override_IsRejectedForDefaultCatalogScenarios()
    {
        var act = () => Invoke("24", false, ["PERF-R-04"]);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Override_IsRejectedForNonIntegratedUnifiedLabScenarios()
    {
        var act = () => Invoke("24", true, ["PERF-W-10-C10"]);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    public void Override_RejectsNonPositiveOrMalformedValues(string value)
    {
        var act = () => Invoke(value, true, ["PERF-W-12-X10"]);
        act.Should().Throw<ArgumentException>();
    }
}
