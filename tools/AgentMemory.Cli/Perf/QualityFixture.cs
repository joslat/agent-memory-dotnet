using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentMemory.Cli.Perf;

/// <summary>
/// The judged retrieval-quality fixture: topics to seed, and cases with hand-labelled correct answers.
/// </summary>
/// <remarks>
/// <para>
/// Loaded from an embedded resource rather than from <c>performance/</c>, because the fixture is
/// <b>code</b>: it is the definition of what "correct retrieval" means, it is reviewed in the diff like
/// any other source, and it must version with the assembly that scores against it. (<c>performance/</c>
/// is untracked, so a fixture there could silently differ between machines and no comparison would mean
/// anything.)
/// </para>
/// <para>
/// The topics carry deliberately <b>disjoint vocabulary</b>. The harness embeds with a hashing
/// bag-of-words function, so shared words mean similar vectors — two topics that share terms would make
/// "relevant" and "irrelevant" indistinguishable and the fixture would measure nothing.
/// </para>
/// </remarks>
public sealed class QualityFixture
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; }
    [JsonPropertyName("ownerId")] public string OwnerId { get; init; } = "perf-quality-owner";
    [JsonPropertyName("sessionId")] public string SessionId { get; init; } = "perf-quality-session";
    [JsonPropertyName("topics")] public Dictionary<string, QualityTopic> Topics { get; init; } = new();
    [JsonPropertyName("cases")] public List<QualityCase> Cases { get; init; } = new();

    public static QualityFixture Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith("retrieval-quality.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        return JsonSerializer.Deserialize<QualityFixture>(stream, Json)
            ?? throw new InvalidOperationException("retrieval-quality.json deserialized to null.");
    }

    /// <summary>Every seeded id, used to verify the fixture and the seeder agree.</summary>
    public IEnumerable<string> AllSeededIds() =>
        Topics.Values.SelectMany(t =>
            t.Entities.Select(e => e.Id)
                .Concat(t.Facts.Select(f => f.Id))
                .Concat(t.Preferences.Select(p => p.Id)));
}

public sealed class QualityTopic
{
    [JsonPropertyName("entities")] public List<QualityEntity> Entities { get; init; } = new();
    [JsonPropertyName("facts")] public List<QualityFact> Facts { get; init; } = new();
    [JsonPropertyName("preferences")] public List<QualityPreference> Preferences { get; init; } = new();
}

public sealed class QualityEntity
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    /// <summary>Text the embedding is computed from — kept explicit so relevance is inspectable.</summary>
    [JsonPropertyName("text")] public string Text { get; init; } = "";
}

public sealed class QualityFact
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("subject")] public string Subject { get; init; } = "";
    [JsonPropertyName("predicate")] public string Predicate { get; init; } = "";
    [JsonPropertyName("object")] public string Object { get; init; } = "";
    [JsonPropertyName("text")] public string Text { get; init; } = "";
}

public sealed class QualityPreference
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("category")] public string Category { get; init; } = "";
    [JsonPropertyName("text")] public string Text { get; init; } = "";
}

/// <summary>One judged retrieval case.</summary>
public sealed class QualityCase
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("category")] public string Category { get; init; } = "";

    /// <summary>
    /// Which memory kind this case is scored against — <c>entity</c>, <c>fact</c>, or <c>preference</c>.
    /// Scoring is per-kind rather than over a merged list, because the sections are ranked independently
    /// and interleaving them would invent a cross-section ordering the system never produced.
    /// </summary>
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";

    [JsonPropertyName("query")] public string Query { get; init; } = "";

    /// <summary>Ids that must be retrieved. Order is not significant; rank is scored via MRR.</summary>
    [JsonPropertyName("relevant")] public List<string> Relevant { get; init; } = new();

    /// <summary>
    /// Ids that must NOT appear. Catches the failure a Recall@K of 1.0 hides: retrieving the right thing
    /// *and* a pile of unrelated memory alongside it.
    /// </summary>
    [JsonPropertyName("mustNotRetrieve")] public List<string> MustNotRetrieve { get; init; } = new();
}
