using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentMemory.Cli.Perf;

/// <summary>
/// The judged extraction-quality fixture: turns, the answer the model is scripted to give for each,
/// and what the pipeline should end up having learned.
/// </summary>
/// <remarks>
/// <para>
/// The write-path counterpart to <see cref="QualityFixture"/>. Ranks 2, 4 and 8 on the optimization
/// matrix all change extraction — skipping turns, merging four prompts into one, batching across a
/// window — and every one of them could quietly learn less. Nothing else detects that.
/// </para>
/// <para>
/// <b>What this does and does not measure.</b> The model is scripted, so this measures the
/// <em>pipeline</em>: given what the extractor returned, did resolution, confidence filtering and
/// persistence do the right thing. It deliberately does <em>not</em> measure whether a prompt is good
/// at reading a conversation — that is model-dependent, needs a real LLM, and belongs to the optional
/// comparative benchmark track. The distinction matters most for rank 4, which changes the prompt
/// itself and therefore needs both.
/// </para>
/// </remarks>
public sealed class ExtractionQualityFixture
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; }

    /// <summary>Owner id prefix. Each case gets its own owner — see <see cref="ExtractionCase"/>.</summary>
    [JsonPropertyName("ownerPrefix")] public string OwnerPrefix { get; init; } = "perf-xq";

    [JsonPropertyName("cases")] public List<ExtractionCase> Cases { get; init; } = new();

    public static ExtractionQualityFixture Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith("extraction-quality.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        return JsonSerializer.Deserialize<ExtractionQualityFixture>(stream, Json)
            ?? throw new InvalidOperationException("extraction-quality.json deserialized to null.");
    }

    /// <summary>
    /// The scripted-response rules this fixture needs, for wiring into the chat client.
    /// </summary>
    public IReadOnlyList<ScriptedChatClient.Rule> ScriptedRules() =>
        Cases.Select(c => new ScriptedChatClient.Rule(
                c.MatchOn,
                JsonSerializer.Serialize(c.ScriptedResponse, Json)))
            .ToList();
}

/// <summary>One judged extraction case.</summary>
public sealed class ExtractionCase
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("category")] public string Category { get; init; } = "";

    /// <summary>
    /// A distinctive phrase from this case's conversation. The scripted chat client uses it to return
    /// this case's answer rather than another's.
    /// </summary>
    [JsonPropertyName("matchOn")] public string MatchOn { get; init; } = "";

    [JsonPropertyName("messages")] public List<ExtractionMessage> Messages { get; init; } = new();

    /// <summary>What the model is scripted to reply. Shape must match what the LLM extractors parse.</summary>
    [JsonPropertyName("scriptedResponse")] public JsonElement ScriptedResponse { get; init; }

    [JsonPropertyName("expectEntities")] public List<string> ExpectEntities { get; init; } = new();
    [JsonPropertyName("expectFacts")] public List<ExpectedFact> ExpectFacts { get; init; } = new();
    [JsonPropertyName("expectPreferences")] public List<string> ExpectPreferences { get; init; } = new();
    [JsonPropertyName("expectRelationships")] public int? ExpectRelationships { get; init; }

    /// <summary>
    /// True when the correct behaviour is to learn nothing at all — an acknowledgement, a command, a
    /// pure transformation. These are the cases a salience gate (rank 2) must not get wrong in the
    /// other direction, and they are why a false-positive rate is reported separately.
    /// </summary>
    [JsonPropertyName("expectNothing")] public bool ExpectNothing { get; init; }

    /// <summary>Free-text rationale, for cases whose expectation is not self-evident.</summary>
    [JsonPropertyName("note")] public string? Note { get; init; }
}

public sealed class ExtractionMessage
{
    [JsonPropertyName("role")] public string Role { get; init; } = "user";
    [JsonPropertyName("content")] public string Content { get; init; } = "";
}

public sealed class ExpectedFact
{
    [JsonPropertyName("subject")] public string Subject { get; init; } = "";
    [JsonPropertyName("predicate")] public string Predicate { get; init; } = "";
    [JsonPropertyName("object")] public string Object { get; init; } = "";

    public override string ToString() => $"{Subject}|{Predicate}|{Object}";
}
