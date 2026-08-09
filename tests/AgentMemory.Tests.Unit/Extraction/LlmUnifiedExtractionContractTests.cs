using AgentMemory.Extraction.Llm;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Extraction;

public sealed class LlmUnifiedExtractionContractTests
{
    [Fact]
    public void Options_ExposeExplicitReversibleUnifiedExtractionSwitch()
    {
        var property = typeof(LlmExtractionOptions).GetProperty("UseUnifiedExtraction");

        property.Should().NotBeNull(
            "LAB-U1 must be reversible and the four-call compatibility path must remain explicit");
        property!.PropertyType.Should().Be(typeof(bool));
        property.GetValue(new LlmExtractionOptions()).Should().Be(false,
            "the compatibility path remains the default until live quality acceptance promotes it");
    }
}
