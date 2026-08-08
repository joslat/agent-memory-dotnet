using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentMemory.Core.Memory;

/// <summary>One canonical relation, its provenance, and the forms a question may use for it.</summary>
internal sealed class RelationVocabularyEntry
{
    /// <summary><c>event</c> or <c>state</c>.</summary>
    /// <remarks>
    /// The split is not decorative. schema.org models actions and has no vocabulary for the
    /// state/identity relations that carry 44.7% of this corpus's fact mass, so the two families are
    /// seeded from different sources and reviewed against different expectations.
    /// </remarks>
    [JsonPropertyName("family")]
    public string Family { get; init; } = "event";

    /// <summary>Where the relation came from, e.g. <c>schema.org:BuyAction</c>, <c>wikidata:P108</c>.</summary>
    /// <remarks>Licence provenance ships with the package; it is part of the artifact, not a footnote.</remarks>
    [JsonPropertyName("sources")]
    public IReadOnlyList<string> Sources { get; init; } = [];

    [JsonPropertyName("surfaceForms")]
    public IReadOnlyList<string> SurfaceForms { get; init; } = [];
}

internal sealed class RelationVocabularyDocument
{
    private static readonly Lazy<RelationVocabularyDocument> Cached = new(Parse);

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("retired")]
    public IReadOnlyList<string> Retired { get; init; } = [];

    [JsonPropertyName("canonical")]
    public IReadOnlyDictionary<string, RelationVocabularyEntry> Canonical { get; init; } =
        new Dictionary<string, RelationVocabularyEntry>(StringComparer.Ordinal);

    /// <summary>The reviewed vocabulary, parsed once.</summary>
    /// <remarks>
    /// Embedded rather than read from disk: this ships as a NuGet package, so a file dependency would
    /// be a deployment hazard and would add a startup I/O failure mode to a library. Parsed once into
    /// immutable structures, after which lookups are O(1) forever.
    /// </remarks>
    internal static RelationVocabularyDocument Load() => Cached.Value;

    private static RelationVocabularyDocument Parse()
    {
        const string ResourceName = "AgentMemory.Core.Memory.relation-vocabulary.json";
        var assembly = typeof(RelationVocabularyDocument).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"The relation vocabulary resource '{ResourceName}' is missing from {assembly.GetName().Name}. " +
                "It must be embedded, not shipped alongside.");

        var document = JsonSerializer.Deserialize<RelationVocabularyDocument>(
            stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("The relation vocabulary resource is empty.");

        if (document.Canonical.Count == 0)
            throw new InvalidOperationException("The relation vocabulary declares no relations.");

        return document;
    }
}
