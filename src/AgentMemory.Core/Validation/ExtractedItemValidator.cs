using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Core.Validation;

/// <summary>
/// Threats TT-05 and TT-06: the checks an extracted fact, preference or relationship passes before it is stored. Each
/// returns null when the item passes, or the reason it does not (for the Validation outcome). Required fields must hold
/// text; every checked field must fit its cap (<see cref="ExtractedItemValidationOptions"/>) and, unless turned off, hold
/// no control character (tab and line breaks aside) and no broken surrogate pair.
/// </summary>
internal static class ExtractedItemValidator
{
    /// <summary>Subject, predicate and object are required.</summary>
    public static string? Fact(ExtractedFact fact, ExtractedItemValidationOptions options)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentNullException.ThrowIfNull(options);
        return Required("subject", fact.Subject, options.MaxFactSubjectLength, options)
            ?? Required("predicate", fact.Predicate, options.MaxFactPredicateLength, options)
            ?? Required("object", fact.Object, options.MaxFactObjectLength, options);
    }

    /// <summary>The text is required; the category and context may be empty.</summary>
    public static string? Preference(ExtractedPreference preference, ExtractedItemValidationOptions options)
    {
        ArgumentNullException.ThrowIfNull(preference);
        ArgumentNullException.ThrowIfNull(options);
        return Required("preference", preference.PreferenceText, options.MaxPreferenceLength, options)
            ?? Optional("category", preference.Category, options.MaxPreferenceCategoryLength, options)
            ?? Optional("context", preference.Context, options.MaxPreferenceLength, options);
    }

    /// <summary>Both endpoints and the type are required; the description may be empty.</summary>
    public static string? Relationship(ExtractedRelationship relationship, ExtractedItemValidationOptions options)
    {
        ArgumentNullException.ThrowIfNull(relationship);
        ArgumentNullException.ThrowIfNull(options);
        return Required("source", relationship.SourceEntity, options.MaxRelationshipEndpointLength, options)
            ?? Required("target", relationship.TargetEntity, options.MaxRelationshipEndpointLength, options)
            ?? Required("type", relationship.RelationshipType, options.MaxRelationshipTypeLength, options)
            ?? Optional("description", relationship.Description, options.MaxRelationshipDescriptionLength, options);
    }

    /// <summary>An entity name over the cap, or with a control character (the rest of entity validation is <see cref="EntityValidator"/>).</summary>
    public static bool NameFits(string name, int maxLength) =>
        (maxLength <= 0 || name.Length <= maxLength) && !HasControlOrBrokenSurrogate(name);

    private static string? Required(string field, string? value, int maxLength, ExtractedItemValidationOptions options) =>
        string.IsNullOrWhiteSpace(value) ? $"The {field} is empty." : Optional(field, value, maxLength, options);

    private static string? Optional(string field, string? value, int maxLength, ExtractedItemValidationOptions options)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (maxLength > 0 && value.Length > maxLength)
            return $"The {field} has {value.Length} characters; at most {maxLength} are accepted.";
        if (options.RejectControlCharacters && HasControlOrBrokenSurrogate(value))
            return $"The {field} holds a control character or a broken surrogate pair.";
        return null;
    }

    private static bool HasControlOrBrokenSurrogate(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsControl(c) && c is not ('\t' or '\n' or '\r')) return true;
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1])) return true;
                i++;
            }
            else if (char.IsLowSurrogate(c)) return true;
        }
        return false;
    }
}
