using FluentAssertions;
using AgentMemory.Core.Stubs;

namespace AgentMemory.Tests.Unit.Stubs;

public class GuidIdGeneratorTests
{
    [Fact]
    public void GenerateId_ReturnsNonEmptyString()
    {
        var gen = new GuidIdGenerator();
        gen.GenerateId().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GenerateId_ReturnsUniqueValues()
    {
        var gen = new GuidIdGenerator();
        var ids = Enumerable.Range(0, 100).Select(_ => gen.GenerateId()).ToList();
        ids.Distinct().Should().HaveCount(100);
    }

    [Fact]
    public void GenerateId_HasNoHyphens()
    {
        var gen = new GuidIdGenerator();
        gen.GenerateId().Should().NotContain("-");
    }

    [Fact]
    public void GenerateId_Has32Characters()
    {
        var gen = new GuidIdGenerator();
        gen.GenerateId().Should().HaveLength(32);
    }
}
