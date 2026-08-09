using AgentMemory.Abstractions.Options;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.OptionsTests;

public sealed class BatchEntityResolutionOptionsTests
{
    [Fact]
    public void UseBatchEntityResolutionSnapshots_DefaultsOn()
    {
        new ExtractionOptions().UseBatchEntityResolutionSnapshots.Should().BeTrue();
    }
}
