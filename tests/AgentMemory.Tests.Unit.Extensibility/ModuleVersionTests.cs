using AgentMemory.Extensibility.Modules;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

public sealed class ModuleVersionTests
{
    [Theory]
    [InlineData("0.1.0")]
    [InlineData("1.2.3-alpha.1+001")]
    [InlineData("1.0.0-x-y-z.--")]
    [InlineData("999999999999999999999999999999.0.0")]
    public void ValidSemVerRoundTrips(string value) => ModuleVersion.Parse(value).ToString().Should().Be(value);

    [Theory]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("v1.2.3")]
    [InlineData("01.2.3")]
    [InlineData("1.2.03")]
    [InlineData("1.2.3-01")]
    [InlineData("1.2.3-a..b")]
    [InlineData("1.2.3+")]
    [InlineData("1.2.3+x+y")]
    [InlineData("1.2.3\n")]
    [InlineData(" 1.2.3")]
    [InlineData("1.2.3-\u00e9")]
    public void InvalidSemVerIsRefused(string value)
    {
        var parse = () => ModuleVersion.Parse(value);
        parse.Should().Throw<FormatException>();
    }

    [Fact]
    public void PrecedenceMatchesSemVerIncludingNumericIdentifiersAndMetadata()
    {
        string[] ordered = ["1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.1.0", "2.0.0"];
        for (var i = 1; i < ordered.Length; i++)
            ModuleVersion.Parse(ordered[i - 1]).CompareTo(ModuleVersion.Parse(ordered[i])).Should().BeLessThan(0);
        ModuleVersion.Parse("1.0.0+abc").CompareTo(ModuleVersion.Parse("1.0.0+other")).Should().Be(0);
        ModuleVersion.Parse("1.0.0-999999999999999999999").CompareTo(ModuleVersion.Parse("1.0.0-a")).Should().BeLessThan(0);
        ModuleVersion.Parse("100000000000000000000000.0.0").CompareTo(ModuleVersion.Parse("99999999999999999999999.0.0")).Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData("[0.1,0.2)", "0.1.0", true)]
    [InlineData("[0.1,0.2)", "0.2.0", false)]
    [InlineData("[0.1,0.2)", "0.2.0-preview.1", true)]
    [InlineData("[1.0.0]", "1.0.0+build", true)]
    [InlineData("[1.0.0]", "1.0.0-rc.1", false)]
    [InlineData("(1,2]", "1.0.0", false)]
    [InlineData("(1,2]", "2.0.0", true)]
    [InlineData("(,1.0]", "0.0.0-0", true)]
    [InlineData("[1.0,)", "999.0.0", true)]
    [InlineData("1.0", "1.1.0", true)]
    [InlineData("(,)", "0.0.0-alpha", true)]
    public void RangesHonorBoundsAndPrereleasePrecedence(string range, string version, bool expected) =>
        ModuleVersionRange.Parse(range).Contains(ModuleVersion.Parse(version)).Should().Be(expected);

    [Theory]
    [InlineData("")]
    [InlineData("[2,1)")]
    [InlineData("[1,1)")]
    [InlineData("(1)")]
    [InlineData("[,1]")]
    [InlineData("[1,]")]
    [InlineData("[1,2,3]")]
    [InlineData("1.*")]
    [InlineData("[1.0.0.1,2)")]
    [InlineData("[1.0,2.0)garbage")]
    public void MalformedOrEmptyRangesAreRefused(string value)
    {
        var parse = () => ModuleVersionRange.Parse(value);
        parse.Should().Throw<FormatException>();
    }
}
