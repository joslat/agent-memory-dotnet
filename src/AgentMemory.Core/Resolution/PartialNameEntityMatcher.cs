using System.Diagnostics;
using System.Globalization;
using System.Text;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using Microsoft.Extensions.Logging;

namespace AgentMemory.Core.Resolution;

/// <summary>
/// Resolves a partial name to the single same-type entity whose name contains it as whole words, or
/// is contained by it: "Priya" ↔ "Priya Nair". Declines when more than one entity qualifies.
/// </summary>
/// <remarks>
/// Words are compared case-, accent- and punctuation-insensitively ("Núñez" = "Nunez", "J. Smith" keeps
/// only "smith": one-letter initials carry no identity). A name is compared through its canonical name
/// and aliases too, like the exact and fuzzy matchers. Identical word sets are not partial matches; they
/// are the exact or fuzzy matchers' business.
/// </remarks>
internal sealed class PartialNameEntityMatcher : IEntityMatcher
{
    private readonly EntityResolutionOptions _options;
    private readonly ILogger _logger;
    private readonly bool _recordAmbiguity;

    public PartialNameEntityMatcher(EntityResolutionOptions options, ILogger logger, bool recordAmbiguity = true)
    {
        _options = options;
        _logger = logger;
        _recordAmbiguity = recordAmbiguity;
    }

    public EntityMatchType MatchType => EntityMatchType.PartialName;

    public Task<EntityResolutionResult?> TryMatchAsync(
        ExtractedEntity candidate,
        IReadOnlyList<Entity> existingEntities,
        CancellationToken cancellationToken = default)
    {
        if (!AppliesTo(candidate.Type))
            return Task.FromResult<EntityResolutionResult?>(null);

        var mention = Words(candidate.Name);
        if (mention.Count == 0)
            return Task.FromResult<EntityResolutionResult?>(null);

        var matches = new List<Entity>();
        foreach (var existing in existingEntities)
        {
            // Same type only, even when type-strict filtering widened the candidate list: "Paris" the
            // city is not part of "Paris Hilton" the person.
            if (!string.Equals(existing.Type, candidate.Type, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!NamesOf(existing).Where(name => !IsRelational(name)).Any(name => IsPartial(mention, Words(name))))
                continue;
            if (matches.All(m => m.EntityId != existing.EntityId))
                matches.Add(existing);
        }

        if (matches.Count == 1)
        {
            return Task.FromResult<EntityResolutionResult?>(new EntityResolutionResult
            {
                ResolvedEntity = matches[0],
                MatchType = MatchType,
                Confidence = _options.PartialNameMatchConfidence,
            });
        }

        if (matches.Count > 1 && _recordAmbiguity)
        {
            // Never guess between people. Recorded so a host can ask "which Priya?" instead.
            _logger.LogDebug(
                "Partial name '{Name}' matches {Count} {Type} entities; declining to choose.",
                candidate.Name, matches.Count, candidate.Type);
            Activity.Current?.AddEvent(new ActivityEvent(
                "memory.resolve.partial_name_ambiguous",
                tags: new ActivityTagsCollection
                {
                    ["memory.resolve.entity_type"] = candidate.Type,
                    ["memory.resolve.match_count"] = matches.Count,
                }));
        }

        return Task.FromResult<EntityResolutionResult?>(null);
    }

    private bool AppliesTo(string? type) =>
        !string.IsNullOrWhiteSpace(type) &&
        _options.PartialNameMatchTypes.Any(t => string.Equals(t, type, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> NamesOf(Entity entity)
    {
        yield return entity.Name;
        if (!string.IsNullOrWhiteSpace(entity.CanonicalName))
            yield return entity.CanonicalName;
        foreach (var alias in entity.Aliases)
            yield return alias;
    }

    /// <summary>
    /// A name that describes someone through another person ("Priya's mom"): its words contain the other
    /// person's name, but it is not that person, so it is never a partial-name candidate.
    /// </summary>
    internal static bool IsRelational(string? name) =>
        name is not null &&
        (name.Contains("'s ", StringComparison.OrdinalIgnoreCase) || name.Contains("\u2019s ", StringComparison.OrdinalIgnoreCase) ||
         name.EndsWith("'s", StringComparison.OrdinalIgnoreCase) || name.EndsWith("\u2019s", StringComparison.OrdinalIgnoreCase));

    /// <summary>True when one word set is a proper subset of the other.</summary>
    internal static bool IsPartial(IReadOnlySet<string> a, IReadOnlySet<string> b) =>
        a.Count > 0 && b.Count > 0 && a.Count != b.Count &&
        (a.Count < b.Count ? a.IsSubsetOf(b) : b.IsSubsetOf(a));

    /// <summary>Lower-cased, accent-free words of two or more letters/digits.</summary>
    internal static IReadOnlySet<string> Words(string? name)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(name))
            return words;

        var current = new StringBuilder();
        foreach (var ch in name.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(char.ToLowerInvariant(ch));
                continue;
            }
            Flush();
        }
        Flush();
        return words;

        void Flush()
        {
            if (current.Length >= 2)
                words.Add(current.ToString());
            current.Clear();
        }
    }
}
