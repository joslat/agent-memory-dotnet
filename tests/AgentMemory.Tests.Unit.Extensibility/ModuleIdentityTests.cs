using AgentMemory.Extensibility.Modules;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

public sealed class ModuleIdentityTests
{
    [Theory]
    [InlineData("com.example.reviews", true)]
    [InlineData("io.agentmemory.fixture.topics", true)]
    [InlineData("com.example.review-items", true)]
    [InlineData("reviews", false)]
    [InlineData("com.example", false)]
    [InlineData("Com.example.reviews", false)]
    [InlineData("com..reviews", false)]
    [InlineData("com.example.reviews\n", false)]
    [InlineData("com.example:reviews", false)]
    public void ValidatesDottedModuleNames(string value, bool expected) =>
        ModuleIdentity.IsValidModuleId(value).Should().Be(expected);

    [Fact]
    public void LengthAndReservedPrefixAreExplicit()
    {
        ModuleIdentity.IsValidModuleId("com.example." + new string('a', 53)).Should().BeFalse();
        ModuleIdentity.IsReservedModuleId("io.agentmemory.fixture").Should().BeTrue();
        ModuleIdentity.IsReservedModuleId("io.agentmemory-other.fixture").Should().BeFalse();
    }

    [Fact]
    public void OwnedIdsCannotCrossModuleOrInjectAnotherNamespace()
    {
        ModuleIdentity.OwnedId("com.example.reviews", "due").Should().Be("com.example.reviews:due");
        ModuleIdentity.IsOwnedId("com.example.reviews", "com.example.reviews:due").Should().BeTrue();
        ModuleIdentity.IsOwnedId("com.example.reviews", "com.example.other:due").Should().BeFalse();
        var build = () => ModuleIdentity.OwnedId("com.example.reviews", "other:due");
        build.Should().Throw<ArgumentException>();
    }
}
