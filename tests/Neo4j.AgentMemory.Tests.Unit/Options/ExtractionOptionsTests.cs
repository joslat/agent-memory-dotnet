using FluentAssertions;
using Neo4j.AgentMemory.Abstractions.Options;

namespace Neo4j.AgentMemory.Tests.Unit.OptionsTests;

public class ExtractionOptionsTests
{
    [Fact]
    public void Default_EntityResolutionIsNotNull()
    {
        var options = new ExtractionOptions();
        options.EntityResolution.Should().NotBeNull();
    }

    [Fact]
    public void Default_ValidationIsNotNull()
    {
        var options = new ExtractionOptions();
        options.Validation.Should().NotBeNull();
    }

    [Fact]
    public void Default_MinConfidenceThresholdIs050()
    {
        var options = new ExtractionOptions();
        options.MinConfidenceThreshold.Should().Be(0.5);
    }

    [Fact]
    public void Default_EnableAutoMergeIsTrue()
    {
        var options = new ExtractionOptions();
        options.EnableAutoMerge.Should().BeTrue();
    }

    [Fact]
    public void Default_AutoMergeThresholdIs095()
    {
        var options = new ExtractionOptions();
        options.AutoMergeThreshold.Should().Be(0.95);
    }

    [Fact]
    public void Default_SameAsThresholdIs085()
    {
        var options = new ExtractionOptions();
        options.SameAsThreshold.Should().Be(0.85);
    }

    [Fact]
    public void Default_SameAsThresholdIsLessThanAutoMerge()
    {
        var options = new ExtractionOptions();
        options.SameAsThreshold.Should().BeLessThan(options.AutoMergeThreshold);
    }

    [Fact]
    public void EntityResolution_DefaultEnablesAllStrategies()
    {
        var options = new EntityResolutionOptions();
        options.EnableExactMatch.Should().BeTrue();
        options.EnableFuzzyMatch.Should().BeTrue();
        options.EnableSemanticMatch.Should().BeTrue();
    }

    [Fact]
    public void EntityResolution_DefaultTypeStrictFilteringIsTrue()
    {
        var options = new EntityResolutionOptions();
        options.TypeStrictFiltering.Should().BeTrue();
    }

    [Fact]
    public void EntityResolution_FuzzyThresholdDefault()
    {
        var options = new EntityResolutionOptions();
        options.FuzzyMatchThreshold.Should().Be(0.85);
    }

    [Fact]
    public void EntityResolution_SemanticThresholdDefault()
    {
        var options = new EntityResolutionOptions();
        options.SemanticMatchThreshold.Should().Be(0.8);
    }

    [Fact]
    public void EntityValidation_DefaultMinNameLengthIs2()
    {
        var options = new EntityValidationOptions();
        options.MinNameLength.Should().Be(2);
    }

    [Fact]
    public void EntityValidation_DefaultRejectionsEnabled()
    {
        var options = new EntityValidationOptions();
        options.RejectNumericOnly.Should().BeTrue();
        options.RejectPunctuationOnly.Should().BeTrue();
        options.UseStopwordFilter.Should().BeTrue();
    }
}
