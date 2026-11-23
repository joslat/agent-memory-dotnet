using System.Collections;
using System.Reflection;
using AgentMemory.Abstractions.Options;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Configuration;

/// <summary>
/// DEF-check (accepted by the owner, 17 Nov 2026): every option where the library's default and the Conversational
/// preset differ has a written reason here. A new difference without one fails, and so does a reason whose difference
/// is gone, so a default can fall behind the preset only on purpose.
/// </summary>
public sealed class PresetDefaultDifferenceTests
{
    /// <summary>Option path (as the test prints it) → why the preset and the default differ.</summary>
    private static readonly Dictionary<string, string> Reasons = new(StringComparer.Ordinal)
    {
        // What is stored. Each changes what a write keeps, and every recorded measurement used the default, so a flip would
        // move results with no setting changed: the preset opts in, a default changes only in a major version.
        ["MemoryOptions.Extraction.CanonicalFactSubjects"] = "Stores a fact by the resolved entity's name: changes stored subjects.",
        ["MemoryOptions.Extraction.DeduplicateWithinExtraction"] = "Drops near-identical facts of one extraction: changes what is stored.",
        ["MemoryOptions.Extraction.LinkFactsToEntities"] = "Writes :ABOUT edges from facts to entities: changes the stored graph.",
        ["MemoryOptions.Extraction.RenameOnCorrectedName"] = "Merges an entity into its corrected name: changes stored entities.",
        ["MemoryOptions.Extraction.SupersedeReplacedFacts"] = "Closes a replaced functional fact: changes what live recall returns.",
        ["MemoryOptions.Extraction.EntityResolution.EnablePartialNameMatch"] = "Resolves \"Priya\" to \"Priya Nair\": changes which entity a mention joins.",
        ["MemoryOptions.Extraction.EntityResolution.TypeStrictFiltering"] = "The preset trusts a shared name over an unreliable type: changes which entity a mention joins.",
        // What is extracted at all.
        ["MemoryOptions.Extraction.SkipPlainQuestions"] = "No model call for a turn that is only a plain question: nothing is extracted from it.",
        ["MemoryOptions.Extraction.SkipUninformativeTurns"] = "No model call for greetings, thanks and acknowledgements: nothing is extracted from them.",
        // What recall returns.
        ["MemoryOptions.FanOut.Enabled"] = "Splits a question into parts and recalls each: more searches, other items.",
        ["MemoryOptions.Recall.MaxRelationships"] = "Adds a relationships section to the context: 0 keeps the context as it was.",
        ["MemoryOptions.ResolveTemporalQueries"] = "Routes a question that names a past time to recall as of that time.",
        ["MemoryOptions.WorkingMemory.RecentTopicsDays"] = "Adds the \"Lately\" line to the profile block: 0 leaves the block as it was.",
        ["MemoryOptions.Isolation.Mode"] = "StrictMultiTenant fails an unscoped recall closed; the default keeps SingleTenant for compatibility.",
        // Speed only, the same results. DEF-check found these three with no recorded reason (17 Nov 2026).
        // UseAccessTrackingQueue: no longer a difference. On by default from 2.4 (DRT-5b, the owner's decision D-10).
        // EmbeddingCacheCapacity: no longer a difference. Since 2.4 the default is 512 unless the host chose strict
        // multi-tenant isolation, which the preset pairs with an explicit 512 (owner decision, 22 Nov 2026).
        ["MemoryOptions.SkipEscalationWhenOwnerHasNoRows"] = "Measured worse as a default: on an owner who holds rows it adds a probe per kind (PERF-R-01 13 to 16 queries, 14 Aug 2026). It pays off only where most owners hold nothing, as in a one-person assistant's early sessions; the owner kept it opt-in on 22 Nov 2026.",
    };

    [Fact]
    public void Every_difference_between_the_default_and_the_conversational_preset_has_a_written_reason()
    {
        var differences = Differences(new MemoryOptions(), MemoryOptions.CreateConversational(), "MemoryOptions").ToList();

        var unexplained = differences.Where(d => !Reasons.ContainsKey(d.Path)).Select(d => $"{d.Path}: default {d.Default}, preset {d.Preset}").ToList();
        var stale = Reasons.Keys.Except(differences.Select(d => d.Path), StringComparer.Ordinal).ToList();

        string.Join(" ; ", unexplained).Should().BeEmpty("every difference needs a reason in PresetDefaultDifferenceTests.Reasons");
        stale.Should().BeEmpty("a reason whose difference is gone is removed with it");
    }

    private sealed record Difference(string Path, string Default, string Preset);

    private static IEnumerable<Difference> Differences(object? left, object? right, string path)
    {
        if (left is null || right is null)
        {
            if (!Equals(left, right)) yield return new(path, Show(left), Show(right));
            yield break;
        }
        var type = left.GetType();
        if (IsLeaf(type))
        {
            if (!LeafEquals(left, right)) yield return new(path, Show(left), Show(right));
            yield break;
        }
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                     .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                     .OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            foreach (var difference in Differences(property.GetValue(left), property.GetValue(right), $"{path}.{property.Name}"))
                yield return difference;
        }
    }

    private static bool IsLeaf(Type type) =>
        type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(TimeSpan) ||
        type == typeof(DateTimeOffset) || type == typeof(Guid) || Nullable.GetUnderlyingType(type) is not null ||
        typeof(IEnumerable).IsAssignableFrom(type) || typeof(Delegate).IsAssignableFrom(type) || type == typeof(Type);

    private static bool LeafEquals(object left, object right) => left is IEnumerable a and not string && right is IEnumerable b
        ? a.Cast<object?>().Select(Show).SequenceEqual(b.Cast<object?>().Select(Show))
        : Equals(left, right);

    private static string Show(object? value) => value switch
    {
        null => "null",
        string s => $"\"{s}\"",
        IEnumerable list and not string => "[" + string.Join(", ", list.Cast<object?>().Select(Show)) + "]",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
    };
}
