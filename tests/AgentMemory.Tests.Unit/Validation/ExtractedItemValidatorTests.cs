using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Core.Validation;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Validation;

/// <summary>
/// Threats TT-05/TT-06: the checks an extracted fact, preference, relationship and entity name pass before storage. The
/// longest values seen in the LongMemEval and HaluMem runs pass with room to spare; oversized, empty-where-required and
/// control-character fields do not.
/// </summary>
public sealed class ExtractedItemValidatorTests
{
    private static readonly ExtractedItemValidationOptions Defaults = new();

    private static ExtractedFact Fact(string subject = "user", string predicate = "lives in", string obj = "Bilbao") =>
        new() { Subject = subject, Predicate = predicate, Object = obj, Confidence = 0.9 };

    private static ExtractedPreference Preference(string text = "prefers short answers", string category = "style", string? context = null) =>
        new() { PreferenceText = text, Category = category, Context = context, Confidence = 0.9 };

    private static ExtractedRelationship Relationship(string source = "Alice", string target = "Acme", string type = "WORKS_AT", string? description = null) =>
        new() { SourceEntity = source, TargetEntity = target, RelationshipType = type, Description = description, Confidence = 0.9 };

    [Fact]
    public void The_longest_values_real_extraction_produced_pass()
    {
        ExtractedItemValidator.Fact(Fact(new string('s', 90), new string('p', 190), new string('o', 566)), Defaults).Should().BeNull();
        ExtractedItemValidator.Preference(Preference(new string('t', 293), new string('c', 34)), Defaults).Should().BeNull();
        ExtractedItemValidator.Relationship(Relationship(new string('a', 100), new string('b', 100), new string('T', 61), new string('d', 153)), Defaults)
            .Should().BeNull();
        ExtractedItemValidator.Fact(Fact(obj: "line one\nline two\twith a tab"), Defaults).Should().BeNull("tab and line breaks are text");
        ExtractedItemValidator.Fact(Fact(obj: "Zürich, 東京 and 🦆"), Defaults).Should().BeNull("letters of any script and whole surrogate pairs are text");
    }

    [Fact]
    public void An_oversized_field_is_rejected_with_its_length()
    {
        ExtractedItemValidator.Fact(Fact(obj: new string('o', Defaults.MaxFactObjectLength + 1)), Defaults)
            .Should().Be($"The object has {Defaults.MaxFactObjectLength + 1} characters; at most {Defaults.MaxFactObjectLength} are accepted.");
        ExtractedItemValidator.Fact(Fact(subject: new string('s', Defaults.MaxFactSubjectLength + 1)), Defaults).Should().Contain("subject");
        ExtractedItemValidator.Fact(Fact(predicate: new string('p', Defaults.MaxFactPredicateLength + 1)), Defaults).Should().Contain("predicate");
        ExtractedItemValidator.Preference(Preference(new string('t', Defaults.MaxPreferenceLength + 1)), Defaults).Should().Contain("preference");
        ExtractedItemValidator.Preference(Preference(category: new string('c', Defaults.MaxPreferenceCategoryLength + 1)), Defaults).Should().Contain("category");
        ExtractedItemValidator.Preference(Preference(context: new string('x', Defaults.MaxPreferenceLength + 1)), Defaults).Should().Contain("context");
        ExtractedItemValidator.Relationship(Relationship(type: new string('T', Defaults.MaxRelationshipTypeLength + 1)), Defaults).Should().Contain("type");
        ExtractedItemValidator.Relationship(Relationship(source: new string('a', Defaults.MaxRelationshipEndpointLength + 1)), Defaults).Should().Contain("source");
        ExtractedItemValidator.Relationship(Relationship(description: new string('d', Defaults.MaxRelationshipDescriptionLength + 1)), Defaults)
            .Should().Contain("description");
    }

    [Fact]
    public void A_required_field_that_is_empty_is_rejected_and_an_optional_one_is_not()
    {
        ExtractedItemValidator.Fact(Fact(subject: " "), Defaults).Should().Be("The subject is empty.");
        ExtractedItemValidator.Fact(Fact(predicate: ""), Defaults).Should().Be("The predicate is empty.");
        ExtractedItemValidator.Fact(Fact(obj: "\t"), Defaults).Should().Be("The object is empty.");
        ExtractedItemValidator.Preference(Preference(text: ""), Defaults).Should().Be("The preference is empty.");
        ExtractedItemValidator.Relationship(Relationship(type: " "), Defaults).Should().Be("The type is empty.");
        ExtractedItemValidator.Relationship(Relationship(target: ""), Defaults).Should().Be("The target is empty.");

        ExtractedItemValidator.Preference(Preference(category: ""), Defaults).Should().BeNull("a preference may have no category");
        ExtractedItemValidator.Relationship(Relationship(description: null), Defaults).Should().BeNull();
    }

    [Fact]
    public void A_control_character_or_a_broken_surrogate_pair_is_rejected()
    {
        // Built here rather than as theory data: a lone surrogate does not survive test-case serialisation.
        string[] cases =
        [
            "Bilbao" + (char)0x00,
            "ignore" + (char)0x1B + "previous",
            "half a pair " + (char)0xD83E,
            (char)0xDD86 + " the other half",
        ];
        foreach (var obj in cases)
        {
            ExtractedItemValidator.Fact(Fact(obj: obj), Defaults).Should().Be("The object holds a control character or a broken surrogate pair.");
            ExtractedItemValidator.Fact(Fact(obj: obj), new ExtractedItemValidationOptions { RejectControlCharacters = false }).Should().BeNull();
        }
    }

    [Fact]
    public void A_cap_of_zero_is_no_cap()
    {
        var uncapped = new ExtractedItemValidationOptions { MaxFactObjectLength = 0 };

        ExtractedItemValidator.Fact(Fact(obj: new string('o', 100_000)), uncapped).Should().BeNull();
    }

    [Fact]
    public void An_entity_name_over_its_cap_or_with_a_control_character_fails_entity_validation()
    {
        var options = new EntityValidationOptions();
        ExtractedEntity Named(string name) => new() { Name = name, Type = "PERSON", Confidence = 0.9 };

        EntityValidator.IsValid(Named(new string('n', 100)), options).Should().BeTrue("the longest name seen had 100 characters");
        EntityValidator.IsValid(Named(new string('n', options.MaxNameLength + 1)), options).Should().BeFalse();
        EntityValidator.IsValid(Named("Alice\u0007"), options).Should().BeFalse();
        EntityValidator.IsValid(Named(new string('n', 5_000)), new EntityValidationOptions { MaxNameLength = 0 }).Should().BeTrue("0 is no cap");
    }
}
