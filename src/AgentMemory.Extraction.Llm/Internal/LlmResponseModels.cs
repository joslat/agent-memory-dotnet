using System.Text.Json.Serialization;

namespace AgentMemory.Extraction.Llm.Internal;

internal sealed class LlmEntityDto
{
    [JsonPropertyName("source_session")]
    public string? SourceSession { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("subtype")]
    public string? Subtype { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; } = 0.9;

    [JsonPropertyName("aliases")]
    public List<string> Aliases { get; set; } = new();
}

internal sealed class LlmFactDto
{
    [JsonPropertyName("source_session")]
    public string? SourceSession { get; set; }

    [JsonPropertyName("subject")]
    public string Subject { get; set; } = "";

    [JsonPropertyName("predicate")]
    public string Predicate { get; set; } = "";

    [JsonPropertyName("object")]
    public string Object { get; set; } = "";

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; } = 0.9;

    // Prospective memory. Null unless TemporalValidityMode.Extract asked for it, and null is the
    // meaningful value: live recall filters on these columns, so an invented expiry silently removes
    // a memory from every future answer.
    //
    // Read leniently (PeriodDateConverter): "2026-08" is a legitimate ISO-8601 month that the native
    // parser rejects, and rejecting it used to discard the WHOLE response. A month or year means the
    // start of the period for valid_from and the end of it for valid_until, always in UTC.
    [JsonPropertyName("valid_from")]
    [JsonConverter(typeof(PeriodStartDateConverter))]
    public DateTimeOffset? ValidFrom { get; set; }

    [JsonPropertyName("valid_until")]
    [JsonConverter(typeof(PeriodEndDateConverter))]
    public DateTimeOffset? ValidUntil { get; set; }

    // Which turn stated this. Null unless AssistantContentMode asked for it, and null is meaningful:
    // it leaves the request's own trust level applying, exactly as before this field existed.
    [JsonPropertyName("source_role")]
    public string? SourceRole { get; set; }

    // Which numbered turn stated it. Null unless ExtractionProvenanceMode.PerItem asked for it.
    [JsonPropertyName("source_turn")]
    public int? SourceTurn { get; set; }
}

internal sealed class LlmPreferenceDto
{
    [JsonPropertyName("source_session")]
    public string? SourceSession { get; set; }

    [JsonPropertyName("category")]
    public string Category { get; set; } = "";

    [JsonPropertyName("preference")]
    public string Preference { get; set; } = "";

    [JsonPropertyName("context")]
    public string? Context { get; set; }

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; } = 0.85;

    /// <inheritdoc cref="LlmFactDto.SourceRole"/>
    [JsonPropertyName("source_role")]
    public string? SourceRole { get; set; }

    /// <inheritdoc cref="LlmFactDto.SourceTurn"/>
    [JsonPropertyName("source_turn")]
    public int? SourceTurn { get; set; }
}

internal sealed class LlmRelationshipDto
{
    [JsonPropertyName("source_session")]
    public string? SourceSession { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("target")]
    public string Target { get; set; } = "";

    [JsonPropertyName("relation_type")]
    public string RelationType { get; set; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; } = 0.8;
}

internal sealed class LlmExtractionResponse
{
    [JsonPropertyName("processed_source_sessions")]
    public List<string>? ProcessedSourceSessions { get; set; }

    [JsonPropertyName("entities")]
    public List<LlmEntityDto> Entities { get; set; } = new();

    [JsonPropertyName("facts")]
    public List<LlmFactDto> Facts { get; set; } = new();

    [JsonPropertyName("preferences")]
    public List<LlmPreferenceDto> Preferences { get; set; } = new();

    [JsonPropertyName("relations")]
    public List<LlmRelationshipDto> Relations { get; set; } = new();
}
