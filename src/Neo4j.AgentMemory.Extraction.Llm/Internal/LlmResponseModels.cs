using System.Text.Json.Serialization;

namespace Neo4j.AgentMemory.Extraction.Llm.Internal;

internal sealed class LlmEntityDto
{
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
    [JsonPropertyName("subject")]
    public string Subject { get; set; } = "";

    [JsonPropertyName("predicate")]
    public string Predicate { get; set; } = "";

    [JsonPropertyName("object")]
    public string Object { get; set; } = "";

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; } = 0.9;
}

internal sealed class LlmPreferenceDto
{
    [JsonPropertyName("category")]
    public string Category { get; set; } = "";

    [JsonPropertyName("preference")]
    public string Preference { get; set; } = "";

    [JsonPropertyName("context")]
    public string? Context { get; set; }

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; } = 0.85;
}

internal sealed class LlmRelationshipDto
{
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
    [JsonPropertyName("entities")]
    public List<LlmEntityDto> Entities { get; set; } = new();

    [JsonPropertyName("facts")]
    public List<LlmFactDto> Facts { get; set; } = new();

    [JsonPropertyName("preferences")]
    public List<LlmPreferenceDto> Preferences { get; set; } = new();

    [JsonPropertyName("relations")]
    public List<LlmRelationshipDto> Relations { get; set; } = new();
}
