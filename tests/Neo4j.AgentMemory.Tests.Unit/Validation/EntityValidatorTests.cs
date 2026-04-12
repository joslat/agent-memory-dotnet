using FluentAssertions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Options;
using Neo4j.AgentMemory.Core.Validation;

namespace Neo4j.AgentMemory.Tests.Unit.Validation;

public sealed class EntityValidatorTests
{
    private static EntityValidationOptions DefaultOptions => new();

    private static ExtractedEntity MakeEntity(string name) =>
        new() { Name = name, Type = "Person" };

    // --- Stopword rejection ---

    [Theory]
    [InlineData("the")]
    [InlineData("and")]
    [InlineData("is")]
    [InlineData("I")]
    [InlineData("we")]
    [InlineData("they")]
    [InlineData("a")]
    [InlineData("an")]
    [InlineData("or")]
    [InlineData("but")]
    public void IsValid_StopwordName_ReturnsFalse(string stopword)
    {
        var entity = MakeEntity(stopword);
        EntityValidator.IsValid(entity, DefaultOptions).Should().BeFalse();
    }

    [Fact]
    public void IsValid_StopwordIsCaseInsensitive()
    {
        EntityValidator.IsValid(MakeEntity("THE"), DefaultOptions).Should().BeFalse();
        EntityValidator.IsValid(MakeEntity("And"), DefaultOptions).Should().BeFalse();
    }

    // --- Minimum length rejection ---

    [Fact]
    public void IsValid_NameShorterThanMinLength_ReturnsFalse()
    {
        var options = new EntityValidationOptions { MinNameLength = 3 };
        EntityValidator.IsValid(MakeEntity("Jo"), options).Should().BeFalse();
    }

    [Fact]
    public void IsValid_EmptyName_ReturnsFalse()
    {
        EntityValidator.IsValid(MakeEntity(""), DefaultOptions).Should().BeFalse();
    }

    [Fact]
    public void IsValid_SingleCharName_ReturnsFalse()
    {
        EntityValidator.IsValid(MakeEntity("X"), DefaultOptions).Should().BeFalse();
    }

    // --- Numeric-only rejection ---

    [Theory]
    [InlineData("123")]
    [InlineData("42")]
    [InlineData("3.14")]
    [InlineData("1,000")]
    public void IsValid_NumericOnlyName_ReturnsFalse(string numericName)
    {
        EntityValidator.IsValid(MakeEntity(numericName), DefaultOptions).Should().BeFalse();
    }

    [Fact]
    public void IsValid_NumericCheckDisabled_AllowsNumericOnly()
    {
        var options = new EntityValidationOptions { RejectNumericOnly = false };
        EntityValidator.IsValid(MakeEntity("42"), options).Should().BeTrue();
    }

    // --- Punctuation-only rejection ---

    [Theory]
    [InlineData("...")]
    [InlineData("!!!")]
    [InlineData("---")]
    [InlineData("()")]
    public void IsValid_PunctuationOnlyName_ReturnsFalse(string punctuation)
    {
        EntityValidator.IsValid(MakeEntity(punctuation), DefaultOptions).Should().BeFalse();
    }

    [Fact]
    public void IsValid_PunctuationCheckDisabled_AllowsPunctuationOnly()
    {
        var options = new EntityValidationOptions { RejectPunctuationOnly = false };
        EntityValidator.IsValid(MakeEntity("..."), options).Should().BeTrue();
    }

    // --- Valid entities pass through ---

    [Theory]
    [InlineData("Alice")]
    [InlineData("OpenAI")]
    [InlineData("New York")]
    [InlineData("GPT-4")]
    [InlineData("John Smith")]
    public void IsValid_ValidName_ReturnsTrue(string name)
    {
        EntityValidator.IsValid(MakeEntity(name), DefaultOptions).Should().BeTrue();
    }

    // --- Stopword filter can be disabled ---

    [Fact]
    public void IsValid_StopwordFilterDisabled_AllowsStopword()
    {
        var options = new EntityValidationOptions { UseStopwordFilter = false };
        EntityValidator.IsValid(MakeEntity("the"), options).Should().BeTrue();
    }

    // --- Bulk validation ---

    [Fact]
    public void ValidateEntities_FiltersOutInvalidEntities()
    {
        var entities = new List<ExtractedEntity>
        {
            MakeEntity("Alice"),
            MakeEntity("the"),
            MakeEntity("42"),
            MakeEntity("OpenAI"),
            MakeEntity("..."),
            MakeEntity("Bob")
        };

        var valid = EntityValidator.ValidateEntities(entities, DefaultOptions);

        valid.Should().HaveCount(3);
        valid.Select(e => e.Name).Should().BeEquivalentTo(new[] { "Alice", "OpenAI", "Bob" });
    }

    [Fact]
    public void ValidateEntities_EmptyList_ReturnsEmpty()
    {
        var result = EntityValidator.ValidateEntities(Array.Empty<ExtractedEntity>(), DefaultOptions);
        result.Should().BeEmpty();
    }

    [Fact]
    public void ValidateEntities_AllValid_ReturnsAll()
    {
        var entities = new[] { MakeEntity("Alice"), MakeEntity("OpenAI") };
        var result = EntityValidator.ValidateEntities(entities, DefaultOptions);
        result.Should().HaveCount(2);
    }
}
