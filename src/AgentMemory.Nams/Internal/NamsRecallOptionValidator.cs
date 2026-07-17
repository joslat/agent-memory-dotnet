using AgentMemory.Nams.Recall;

namespace AgentMemory.Nams.Internal;

/// <summary>Validation predicates for <see cref="NamsRecallOptions"/>, matching this repository's established
/// convention (see <see cref="NamsOptionValidator"/>) of extracted, individually testable predicate methods
/// called from a fluent <c>AddOptions&lt;T&gt;().Validate(...)</c> chain.</summary>
internal static class NamsRecallOptionValidator
{
    public static bool HasPositiveEntitySearchLimit(NamsRecallOptions options) => options.EntitySearchLimit > 0;

    public static bool HasPositiveMaxTotalCharacters(NamsRecallOptions options) => options.MaxTotalCharacters > 0;
}
